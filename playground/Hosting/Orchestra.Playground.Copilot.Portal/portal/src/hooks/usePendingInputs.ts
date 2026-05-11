import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { api } from '../api';
import type { PendingInputRecord } from '../types';

/**
 * Composite identifier used as the React key and dedup primary key for pending
 * input records. The combination of orchestration name + runId + step name is
 * unique across the system because a single run cannot have two concurrent
 * pauses on the same step.
 */
function recordKey(r: Pick<PendingInputRecord, 'orchestrationName' | 'runId' | 'stepName'>): string {
	return `${r.orchestrationName}|${r.runId}|${r.stepName}`;
}

export interface UsePendingInputsResult {
	/** All currently waiting records (newest last). Stable references between renders. */
	list: PendingInputRecord[];
	/** Convenience: <c>list.length</c>. */
	count: number;
	/** True until the initial GET /api/runs/pending resolves. */
	loading: boolean;
	/** Most recent error message from the initial fetch (null on success or while loading). */
	error: string | null;
	/** Re-fetches the canonical list from the server. */
	refresh: () => Promise<void>;
	/**
	 * Removes a record locally without contacting the server. Use after a successful
	 * <c>POST /respond</c> to keep the UI snappy — the matching <c>input-received</c>
	 * SSE event will arrive shortly after and is idempotent.
	 */
	removeLocal: (orchestrationName: string, runId: string, stepName: string) => void;
	/**
	 * Imperative event-application API. App.tsx forwards these from its existing
	 * <see cref="useDashboardEvents"/> subscription so we don't open a duplicate
	 * EventSource. All three are idempotent.
	 */
	applyAwaitingInput: (evt: PendingInputRecord) => void;
	applyInputReceived: (evt: { orchestrationName: string; runId: string; stepName: string }) => void;
	applyInputTimeout: (evt: { orchestrationName: string; runId: string; stepName: string }) => void;
}

/**
 * Tracks orchestration runs that are currently waiting for human input. Loads
 * the canonical list from <c>GET /api/runs/pending</c> on mount and is updated
 * by App.tsx forwarding dashboard SSE events through the imperative
 * <c>applyAwaitingInput</c> / <c>applyInputReceived</c> / <c>applyInputTimeout</c>
 * methods.
 *
 * Records are deduped by <c>orchestrationName|runId|stepName</c>, so a duplicate
 * <c>awaiting-input</c> event for the same wait simply replaces the prior copy
 * (same metadata) — never produces a duplicate row.
 */
export function usePendingInputs(): UsePendingInputsResult {
	const [list, setList] = useState<PendingInputRecord[]>([]);
	const [loading, setLoading] = useState(true);
	const [error, setError] = useState<string | null>(null);
	// Race-guard so a slow initial fetch can't overwrite SSE-applied state.
	const epochRef = useRef(0);

	const refresh = useCallback(async () => {
		const myEpoch = ++epochRef.current;
		setLoading(true);
		try {
			const records = await api.get<PendingInputRecord[]>('/api/runs/pending');
			if (myEpoch !== epochRef.current) return; // a newer fetch is in flight
			setList(records ?? []);
			setError(null);
		} catch (err) {
			if (myEpoch !== epochRef.current) return;
			setError(err instanceof Error ? err.message : String(err));
		} finally {
			if (myEpoch === epochRef.current) {
				setLoading(false);
			}
		}
	}, []);

	useEffect(() => {
		void refresh();
	}, [refresh]);

	const removeByKey = useCallback((orchestrationName: string, runId: string, stepName: string) => {
		const key = recordKey({ orchestrationName, runId, stepName });
		setList(prev => {
			const next = prev.filter(r => recordKey(r) !== key);
			// Avoid a wasted re-render if nothing changed (event for a record we never saw).
			return next.length === prev.length ? prev : next;
		});
	}, []);

	const removeLocal = removeByKey;

	const applyAwaitingInput = useCallback((evt: PendingInputRecord) => {
		setList(prev => {
			const key = recordKey(evt);
			const filtered = prev.filter(r => recordKey(r) !== key);
			return [...filtered, evt];
		});
	}, []);

	const applyInputReceived = useCallback(
		(evt: { orchestrationName: string; runId: string; stepName: string }) => {
			removeByKey(evt.orchestrationName, evt.runId, evt.stepName);
		},
		[removeByKey],
	);

	const applyInputTimeout = useCallback(
		(evt: { orchestrationName: string; runId: string; stepName: string }) => {
			removeByKey(evt.orchestrationName, evt.runId, evt.stepName);
		},
		[removeByKey],
	);

	const count = useMemo(() => list.length, [list]);
	return {
		list,
		count,
		loading,
		error,
		refresh,
		removeLocal,
		applyAwaitingInput,
		applyInputReceived,
		applyInputTimeout,
	};
}
