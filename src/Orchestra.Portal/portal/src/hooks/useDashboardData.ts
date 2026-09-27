import { useState, useEffect, useCallback, useRef, type Dispatch, type SetStateAction } from 'react';
import { api } from '../api';
import { DEFAULT_POLLING, setServerConfig, type ServerConfig } from '../serverConfig';
import type {
  ActiveData,
  HistoryListEntry,
  HistoryResponse,
  OrchestrationsResponse,
  Profile,
  RuntimeOrchestration,
  ServerStatus,
} from '../types';
import { useDashboardEvents } from './useDashboardEvents';
import { createDashboardEventHandlers } from '../dashboardEventHandlers';
import type { DashboardResyncActions } from '../dashboardEventHandlers';
import { useOnlineStatus, type OnlineStatus } from './useOnlineStatus';

/**
 * Pending-input plumbing the dashboard stream applies directly to state (no refetch).
 * Matches the subset of `usePendingInputs()` the SSE handlers need.
 */
export interface DashboardPendingInputActions {
  refresh: () => Promise<void> | void;
  applyAwaitingInput: DashboardResyncActions['applyAwaitingInput'];
  applyInputReceived: DashboardResyncActions['applyInputReceived'];
  applyInputTimeout: DashboardResyncActions['applyInputTimeout'];
}

export interface UseDashboardDataOptions {
  /** `/api/history?...` URL for the sidebar list; re-fetched immediately when it changes. */
  historyUrl: string;
  pendingInputs: DashboardPendingInputActions;
}

export interface DashboardData {
  orchestrations: RuntimeOrchestration[];
  history: HistoryListEntry[];
  setHistory: Dispatch<SetStateAction<HistoryListEntry[]>>;
  activeData: ActiveData;
  profiles: Profile[];
  serverStatus: ServerStatus;
  /** True until the first `/api/orchestrations` response. */
  loading: boolean;
  /** True until the first history/active response. */
  historyLoading: boolean;
  onlineStatus: OnlineStatus;
  /** Whether the `/api/events` push stream is currently connected. */
  eventsConnected: boolean;
  /** Full snapshot reload (orchestrations, then history + active). */
  loadData: () => Promise<void>;
  loadProfiles: () => Promise<void>;
  refreshActive: () => Promise<void>;
  refreshHistory: () => Promise<void>;
  refreshOrchestrations: () => Promise<void>;
}

/**
 * Multiplier applied to the configured `orchestrations` / `history` / profile polling
 * intervals while the dashboard push stream is connected. Those lists are refreshed by
 * `orchestrations-changed` / `execution-*` / `profiles-changed` events, so the timers become
 * a slow self-heal instead of the primary refresh path. `/api/active` keeps its configured
 * cadence: step progress inside a running execution has no dashboard event.
 */
export const SSE_CONNECTED_POLL_MULTIPLIER = 6;

/** Fallback profile refresh cadence when no push stream is connected. */
export const PROFILES_POLL_MS = 30_000;

const EMPTY_STATUS: ServerStatus = {
  outlook: null,
  orchestrationCount: 0,
  activeTriggers: 0,
  runningExecutions: 0,
  agentRuntime: null,
};

/**
 * Owns the dashboard's server-derived state: the orchestration cards, the Recent Executions
 * list, the Active list, profiles and the status-bar payload, plus the two ways they refresh -
 * the `/api/events` push stream (primary) and interval polling (fallback / progress).
 *
 * Extracted from `App.tsx` so the refresh policy is testable in isolation. Behavioural contract:
 * - a full snapshot loads on mount, on reconnect (`connected` event) and when the server
 *   becomes reachable again;
 * - execution start/stop events refresh Active + Recent immediately; registry/trigger changes
 *   refresh the cards; profile events refresh profiles;
 * - polling continues at the configured cadence while the push stream is down and slows by
 *   {@link SSE_CONNECTED_POLL_MULTIPLIER} while it is up (except `/api/active`);
 * - a running execution that vanishes from `/api/active` is kept for one extra poll so a
 *   transient gap does not flicker the card away.
 */
