import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { renderHook, act, waitFor } from '@testing-library/react';
import { api } from '../api';
import { resetServerConfigForTests } from '../serverConfig';
import { useDashboardData, reconcileMissing, SSE_CONNECTED_POLL_MULTIPLIER, type DashboardPendingInputActions } from './useDashboardData';

// ── EventSource mock (same shape as useDashboardEvents.test) ─────────────────

type Listener = (e: MessageEvent | Event) => void;

class MockEventSource {
  static instances: MockEventSource[] = [];
  url: string;
  readyState = 0;
  listeners = new Map<string, Listener[]>();
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

  dispatch(type: string, data?: unknown) {
    const arr = this.listeners.get(type) ?? [];
    const event = data !== undefined
      ? ({ data: typeof data === 'string' ? data : JSON.stringify(data) } as MessageEvent)
      : (new Event(type) as Event);
    arr.forEach(l => l(event));
  }

  close() {
    this.readyState = MockEventSource.CLOSED;
  }
}

// ── fetch mock keyed by URL path ─────────────────────────────────────────────

const responses: Record<string, () => unknown> = {};
const calls: string[] = [];

function jsonResponse(body: unknown, status = 200): Response {
  return {
    ok: status >= 200 && status < 300,
    status,
    headers: new Headers({ 'Content-Type': 'application/json' }),
    async json() { return body; },
    async text() { return JSON.stringify(body); },
  } as unknown as Response;
}

function installFetch(): void {
  globalThis.fetch = vi.fn(async (input: RequestInfo | URL) => {
    const url = typeof input === 'string' ? input : input instanceof URL ? input.toString() : input.url;
    const path = url.split('?')[0];
    calls.push(url);
    const factory = responses[path];
    if (!factory) return jsonResponse({ error: `unmocked ${url}` }, 404);
    return jsonResponse(factory());
  }) as unknown as typeof fetch;
}

function countCalls(path: string): number {
  return calls.filter(u => u.split('?')[0] === path).length;
}

const pendingInputs: DashboardPendingInputActions = {
  refresh: vi.fn(() => {}),
  applyAwaitingInput: vi.fn(() => {}),
  applyInputReceived: vi.fn(() => {}),
  applyInputTimeout: vi.fn(() => {}),
};

const POLLING = { activeExecutionsMs: 1000, orchestrationsMs: 5000, historyMs: 5000, serverStatusMs: 5000 };

