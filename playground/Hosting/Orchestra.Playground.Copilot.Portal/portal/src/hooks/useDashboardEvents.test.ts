import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { renderHook, act } from '@testing-library/react';
import { useDashboardEvents } from './useDashboardEvents';

// ── EventSource mock ─────────────────────────────────────────────────────────

type Listener = (e: MessageEvent | Event) => void;

class MockEventSource {
  static instances: MockEventSource[] = [];
  url: string;
  readyState = 0; // CONNECTING
  listeners = new Map<string, Listener[]>();
  closed = false;
  static CONNECTING = 0;
  static OPEN = 1;
  static CLOSED = 2;

  constructor(url: string) {
    this.url = url;
    MockEventSource.instances.push(this);
  }

  addEventListener(type: string, listener: Listener) {
    const arr = this.listeners.get(type) ?? [];
    arr.push(listener);
    this.listeners.set(type, arr);
  }

  removeEventListener(type: string, listener: Listener) {
    const arr = this.listeners.get(type);
    if (!arr) return;
    this.listeners.set(type, arr.filter(l => l !== listener));
  }

  dispatch(type: string, data?: unknown) {
    const arr = this.listeners.get(type) ?? [];
    const event = data !== undefined
      ? ({ data: typeof data === 'string' ? data : JSON.stringify(data) } as MessageEvent)
      : (new Event(type) as Event);
    arr.forEach(l => l(event));
  }

  close() {
    this.closed = true;
    this.readyState = MockEventSource.CLOSED;
  }
}

