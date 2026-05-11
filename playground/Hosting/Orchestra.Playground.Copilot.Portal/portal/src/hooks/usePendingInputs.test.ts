import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { renderHook, act, waitFor } from '@testing-library/react';
import { usePendingInputs } from './usePendingInputs';
import { api } from '../api';
import type { PendingInputRecord } from '../types';

function makeRecord(overrides: Partial<PendingInputRecord> = {}): PendingInputRecord {
  return {
    orchestrationName: 'orch-a',
    runId: 'run-1',
    stepName: 'step-1',
    kind: 'Approval',
    prompt: 'Approve?',
    choices: ['approve', 'reject'],
    createdAt: '2025-05-09T12:00:00Z',
    ...overrides,
  };
}

describe('usePendingInputs', () => {
  let originalFetch: typeof globalThis.fetch;

  beforeEach(() => {
    originalFetch = globalThis.fetch;
    // The api wrapper has an in-memory GET cache that survives between renderHook
    // calls (it lives on a module-level Map). Clear it so each test sees its own
    // mocked response and we don't return stale data from a prior test.
    api.clearCache();
  });

  afterEach(() => {
    globalThis.fetch = originalFetch;
    vi.restoreAllMocks();
  });

  function mockFetchOnce(records: PendingInputRecord[]): void {
    globalThis.fetch = vi.fn(async () => ({
      ok: true,
      status: 200,
      headers: new Headers({ 'Content-Type': 'application/json' }),
      async json() { return records; },
      async text() { return JSON.stringify(records); },
    }) as unknown as Response) as unknown as typeof fetch;
  }

  function mockFetchError(): void {
    // Use a 4xx response — the api wrapper retries 5xx with exponential backoff,
    // which would exceed waitFor's deadline. 4xx returns to the caller immediately.
    globalThis.fetch = vi.fn(async () => ({
      ok: false,
      status: 404,
      headers: new Headers(),
      async json() { return { error: 'not found' }; },
      async text() { return 'not found'; },
    }) as unknown as Response) as unknown as typeof fetch;
  }

  it('starts in loading state and populates from /api/runs/pending', async () => {
    const seed = [
      makeRecord({ runId: 'r-1', stepName: 'review' }),
      makeRecord({ runId: 'r-2', stepName: 'review', orchestrationName: 'orch-b' }),
    ];
    mockFetchOnce(seed);

    const { result } = renderHook(() => usePendingInputs());

    expect(result.current.loading).toBe(true);

    await waitFor(() => expect(result.current.loading).toBe(false));
    expect(result.current.list).toHaveLength(2);
    expect(result.current.count).toBe(2);
    expect(result.current.error).toBeNull();
  });

  it('exposes the fetch error on failure', async () => {
    mockFetchError();
    const { result } = renderHook(() => usePendingInputs());
    await waitFor(() => expect(result.current.loading).toBe(false));
    expect(result.current.error).not.toBeNull();
    expect(result.current.list).toEqual([]);
  });

  it('applyAwaitingInput appends a new record and replaces by composite key', async () => {
    mockFetchOnce([]);
    const { result } = renderHook(() => usePendingInputs());
    await waitFor(() => expect(result.current.loading).toBe(false));

    act(() => {
      result.current.applyAwaitingInput(makeRecord({ runId: 'r-1' }));
    });
    expect(result.current.count).toBe(1);

    // Same composite key → replace, not append (idempotent).
    act(() => {
      result.current.applyAwaitingInput(makeRecord({ runId: 'r-1', prompt: 'Approve? (v2)' }));
    });
    expect(result.current.count).toBe(1);
    expect(result.current.list[0].prompt).toBe('Approve? (v2)');

    // Different runId → append.
    act(() => {
      result.current.applyAwaitingInput(makeRecord({ runId: 'r-2' }));
    });
    expect(result.current.count).toBe(2);
  });

  it('applyInputReceived removes the matching record', async () => {
    mockFetchOnce([
      makeRecord({ runId: 'r-1' }),
      makeRecord({ runId: 'r-2' }),
    ]);
    const { result } = renderHook(() => usePendingInputs());
    await waitFor(() => expect(result.current.count).toBe(2));

    act(() => {
      result.current.applyInputReceived({ orchestrationName: 'orch-a', runId: 'r-1', stepName: 'step-1' });
    });
    expect(result.current.count).toBe(1);
    expect(result.current.list[0].runId).toBe('r-2');

    // Removing a non-existent record is a no-op (preserves reference identity).
    const before = result.current.list;
    act(() => {
      result.current.applyInputReceived({ orchestrationName: 'no-such', runId: 'no-such', stepName: 'no-such' });
    });
    expect(result.current.list).toBe(before);
  });

  it('applyInputTimeout removes the matching record', async () => {
    mockFetchOnce([makeRecord({ runId: 'r-1' })]);
    const { result } = renderHook(() => usePendingInputs());
    await waitFor(() => expect(result.current.count).toBe(1));

    act(() => {
      result.current.applyInputTimeout({ orchestrationName: 'orch-a', runId: 'r-1', stepName: 'step-1' });
    });
    expect(result.current.count).toBe(0);
  });

  it('removeLocal mirrors applyInputReceived semantics (optimistic UI)', async () => {
    mockFetchOnce([makeRecord({ runId: 'r-1' })]);
    const { result } = renderHook(() => usePendingInputs());
    await waitFor(() => expect(result.current.count).toBe(1));

    act(() => {
      result.current.removeLocal('orch-a', 'r-1', 'step-1');
    });
    expect(result.current.count).toBe(0);
  });

  it('refresh re-fetches the canonical list', async () => {
    mockFetchOnce([makeRecord({ runId: 'r-1' })]);
    const { result } = renderHook(() => usePendingInputs());
    await waitFor(() => expect(result.current.count).toBe(1));

    // The next fetch returns a different list (e.g. server reconciled state).
    mockFetchOnce([
      makeRecord({ runId: 'r-2' }),
      makeRecord({ runId: 'r-3' }),
    ]);

    await act(async () => {
      await result.current.refresh();
    });

    expect(result.current.count).toBe(2);
    expect(result.current.list.map(r => r.runId)).toEqual(['r-2', 'r-3']);
  });
});