describe('useDashboardData', () => {
  let originalFetch: typeof globalThis.fetch;
  let originalEventSource: typeof EventSource;

  beforeEach(() => {
    originalFetch = globalThis.fetch;
    originalEventSource = globalThis.EventSource;
    // @ts-expect-error - replacing with mock for tests
    globalThis.EventSource = MockEventSource;
    MockEventSource.instances = [];
    calls.length = 0;
    for (const key of Object.keys(responses)) delete responses[key];
    responses['/api/orchestrations'] = () => ({ orchestrations: [{ id: 'o1', name: 'one', enabled: false, steps: [] }] });
    responses['/api/history'] = () => ({ runs: [] });
    responses['/api/active'] = () => ({ running: [], pending: [] });
    responses['/api/profiles'] = () => ({ count: 0, profiles: [] });
    responses['/api/config'] = () => ({ polling: POLLING, defaultModel: 'claude-opus-4.8' });
    responses['/api/status'] = () => ({ orchestrationCount: 1, activeTriggers: 0, runningExecutions: 0 });
    installFetch();
    api.clearCache();
    resetServerConfigForTests();
    vi.useFakeTimers({ shouldAdvanceTime: true });
  });

  afterEach(() => {
    vi.useRealTimers();
    globalThis.fetch = originalFetch;
    globalThis.EventSource = originalEventSource;
    vi.restoreAllMocks();
  });

  function render(historyUrl = '/api/history?limit=50') {
    return renderHook(({ url }) => useDashboardData({ historyUrl: url, pendingInputs }), { initialProps: { url: historyUrl } });
  }

  async function settle() {
    // Let the mount fetches (orchestrations, history/active, profiles, config, status) land.
    await waitFor(() => {
      expect(countCalls('/api/orchestrations')).toBeGreaterThanOrEqual(1);
      expect(countCalls('/api/active')).toBeGreaterThanOrEqual(1);
      expect(countCalls('/api/profiles')).toBeGreaterThanOrEqual(1);
      expect(countCalls('/api/status')).toBeGreaterThanOrEqual(1);
    });
  }

  it('loads the full snapshot on mount and exposes it', async () => {
    const { result } = render();
    await settle();

    await waitFor(() => expect(result.current.loading).toBe(false));
    await waitFor(() => expect(result.current.historyLoading).toBe(false));
    expect(result.current.orchestrations.map(o => o.id)).toEqual(['o1']);
    expect(result.current.serverStatus.orchestrationCount).toBe(1);
    expect(MockEventSource.instances.map(s => s.url)).toEqual(['/api/events']);
  });

  it('re-fetches history immediately when the history URL changes (but not on mount twice)', async () => {
    const { rerender } = render('/api/history?limit=50');
    await settle();
    const before = countCalls('/api/history');

    rerender({ url: '/api/history?limit=50&status=failed' });

    await waitFor(() => expect(countCalls('/api/history')).toBe(before + 1));
    const historyCalls = calls.filter(u => u.startsWith('/api/history?'));
    expect(historyCalls[historyCalls.length - 1]).toContain('status=failed');
  });

  it('refreshes Active + Recent on execution events and the cards on orchestrations-changed', async () => {
    render();
    await settle();
    const active = countCalls('/api/active');
    const history = countCalls('/api/history');
    const orchestrations = countCalls('/api/orchestrations');
    const stream = MockEventSource.instances[0];

    act(() => stream.dispatch('execution-started', { executionId: 'e1', orchestrationId: 'o1', orchestrationName: 'one', triggeredBy: 'manual' }));
    await waitFor(() => {
      expect(countCalls('/api/active')).toBe(active + 1);
      expect(countCalls('/api/history')).toBe(history + 1);
    });

    act(() => stream.dispatch('orchestrations-changed', { reason: 'registry:registered' }));
    await waitFor(() => expect(countCalls('/api/orchestrations')).toBe(orchestrations + 1));
  });

  it('does a full re-sync on connect and reports the stream as connected', async () => {
    const { result } = render();
    await settle();
    const orchestrations = countCalls('/api/orchestrations');
    expect(result.current.eventsConnected).toBe(false);

    act(() => MockEventSource.instances[0].dispatch('connected'));

    await waitFor(() => expect(result.current.eventsConnected).toBe(true));
    await waitFor(() => expect(countCalls('/api/orchestrations')).toBe(orchestrations + 1));
    expect(pendingInputs.refresh).toHaveBeenCalled();
  });

  it('polls orchestrations at the configured cadence while the stream is down, and slower once it is up', async () => {
    render();
    await settle();
    // /api/config applied POLLING; give the timers a moment to be (re)armed with it.
    await waitFor(() => expect(countCalls('/api/config')).toBe(1));
    const before = countCalls('/api/orchestrations');

    await act(async () => { await vi.advanceTimersByTimeAsync(POLLING.orchestrationsMs + 50); });
    await waitFor(() => expect(countCalls('/api/orchestrations')).toBe(before + 1));

    act(() => MockEventSource.instances[0].dispatch('connected'));
    // connected -> reloadAll fetches orchestrations once more; settle that first.
    await waitFor(() => expect(countCalls('/api/orchestrations')).toBe(before + 2));
    const afterConnect = countCalls('/api/orchestrations');

    await act(async () => { await vi.advanceTimersByTimeAsync(POLLING.orchestrationsMs + 50); });
    // One configured interval is not enough while pushes are flowing.
    expect(countCalls('/api/orchestrations')).toBe(afterConnect);

    await act(async () => { await vi.advanceTimersByTimeAsync(POLLING.orchestrationsMs * (SSE_CONNECTED_POLL_MULTIPLIER - 1) + 100); });
    await waitFor(() => expect(countCalls('/api/orchestrations')).toBe(afterConnect + 1));
  });

  it('does not poll /api/active while nothing runs and no trigger is armed', async () => {
    render();
    await settle();
    const before = countCalls('/api/active');

    await act(async () => { await vi.advanceTimersByTimeAsync(POLLING.activeExecutionsMs * 3 + 50); });

    expect(countCalls('/api/active')).toBe(before);
  });

  it('polls /api/active at the configured cadence while an execution runs, even when the stream is up', async () => {
    responses['/api/active'] = () => ({ running: [{ executionId: 'e1', orchestrationName: 'one' }], pending: [] });
    const { result } = render();
    await settle();
    await waitFor(() => expect(result.current.activeData.running).toHaveLength(1));
    act(() => MockEventSource.instances[0].dispatch('connected'));
    await waitFor(() => expect(result.current.eventsConnected).toBe(true));
    const before = countCalls('/api/active');

    await act(async () => { await vi.advanceTimersByTimeAsync(POLLING.activeExecutionsMs + 50); });

    await waitFor(() => expect(countCalls('/api/active')).toBeGreaterThan(before));
  });

  it('marks the stream disconnected on error so polling speeds back up', async () => {
    const { result } = render();
    await settle();
    act(() => MockEventSource.instances[0].dispatch('connected'));
    await waitFor(() => expect(result.current.eventsConnected).toBe(true));

    act(() => MockEventSource.instances[0].dispatch('error'));

    await waitFor(() => expect(result.current.eventsConnected).toBe(false));
  });
});

describe('reconcileMissing', () => {
  it('keeps a vanished execution for exactly one cycle', () => {
    const missing = new Set<string>();
    const prev = [{ executionId: 'a' }, { executionId: 'b' }];

    const first = reconcileMissing(prev, new Set(['b']), missing);
    expect(first.map(e => e.executionId)).toEqual(['a']);
    expect(missing.has('a')).toBe(true);

    const second = reconcileMissing(prev, new Set(['b']), missing);
    expect(second).toEqual([]);
  });

  it('forgets an execution that reappears', () => {
    const missing = new Set<string>(['a']);

    const retained = reconcileMissing([{ executionId: 'a' }], new Set(['a']), missing);

    expect(retained).toEqual([]);
    expect(missing.size).toBe(0);
  });

  it('drops tracker entries for executions no longer in the previous list', () => {
    const missing = new Set<string>(['stale']);

    reconcileMissing([{ executionId: 'a' }], new Set(['a']), missing);

    expect(missing.has('stale')).toBe(false);
  });

  it('ignores entries without an execution id', () => {
    const missing = new Set<string>();

    const retained = reconcileMissing([{ executionId: null }, { executionId: undefined }], new Set<string>(), missing);

    expect(retained).toEqual([]);
    expect(missing.size).toBe(0);
  });
});