describe('useDashboardEvents', () => {
  let originalEventSource: typeof EventSource;

  beforeEach(() => {
    originalEventSource = globalThis.EventSource;
    // @ts-expect-error — replacing with mock for tests
    globalThis.EventSource = MockEventSource;
    MockEventSource.instances = [];
    vi.useFakeTimers();
  });

  afterEach(() => {
    globalThis.EventSource = originalEventSource;
    vi.useRealTimers();
  });

  it('opens an EventSource against /api/events on mount', () => {
    renderHook(() => useDashboardEvents({}));
    expect(MockEventSource.instances).toHaveLength(1);
    expect(MockEventSource.instances[0].url).toBe('/api/events');
  });

  it('invokes onConnected when the connected event fires', () => {
    const onConnected = vi.fn();
    renderHook(() => useDashboardEvents({ onConnected }));
    act(() => {
      MockEventSource.instances[0].dispatch('connected', {});
    });
    expect(onConnected).toHaveBeenCalledTimes(1);
  });

  it('invokes onProfileActiveSetChanged with parsed payload', () => {
    const handler = vi.fn();
    renderHook(() =>
      useDashboardEvents({ onProfileActiveSetChanged: handler }),
    );
    act(() => {
      MockEventSource.instances[0].dispatch('profile-active-set-changed', {
        activatedOrchestrationIds: ['a', 'b'],
        deactivatedOrchestrationIds: [],
        trigger: 'schedule',
      });
    });
    expect(handler).toHaveBeenCalledWith({
      activatedOrchestrationIds: ['a', 'b'],
      deactivatedOrchestrationIds: [],
      trigger: 'schedule',
    });
  });

  it('invokes onExecutionStarted and onExecutionCompleted', () => {
    const started = vi.fn();
    const completed = vi.fn();
    renderHook(() =>
      useDashboardEvents({
        onExecutionStarted: started,
        onExecutionCompleted: completed,
      }),
    );

    act(() => {
      MockEventSource.instances[0].dispatch('execution-started', {
        executionId: 'e1',
        orchestrationId: 'o1',
        orchestrationName: 'Orch 1',
        triggeredBy: 'manual',
      });
    });
    expect(started).toHaveBeenCalledWith({
      executionId: 'e1',
      orchestrationId: 'o1',
      orchestrationName: 'Orch 1',
      triggeredBy: 'manual',
    });

    act(() => {
      MockEventSource.instances[0].dispatch('execution-completed', {
        executionId: 'e1',
        orchestrationId: 'o1',
        orchestrationName: 'Orch 1',
        status: 'Completed',
      });
    });
    expect(completed).toHaveBeenCalledWith({
      executionId: 'e1',
      orchestrationId: 'o1',
      orchestrationName: 'Orch 1',
      status: 'Completed',
    });
  });

  it('closes the EventSource on unmount', () => {
    const { unmount } = renderHook(() => useDashboardEvents({}));
    const es = MockEventSource.instances[0];
    expect(es.closed).toBe(false);
    unmount();
    expect(es.closed).toBe(true);
  });

  it('reconnects with backoff when the stream errors and the connection is CLOSED', () => {
    renderHook(() => useDashboardEvents({}));
    const first = MockEventSource.instances[0];

    // Simulate server-side close
    first.readyState = MockEventSource.CLOSED;
    act(() => {
      first.dispatch('error');
    });

    // Initial backoff is 1000ms — advance and verify a new EventSource is created
    act(() => {
      vi.advanceTimersByTime(1_000);
    });
    expect(MockEventSource.instances).toHaveLength(2);
    expect(MockEventSource.instances[1].url).toBe('/api/events');
  });

  it('ignores malformed event payloads without throwing', () => {
    const handler = vi.fn();
    renderHook(() =>
      useDashboardEvents({ onProfileActiveSetChanged: handler }),
    );
    expect(() => {
      act(() => {
        // Dispatch with garbage data to force JSON.parse failure
        const arr = MockEventSource.instances[0].listeners.get('profile-active-set-changed');
        arr?.forEach(l => l({ data: '{not json' } as MessageEvent));
      });
    }).not.toThrow();
    expect(handler).not.toHaveBeenCalled();
  });

  it('invokes onAwaitingInput with parsed PendingInputRecord', () => {
    const handler = vi.fn();
    renderHook(() => useDashboardEvents({ onAwaitingInput: handler }));
    act(() => {
      MockEventSource.instances[0].dispatch('awaiting-input', {
        orchestrationName: 'approval-deploy',
        runId: 'run-abc',
        stepName: 'review-deploy',
        kind: 'Approval',
        prompt: 'Approve deploy?',
        choices: ['approve', 'reject'],
        createdAt: '2025-05-09T12:34:56Z',
      });
    });
    expect(handler).toHaveBeenCalledTimes(1);
    expect(handler).toHaveBeenCalledWith({
      orchestrationName: 'approval-deploy',
      runId: 'run-abc',
      stepName: 'review-deploy',
      kind: 'Approval',
      prompt: 'Approve deploy?',
      choices: ['approve', 'reject'],
      createdAt: '2025-05-09T12:34:56Z',
    });
  });

  it('invokes onInputReceived with parsed payload', () => {
    const handler = vi.fn();
    renderHook(() => useDashboardEvents({ onInputReceived: handler }));
    act(() => {
      MockEventSource.instances[0].dispatch('input-received', {
        orchestrationName: 'approval-deploy',
        runId: 'run-abc',
        stepName: 'review-deploy',
        choice: 'approve',
        reply: null,
        respondedBy: 'alice',
        respondedAt: '2025-05-09T12:35:00Z',
      });
    });
    expect(handler).toHaveBeenCalledWith({
      orchestrationName: 'approval-deploy',
      runId: 'run-abc',
      stepName: 'review-deploy',
      choice: 'approve',
      reply: null,
      respondedBy: 'alice',
      respondedAt: '2025-05-09T12:35:00Z',
    });
  });

  it('invokes onInputTimeout with parsed payload', () => {
    const handler = vi.fn();
    renderHook(() => useDashboardEvents({ onInputTimeout: handler }));
    act(() => {
      MockEventSource.instances[0].dispatch('input-timeout', {
        orchestrationName: 'approval-deploy',
        runId: 'run-abc',
        stepName: 'review-deploy',
        onTimeout: 'Reject',
      });
    });
    expect(handler).toHaveBeenCalledWith({
      orchestrationName: 'approval-deploy',
      runId: 'run-abc',
      stepName: 'review-deploy',
      onTimeout: 'Reject',
    });
  });

  it('does not throw when HITL handlers are not provided', () => {
    renderHook(() => useDashboardEvents({}));
    expect(() => {
      act(() => {
        MockEventSource.instances[0].dispatch('awaiting-input', {
          orchestrationName: 'o', runId: 'r', stepName: 's', kind: 'Approval',
          prompt: 'p', createdAt: '2025-05-09T12:00:00Z',
        });
        MockEventSource.instances[0].dispatch('input-received', {
          orchestrationName: 'o', runId: 'r', stepName: 's', respondedAt: '2025-05-09T12:01:00Z',
        });
        MockEventSource.instances[0].dispatch('input-timeout', {
          orchestrationName: 'o', runId: 'r', stepName: 's', onTimeout: 'Reject',
        });
      });
    }).not.toThrow();
  });
});