export function useDashboardData({ historyUrl, pendingInputs }: UseDashboardDataOptions): DashboardData {
  const [orchestrations, setOrchestrations] = useState<RuntimeOrchestration[]>([]);
  const [history, setHistory] = useState<HistoryListEntry[]>([]);
  const [activeData, setActiveData] = useState<ActiveData>({ running: [], pending: [] });
  const [profiles, setProfiles] = useState<Profile[]>([]);
  const [serverStatus, setServerStatus] = useState<ServerStatus>(EMPTY_STATUS);
  const [loading, setLoading] = useState(true);
  const [historyLoading, setHistoryLoading] = useState(true);
  const [pollingConfig, setPollingConfig] = useState<ServerConfig['polling']>(DEFAULT_POLLING);
  const [eventsConnected, setEventsConnected] = useState(false);

  const historyUrlRef = useRef(historyUrl);
  useEffect(() => { historyUrlRef.current = historyUrl; }, [historyUrl]);

  const onlineStatus = useOnlineStatus();
  const serverReachableRef = useRef(true);
  useEffect(() => { serverReachableRef.current = onlineStatus.isServerReachable; }, [onlineStatus.isServerReachable]);

  // Executions seen missing from /api/active once; dropped on the second miss.
  const missingRunningIdsRef = useRef<Set<string>>(new Set());

  const pendingInputsRef = useRef(pendingInputs);
  useEffect(() => { pendingInputsRef.current = pendingInputs; }, [pendingInputs]);

  // ── Loaders ───────────────────────────────────────────────────────────────

  const loadData = useCallback(async () => {
    // Orchestrations first (fast) so the list appears immediately, then history + active.
    try {
      const orchData = await api.get<OrchestrationsResponse>('/api/orchestrations');
      setOrchestrations(orchData.orchestrations || []);
    } catch (err) {
      console.error('Failed to load orchestrations:', err);
    } finally {
      setLoading(false);
    }

    try {
      const [histData, activeDataResult] = await Promise.all([
        api.get<HistoryResponse>(historyUrlRef.current),
        api.get<ActiveData>('/api/active'),
      ]);
      setHistory(histData.runs || []);
      setActiveData(activeDataResult || { running: [], pending: [] });
      missingRunningIdsRef.current.clear();
    } catch (err) {
      console.error('Failed to load history/active data:', err);
    } finally {
      setHistoryLoading(false);
    }
  }, []);

  const loadProfiles = useCallback(async () => {
    try {
      const data = await api.get<{ count: number; profiles: Profile[] }>('/api/profiles');
      setProfiles(data.profiles || []);
    } catch (err) {
      console.error('Failed to load profiles:', err);
    }
  }, []);

  const refreshActive = useCallback(async () => {
    if (!serverReachableRef.current) return;
    try {
      const data = await api.get<ActiveData>('/api/active');
      setActiveData(data || { running: [], pending: [] });
    } catch (err) {
      console.error('Failed to refresh active:', err);
    }
  }, []);

  const refreshHistory = useCallback(async () => {
    if (!serverReachableRef.current) return;
    try {
      const histData = await api.get<HistoryResponse>(historyUrlRef.current);
      setHistory(histData.runs || []);
    } catch (err) {
      console.error('Failed to refresh history:', err);
    }
  }, []);

  const refreshOrchestrations = useCallback(async () => {
    if (!serverReachableRef.current) return;
    try {
      const data = await api.get<OrchestrationsResponse>('/api/orchestrations');
      setOrchestrations(data.orchestrations || []);
    } catch (err) {
      console.error('Failed to refresh orchestrations:', err);
    }
  }, []);

  const fetchStatus = useCallback(async () => {
    if (!serverReachableRef.current) return;
    try {
      const status = await api.get<ServerStatus>('/api/status');
      setServerStatus({
        outlook: status.outlook || null,
        orchestrationCount: status.orchestrationCount || 0,
        activeTriggers: status.activeTriggers || 0,
        runningExecutions: status.runningExecutions || 0,
        agentRuntime: status.agentRuntime ?? null,
        agentRuntimes: status.agentRuntimes ?? undefined,
        defaultProvider: status.defaultProvider,
        providers: status.providers,
      });
    } catch (err) {
      console.error('Failed to fetch server status:', err);
    }
  }, []);

  // ── Initial loads ─────────────────────────────────────────────────────────

  useEffect(() => { void loadData(); }, [loadData]);
  useEffect(() => { void loadProfiles(); }, [loadProfiles]);

  useEffect(() => {
    (async () => {
      try {
        const config = await api.get<ServerConfig>('/api/config');
        setServerConfig(config);
        if (config?.polling) {
          setPollingConfig({ ...DEFAULT_POLLING, ...config.polling });
        }
      } catch (err) {
        console.error('Failed to load config, using defaults:', err);
      }
    })();
  }, []);

  // History filters changed: re-fetch now rather than on the next tick. The mount fetch
  // already ran through loadData(), so skip the first invocation.
  const initialHistoryFetchRef = useRef(true);
  useEffect(() => {
    if (initialHistoryFetchRef.current) {
      initialHistoryFetchRef.current = false;
      return;
    }
    void refreshHistory();
  }, [historyUrl, refreshHistory]);

  // Back online: full snapshot.
  useEffect(() => {
    if (onlineStatus.isOnline && onlineStatus.isServerReachable) {
      void loadData();
    }
  }, [onlineStatus.isOnline, onlineStatus.isServerReachable, loadData]);

  // ── Push stream ───────────────────────────────────────────────────────────

  useDashboardEvents({
    ...createDashboardEventHandlers({
      reloadAll: () => { void loadData(); },
      reloadProfiles: () => { void loadProfiles(); },
      refreshOrchestrations: () => { void refreshOrchestrations(); },
      refreshActive: () => { void refreshActive(); },
      refreshHistory: () => { void refreshHistory(); },
      refreshPendingInputs: () => { void pendingInputsRef.current.refresh(); },
      applyAwaitingInput: (evt) => pendingInputsRef.current.applyAwaitingInput(evt),
      applyInputReceived: (evt) => pendingInputsRef.current.applyInputReceived(evt),
      applyInputTimeout: (evt) => pendingInputsRef.current.applyInputTimeout(evt),
    }),
    onConnectionChanged: setEventsConnected,
  });

  // ── Polling ───────────────────────────────────────────────────────────────

  const slow = eventsConnected ? SSE_CONNECTED_POLL_MULTIPLIER : 1;

  // /api/active: step progress has no push event, so this keeps its configured cadence
  // whenever something can be running (an execution is live or a trigger is armed).
  const hasActiveOrPending = activeData.running.length > 0 || activeData.pending.length > 0;
  const hasEnabledTriggers = orchestrations.some(o => o.enabled);
  useEffect(() => {
    if (!hasActiveOrPending && !hasEnabledTriggers) return;

    const interval = setInterval(async () => {
      if (!serverReachableRef.current) return;
      try {
        const data = await api.get<ActiveData>('/api/active');
        const newRunning = data?.running || [];
        const newRunningIds = new Set(newRunning.map(r => r.executionId));

        setActiveData(prev => ({
          running: [...newRunning, ...reconcileMissing(prev.running, newRunningIds, missingRunningIdsRef.current)],
          pending: data?.pending || [],
        }));
      } catch (err) {
        console.error('Failed to refresh active:', err);
      }
    }, pollingConfig.activeExecutionsMs);
    return () => clearInterval(interval);
  }, [hasActiveOrPending, hasEnabledTriggers, pollingConfig.activeExecutionsMs]);

  // /api/orchestrations: pushed via orchestrations-changed; the timer is a slow self-heal.
  useEffect(() => {
    const interval = setInterval(() => { void refreshOrchestrations(); }, pollingConfig.orchestrationsMs * slow);
    return () => clearInterval(interval);
  }, [pollingConfig.orchestrationsMs, slow, refreshOrchestrations]);

  // /api/history: pushed via execution-started/completed; polled while something is live.
  const hasActiveInHistory = history.some(h => h.isActive);
  const hasRunning = activeData.running.length > 0;
  useEffect(() => {
    if (!hasEnabledTriggers && !hasActiveInHistory && !hasRunning) return;
    const interval = setInterval(() => { void refreshHistory(); }, pollingConfig.historyMs * slow);
    return () => clearInterval(interval);
  }, [hasEnabledTriggers, hasActiveInHistory, hasRunning, pollingConfig.historyMs, slow, refreshHistory]);

  // /api/profiles: pushed via profiles-changed / profile-active-set-changed; slow self-heal.
  useEffect(() => {
    const interval = setInterval(() => {
      if (!serverReachableRef.current) return;
      void loadProfiles();
    }, PROFILES_POLL_MS * slow);
    return () => clearInterval(interval);
  }, [loadProfiles, slow]);

  // /api/status: counts and agent runtimes have no push event; polled at the configured cadence.
  useEffect(() => {
    void fetchStatus();
    const interval = setInterval(() => { void fetchStatus(); }, pollingConfig.serverStatusMs);
    return () => clearInterval(interval);
  }, [pollingConfig.serverStatusMs, fetchStatus]);

  return {
    orchestrations,
    history,
    setHistory,
    activeData,
    profiles,
    serverStatus,
    loading,
    historyLoading,
    onlineStatus,
    eventsConnected,
    loadData,
    loadProfiles,
    refreshActive,
    refreshHistory,
    refreshOrchestrations,
  };
}

/**
 * Executions that were running but are absent from the latest `/api/active` response are
 * kept for exactly one poll cycle (tracked in `missing`), so a transient gap between the
 * engine finishing and history catching up does not flicker the card away. Exported for tests.
 */
export function reconcileMissing<T extends { executionId?: string | null }>(
  previousRunning: T[],
  currentIds: Set<string>,
  missing: Set<string>,
): T[] {
  const prevRunningIds = new Set(previousRunning.map(r => r.executionId).filter((id): id is string => !!id));
  const retained: T[] = [];

  for (const exec of previousRunning) {
    if (!exec.executionId) continue;
    if (currentIds.has(exec.executionId)) continue;
    if (!missing.has(exec.executionId)) {
      // First miss: keep it one more cycle.
      missing.add(exec.executionId);
      retained.push(exec);
    }
    // Second miss: confirmed gone.
  }

  // Reappeared, or no longer tracked at all: forget.
  for (const id of [...missing]) {
    if (currentIds.has(id) || !prevRunningIds.has(id)) {
      missing.delete(id);
    }
  }

  return retained;
}
