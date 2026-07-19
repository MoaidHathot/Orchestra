import React, { useState, useEffect, useCallback, useMemo, useRef } from 'react';
import './App.css';
import { api } from './api';
import { Icons } from './icons';
import { activeOrchestrationMatchesSearch, formatTime, isIncompleteExecution, profileFilterMatchesOrchestration, getMatchingProfiles, orchestrationMatchesProfileFilter, orchestrationMatchesSearch, buildRestoredStepStatusUpdates } from './utils';
import type { RunOrigin } from './runFilters';
import {
  type HistoryFilterState,
  buildFilterQueryString,
  loadFilterState,
  saveFilterState,
} from './runFilters';
import HistoryFilterSelector from './components/HistoryFilterSelector';
import HistoryRow from './components/HistoryRow';
import type { PortalStepStatus } from './utils';
import type {
  Orchestration,
  ActiveData,
  ServerStatus,
  ExecutionModalState,
  HookExecution,
  ModelInfo,
  StepEvent,
  Step,
  TraceData,
  RunContext,
  AuditLogEntry,
  Profile,
  ActorContext,
  ActorStream,
  StepActorStreams,
  ExecutionStateSnapshot,
} from './types';
import ActiveOrchestrationCard from './components/ActiveOrchestrationCard';
import type { CardExecution } from './components/ActiveOrchestrationCard';
import StatusBar from './components/StatusBar';
import OfflineBanner from './components/OfflineBanner';
import ViewerModal from './components/modals/ViewerModal';
import HistoryModal from './components/modals/HistoryModal';
import ActiveModal from './components/modals/ActiveModal';
import AddModal from './components/modals/AddModal';
import RunModal from './components/modals/RunModal';
import ExecutionModal from './components/modals/ExecutionModal';
import McpsModal from './components/modals/McpsModal';
import BuilderModal from './components/modals/BuilderModal';
import ProfilesModal from './components/modals/ProfilesModal';
import WaitingInputsModal from './components/modals/WaitingInputsModal';
import ProfileSelector from './components/ProfileSelector';
import { useKeyboardShortcuts } from './hooks/useKeyboardShortcuts';
import { useOnlineStatus } from './hooks/useOnlineStatus';
import { useDashboardEvents } from './hooks/useDashboardEvents';
import { createDashboardEventHandlers } from './dashboardEventHandlers';
import { usePendingInputs } from './hooks/usePendingInputs';

// ── API response types ──────────────────────────────────────────────────────

interface OrchestrationsResponse {
  orchestrations: RuntimeOrchestration[];
}

interface HistoryResponse {
  runs: HistoryListEntry[];
}

type ActiveStatusFilter = 'all' | 'running' | 'enabled' | 'disabled';

const ACTIVE_STATUS_FILTERS: { id: ActiveStatusFilter; label: string }[] = [
  { id: 'all', label: 'All' },
  { id: 'running', label: 'Running' },
  { id: 'enabled', label: 'Enabled' },
  { id: 'disabled', label: 'Disabled' },
];

/** Shape of a step in the detailed execution response from /api/history/:name/:runId */
interface ExecutionDetailStep {
  name: string;
  status: string;
  content?: string;
  startedAt?: string;
  completedAt?: string;
  actualModel?: string;
  selectedModel?: string;
  requestedModelInfo?: ModelInfo;
  selectedModelInfo?: ModelInfo;
  actualModelInfo?: ModelInfo;
  /** Agent provider this step was configured to run on. */
  configuredProvider?: string;
  /** Agent provider that actually ran this step. */
  actualProvider?: string;
  errorMessage?: string;
  savedFiles?: string[] | null;
  usage?: {
    inputTokens?: number;
    outputTokens?: number;
  };
  trace?: Omit<TraceData, 'toolCalls'> & {
    toolCalls?: Array<{
      toolName: string;
      mcpServer?: string;
      success?: boolean;
      startedAt?: string;
    }>;
  };
  /** For steps of type Orchestration: child run's execution id (clickable in history view). */
  childExecutionId?: string | null;
  /** For steps of type Orchestration: child orchestration name (used to load the child run). */
  childOrchestrationName?: string | null;
  /** For steps of type Orchestration: lowercase terminal status of the child run. */
  childStatus?: string | null;
}

interface ExecutionDetailsResponse {
  status: string;
  completionReason?: string;
  completedByStep?: string;
  isIncomplete?: boolean;
  finalContent?: string;
  savedFiles?: string[] | null;
  steps?: ExecutionDetailStep[];
  context?: RunContext | null;
  hookExecutions?: HookExecution[];
  retriedFromRunId?: string | null;
  retryMode?: string | null;
}

// ── Viewer / History / Add / Run modal state types ──────────────────────────

interface ViewerModalState {
  open: boolean;
  orchestration: Orchestration | null;
}

interface HistoryModalState {
  open: boolean;
}

interface AddModalState {
  open: boolean;
}

interface RunModalState {
  open: boolean;
  orchestration: Orchestration | null;
  /**
   * Pre-filled parameter values. Only set for the "Re-run with edits…" flow that
   * seeds the modal from a source run's stored parameters. Null/absent for a
   * fresh run keeps the existing empty-defaults behavior.
   */
  initialValues?: Record<string, string> | null;
  /**
   * When set, the modal acts as a retry: the source run's identifiers are
   * remembered so on submit we POST to the retry endpoint (preserving lineage)
   * instead of the fresh-run endpoint (which would orphan the lineage badge).
   */
  retryContext?: {
    orchestrationName: string;
    sourceRunId: string;
  } | null;
  /** Optional override for the modal title (e.g. "Re-run {name}"). */
  title?: string;
  /** Optional override for the submit button (e.g. "Re-run"). */
  submitLabel?: string;
}

interface McpsModalState {
  open: boolean;
}

interface ActiveModalState {
  open: boolean;
  data: ActiveData | null;
  loading: boolean;
}

/** Execution entry as it appears in the left-pane history list (may be enriched with active info). */
interface HistoryListEntry {
  runId: string;
  executionId?: string;
  orchestrationId?: string;
  orchestrationName: string;
  status?: string;
  completionReason?: string;
  completedByStep?: string;
  isActive?: boolean;
  isIncomplete?: boolean;
  startedAt?: string;
  durationSeconds?: number;
  parameters?: Record<string, unknown>;
  /** Server-classified origin token (manual/scheduler/loop/...). Falls back to client classification of triggeredBy. */
  origin?: RunOrigin;
  /** Free-form trigger string from the run record; useful for tooltips and as a classification fallback. */
  triggeredBy?: string;
  // ── Lineage (filled in by /api/history projection) ─────────────
  retriedFromRunId?: string | null;
  retryMode?: string | null;
  parentExecutionId?: string | null;
  parentStepName?: string | null;
  parentOrchestrationName?: string | null;
  rootExecutionId?: string | null;
  nestingDepth?: number;
}

// ── Helpers for SSE event handling ──────────────────────────────────────────

/** Extended orchestration type for runtime fields returned by the API but not in the base Orchestration type. */
interface RuntimeOrchestration extends Orchestration {
  status?: string;
  stepCount?: number;
  triggerType?: string;
  hasParameters?: boolean;
  lastExecutionStatus?: string;
  /**
   * Total number of recorded runs for this orchestration, derived by the API from the
   * persisted run store (with in-memory trigger counters as an overlay). Stable across
   * server restarts; populated even for manual orchestrations that have no trigger.
   */
  runCount?: number;
  /**
   * Most-recent run start time. ISO-8601 UTC. Same source-of-truth semantics as
   * <c>runCount</c>.
   */
  lastExecutionTime?: string;
}

type StepStatusValue = PortalStepStatus;

interface SSEEventData {
  stepName?: string;
  status?: string;
  completionReason?: string;
  completedByStep?: string;
  executionId?: string;
  chunk?: string;
  content?: string;
  contentPreview?: string;
  error?: string;
  message?: string;
  filePath?: string;
  savedFiles?: string[] | null;
  [key: string]: unknown;
}

interface FinalStepResultData {
  status?: string;
  contentPreview?: string;
  error?: string;
  savedFiles?: string[] | null;
}

// ── The App component ───────────────────────────────────────────────────────

function App(): React.JSX.Element {
  const [orchestrations, setOrchestrations] = useState<RuntimeOrchestration[]>([]);
  const [history, setHistory] = useState<HistoryListEntry[]>([]);
  const [searchQuery, setSearchQuery] = useState('');
  const [mainPaneSearchQuery, setMainPaneSearchQuery] = useState('');
  const [activeStatusFilter, setActiveStatusFilter] = useState<ActiveStatusFilter>('all');
  const [selectedOrchId, setSelectedOrchId] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [historyLoading, setHistoryLoading] = useState(true);
  const [activeData, setActiveData] = useState<ActiveData>({ running: [], pending: [] });
  const [sidebarOpen, setSidebarOpen] = useState(false);

  // Track run contexts per execution ID (from SSE run-context events)
  const runContextsRef = useRef<Map<string, RunContext>>(new Map());

  // Polling config from server (loaded once on init)
  const [pollingConfig, setPollingConfig] = useState({
    activeExecutionsMs: 1000,
    orchestrationsMs: 5000,
    historyMs: 5000,
    serverStatusMs: 5000,
  });

  // Modal states
  const [viewerModal, setViewerModal] = useState<ViewerModalState>({ open: false, orchestration: null });
  const [historyModal, setHistoryModal] = useState<HistoryModalState>({ open: false });
  const [addModal, setAddModal] = useState<AddModalState>({ open: false });
  const [runModal, setRunModal] = useState<RunModalState>({ open: false, orchestration: null });
  const [executionModal, setExecutionModal] = useState<ExecutionModalState>({
    open: false,
    orchestration: null,
    executionId: null,
    stepStatuses: {},
    stepEvents: {},
    stepResults: {},
    stepTraces: {},
    stepAuditLogs: {},
    stepActorStreams: {},
    streamingContent: '',
    finalResult: '',
    status: 'idle',
    errorMessage: null,
    completedByStep: null,
    runContext: null,
    hookExecutions: [],
    savedFiles: [],
    stepSavedFiles: {},
    retriedFromRunId: null,
    retryMode: null,
    historicalRun: null,
  });
  const eventSourceRef = useRef<EventSource | null>(null);
  const [mcpsModal, setMcpsModal] = useState<McpsModalState>({ open: false });
  const [activeModal, setActiveModal] = useState<ActiveModalState>({ open: false, data: null, loading: false });
  const [builderModal, setBuilderModal] = useState(false);
  const [profilesModal, setProfilesModal] = useState(false);
  const [waitingInputsModal, setWaitingInputsModal] = useState(false);

  // Tracks orchestration runs paused on human input. State is owned here so the
  // sidebar count badge, the active-card chip, and the WaitingInputsModal all see
  // the same canonical list and react to the same SSE events.
  const pendingInputs = usePendingInputs();

  // Profile data for filtering & membership display
  const [profiles, setProfiles] = useState<Profile[]>([]);
  const [profileFilter, setProfileFilter] = useState<string[]>([]); // empty = all, array of profile ids = multi-select
  const [profileDropdownOpen, setProfileDropdownOpen] = useState(false);
  const [mainPaneProfileFilter, setMainPaneProfileFilter] = useState<string[]>([]); // same logic for main pane

  // History filter state (persisted in localStorage)
  // The legacy "Hide incomplete" boolean is now part of the unified filter combo state,
  // but we still expose a top-level toggle for the empty-state recovery button at the
  // bottom of the panel.
  const [historyFilters, setHistoryFilters] = useState<HistoryFilterState>(() => loadFilterState());
  const updateHistoryFilters = useCallback((next: HistoryFilterState) => {
    setHistoryFilters(next);
    saveFilterState(next);
  }, []);
  const toggleHideIncomplete = useCallback(() => {
    setHistoryFilters(prev => {
      const next = { ...prev, hideIncomplete: !prev.hideIncomplete };
      saveFilterState(next);
      return next;
    });
  }, []);
  const hideIncomplete = historyFilters.hideIncomplete;

  // The query string is recomputed whenever the filter state changes; the ref allows
  // long-lived polling intervals to read the latest URL without tearing down on every
  // checkbox toggle.
  //
  // The sidebar fetches a generous slice (not the full set) so the in-place scroll list
  // covers the typical browsing case without forcing the user to open the "Show all
  // executions" modal. The modal at /api/history/all still serves deep history with
  // pagination. The endpoint returns lightweight row summaries so this size is cheap.
  const historyUrl = useMemo(
    () => `/api/history?limit=50${buildFilterQueryString(historyFilters)}`,
    [historyFilters],
  );
  const historyUrlRef = useRef(historyUrl);
  useEffect(() => { historyUrlRef.current = historyUrl; }, [historyUrl]);
  const [historyCollapsed, setHistoryCollapsed] = useState<boolean>(() => {
    const stored = localStorage.getItem('orchestra-history-collapsed');
    return stored === null ? true : stored === 'true';
  });
  const toggleHistoryCollapsed = useCallback(() => {
    setHistoryCollapsed(prev => {
      const next = !prev;
      localStorage.setItem('orchestra-history-collapsed', String(next));
      return next;
    });
  }, []);
  const [orchestrationsCollapsed, setOrchestrationsCollapsed] = useState<boolean>(() => {
    const stored = localStorage.getItem('orchestra-orchestrations-collapsed');
    return stored === 'true';
  });
  const toggleOrchestrationsCollapsed = useCallback(() => {
    setOrchestrationsCollapsed(prev => {
      const next = !prev;
      localStorage.setItem('orchestra-orchestrations-collapsed', String(next));
      return next;
    });
  }, []);

  // Status bar state
  const [serverStatus, setServerStatus] = useState<ServerStatus>({
    outlook: null,
    orchestrationCount: 0,
    activeTriggers: 0,
    runningExecutions: 0,
    agentRuntime: null,
  });

  // Online/offline tracking
  const onlineStatus = useOnlineStatus();

  // Ref so setInterval callbacks always see the latest reachability
  const serverReachableRef = useRef(true);
  useEffect(() => { serverReachableRef.current = onlineStatus.isServerReachable; }, [onlineStatus.isServerReachable]);

  // ── Load data ─────────────────────────────────────────────────────────────

  const loadData = useCallback(async () => {
    // Load orchestrations first (fast) so the list appears immediately,
    // then load history and active data in the background.
    try {
      const orchData = await api.get<OrchestrationsResponse>('/api/orchestrations');
      setOrchestrations(orchData.orchestrations || []);
    } catch (err) {
      console.error('Failed to load orchestrations:', err);
    } finally {
      setLoading(false);
    }

    // Load history and active data in parallel (may be slower due to cold index)
    try {
      const [histData, activeDataResult] = await Promise.all([
        api.get<HistoryResponse>(historyUrlRef.current),
        api.get<ActiveData>('/api/active'),
      ]);
      setHistory(histData.runs || []);
      setActiveData(activeDataResult || { running: [], pending: [] });
      // Full refresh: clear the missing-execution tracker to avoid stale retains
      missingRunningIdsRef.current.clear();
    } catch (err) {
      console.error('Failed to load history/active data:', err);
    } finally {
      setHistoryLoading(false);
    }
  }, []);

  useEffect(() => { loadData(); }, [loadData]);

  // Load polling config from server (once on init)
  useEffect(() => {
    (async () => {
      try {
        const config = await api.get<{ polling: typeof pollingConfig }>('/api/config');
        if (config?.polling) {
          setPollingConfig(config.polling);
        }
      } catch (err) {
        console.error('Failed to load config, using defaults:', err);
      }
    })();
  }, []); // eslint-disable-line react-hooks/exhaustive-deps

  // Load profiles for filter & membership display
  const loadProfiles = useCallback(async () => {
    try {
      const data = await api.get<{ count: number; profiles: Profile[] }>('/api/profiles');
      setProfiles(data.profiles || []);
    } catch (err) {
      console.error('Failed to load profiles:', err);
    }
  }, []);

  useEffect(() => { loadProfiles(); }, [loadProfiles]);

  // Lightweight single-endpoint reloads used by the SSE dashboard-events stream
  // to update individual sections in response to backend pushes (no polling needed).
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

  // Re-fetch history immediately when filters change (instead of waiting for the next poll
  // tick). The initial mount fetches via loadData(); skip that one to avoid a duplicate request.
  const initialHistoryFetchRef = useRef(true);
  useEffect(() => {
    if (initialHistoryFetchRef.current) {
      initialHistoryFetchRef.current = false;
      return;
    }
    refreshHistory();
  }, [historyUrl, refreshHistory]);

  const refreshOrchestrations = useCallback(async () => {
    if (!serverReachableRef.current) return;
    try {
      const data = await api.get<OrchestrationsResponse>('/api/orchestrations');
      setOrchestrations(data.orchestrations || []);
    } catch (err) {
      console.error('Failed to refresh orchestrations:', err);
    }
  }, []);

  // Subscribe to the backend dashboard-events SSE stream so the UI reflects changes
  // driven by schedulers / triggers immediately, without waiting for the next poll.
  // On (re)connect we do a FULL snapshot re-sync (including the Active + Recent lists),
  // because the backend keeps no event replay — see createDashboardEventHandlers.
  useDashboardEvents(createDashboardEventHandlers({
    reloadAll: () => { void loadData(); },
    reloadProfiles: () => { void loadProfiles(); },
    refreshOrchestrations: () => { void refreshOrchestrations(); },
    refreshActive: () => { void refreshActive(); },
    refreshHistory: () => { void refreshHistory(); },
    refreshPendingInputs: () => { void pendingInputs.refresh(); },
    applyAwaitingInput: pendingInputs.applyAwaitingInput,
    applyInputReceived: pendingInputs.applyInputReceived,
    applyInputTimeout: pendingInputs.applyInputTimeout,
  }));

  // Defense-in-depth polling fallback for /api/profiles.
  // SSE is the primary update mechanism, but if a `profile-active-set-changed` event is
  // ever missed (reconnect race, suppressed emission, transient backend hiccup), the
  // selector's IsActive flags would otherwise stay stale until the next manual refresh.
  // A low-frequency periodic reload is sufficient to self-heal without significant load.
  useEffect(() => {
    const interval = setInterval(() => {
      if (!serverReachableRef.current) return;
      loadProfiles();
    }, 30_000);
    return () => clearInterval(interval);
  }, [loadProfiles]);

  // Reload data when coming back online
  useEffect(() => {
    if (onlineStatus.isOnline && onlineStatus.isServerReachable) {
      loadData();
    }
  }, [onlineStatus.isOnline, onlineStatus.isServerReachable, loadData]);

  // Track which execution IDs have been seen as missing (for 2-poll confirmation)
  const missingRunningIdsRef = useRef<Set<string>>(new Set());

  // Auto-refresh active orchestrations when there are running or pending ones
  useEffect(() => {
    const hasActiveOrPending = activeData.running.length > 0 || activeData.pending.length > 0;
    const hasEnabledTriggers = orchestrations.some(o => o.enabled);

    if (hasActiveOrPending || hasEnabledTriggers) {
      const interval = setInterval(async () => {
        if (!serverReachableRef.current) return; // Skip when server is down
        try {
          const data = await api.get<ActiveData>('/api/active');
          const newRunning = data?.running || [];
          const newRunningIds = new Set(newRunning.map(r => r.executionId));

          // Reconcile: keep previously-running executions for one extra poll cycle
          // to avoid transient disappearances from the UI
          setActiveData(prev => {
            const prevRunningIds = new Set(prev.running.map(r => r.executionId));
            const retained: typeof prev.running = [];

            for (const exec of prev.running) {
              if (!exec.executionId) continue;
              if (!newRunningIds.has(exec.executionId)) {
                // This execution was running but is now missing from the server response
                if (!missingRunningIdsRef.current.has(exec.executionId)) {
                  // First time missing: retain it for one more cycle
                  missingRunningIdsRef.current.add(exec.executionId);
                  retained.push(exec);
                }
                // Second time missing: let it drop (confirmed gone)
              }
            }

            // Clear missing tracker for IDs that reappeared
            for (const id of missingRunningIdsRef.current) {
              if (newRunningIds.has(id)) {
                missingRunningIdsRef.current.delete(id);
              }
            }

            // Clean up old entries from the missing tracker
            for (const id of missingRunningIdsRef.current) {
              if (!prevRunningIds.has(id)) {
                missingRunningIdsRef.current.delete(id);
              }
            }

            return {
              running: [...newRunning, ...retained],
              pending: data?.pending || [],
            };
          });
        } catch (err) {
          console.error('Failed to refresh active:', err);
        }
      }, pollingConfig.activeExecutionsMs);
      return () => clearInterval(interval);
    }
  }, [activeData.running.length, activeData.pending.length, orchestrations, pollingConfig.activeExecutionsMs]);

  // Auto-refresh orchestrations list for external changes
  useEffect(() => {
    const interval = setInterval(async () => {
      if (!serverReachableRef.current) return; // Skip when server is down
      try {
        const data = await api.get<OrchestrationsResponse>('/api/orchestrations');
        setOrchestrations(data.orchestrations || []);
      } catch (err) {
        console.error('Failed to refresh orchestrations:', err);
      }
    }, pollingConfig.orchestrationsMs);
    return () => clearInterval(interval);
  }, [pollingConfig.orchestrationsMs]);

  // Auto-refresh history when there are enabled triggers, active executions, or running items
  useEffect(() => {
    const hasEnabledTriggers = orchestrations.some(o => o.enabled);
    const hasActiveInHistory = history.some(h => h.isActive);
    const hasRunning = activeData.running.length > 0;
    if (hasEnabledTriggers || hasActiveInHistory || hasRunning) {
      const interval = setInterval(async () => {
        if (!serverReachableRef.current) return; // Skip when server is down
        try {
          const histData = await api.get<HistoryResponse>(historyUrlRef.current);
          setHistory(histData.runs || []);
        } catch (err) {
          console.error('Failed to refresh history:', err);
        }
      }, pollingConfig.historyMs);
      return () => clearInterval(interval);
    }
  }, [orchestrations, history, activeData.running.length, pollingConfig.historyMs]);

  // Poll server status (including Outlook connection status) every 5 seconds
  useEffect(() => {
    const fetchStatus = async () => {
      if (!serverReachableRef.current) return; // Skip when server is down
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
    };

    fetchStatus();
    const interval = setInterval(fetchStatus, pollingConfig.serverStatusMs);
    return () => clearInterval(interval);
  }, [pollingConfig.serverStatusMs]);

  // ── Profile membership helper ──────────────────────────────────────────────

  /** Returns profiles that match a given orchestration based on filter rules. */
  const getProfilesForOrchestration = useCallback((orch: Orchestration): Profile[] => {
    return getMatchingProfiles(profiles, orch.id, orch.tags);
  }, [profiles]);

  /** Check if an orchestration (by ID) matches any of the given profile IDs. */
  const orchMatchesProfileFilter = useCallback((orchId: string, selectedProfileIds: string[]): boolean => {
    if (selectedProfileIds.length === 0) return true;
    const orch = orchestrations.find(o => o.id === orchId);
    if (!orch) return true; // if we can't find the orchestration, don't filter it out
    return orchestrationMatchesProfileFilter(orchId, orch.tags, selectedProfileIds, profiles);
  }, [profiles, orchestrations]);

  // ── Orchestration view: single-source categorized list ──
  // Uses orchestrations as the source of truth, overlays execution state from activeData.
  // No cross-referencing between two independently-polled endpoints.

  const orchestrationView = useMemo<{
    running: CardExecution[];
    enabled: CardExecution[];
    disabled: CardExecution[];
  }>(() => {
    // Filter orchestrations by profile if applicable
    const matchedOrchs = mainPaneProfileFilter.length > 0
      ? orchestrations.filter(o => orchMatchesProfileFilter(o.id, mainPaneProfileFilter))
      : orchestrations;

    // Build a set of running orchestration IDs from active data (unfiltered — always show running)
    const runningExecsByOrchId = new Map<string, CardExecution[]>();
    for (const exec of activeData.running) {
      const existing = runningExecsByOrchId.get(exec.orchestrationId) || [];
      existing.push(exec);
      runningExecsByOrchId.set(exec.orchestrationId, existing);
    }

    const running: CardExecution[] = [];
    const enabled: CardExecution[] = [];
    const disabled: CardExecution[] = [];
    const matchedOrchIds = new Set(matchedOrchs.map(o => o.id));

    // Build a lookup from pending data for trigger metadata (nextFireTime, etc.)
    const pendingByOrchId = new Map(
      activeData.pending.map(p => [p.orchestrationId, p])
    );

    // Always show ALL running executions regardless of profile filter
    for (const exec of activeData.running) {
      // Attach runContext if available (from SSE run-context events)
      const ctx = exec.executionId ? runContextsRef.current.get(exec.executionId) : undefined;
      if (ctx) {
        running.push({ ...exec, runContext: ctx });
      } else {
        running.push(exec);
      }
    }

    for (const orch of matchedOrchs) {
      const rt = orch as RuntimeOrchestration;
      const isEnabled = rt.enabled !== false; // default true for ManualTriggerConfig
      const hasTrigger = !!(rt.triggerType && rt.triggerType !== 'Manual');

      // Orchestrations without a trigger that aren't matched by any profile
      // should appear greyed out (disabled) when profiles exist
      const matchedByAnyProfile = profiles.length > 0
        ? getMatchingProfiles(profiles, orch.id, orch.tags).length > 0
        : true; // no profiles configured = no greying out

      // Merge trigger metadata from activeData.pending
      const pendingInfo = pendingByOrchId.get(orch.id);

      // Also build a card for the orchestration definition itself
      const cardExec: CardExecution = {
        orchestrationId: orch.id,
        orchestrationName: orch.name,
        stepCount: rt.stepCount || orch.steps?.length,
        triggeredBy: rt.triggerType || 'Manual',
        // Merge trigger metadata from activeData.pending
        nextFireTime: pendingInfo?.nextFireTime,
        // runCount and lastFireTime: prefer the orchestration record (sourced from the
        // persisted run store on the server) over the pending-trigger snapshot. Both
        // endpoints now overlay the same store-derived stats, but the orchestration
        // endpoint covers ALL orchestrations including manual / no-trigger ones whose
        // pendingInfo would otherwise be undefined and leave the card showing 0 / Never.
        lastFireTime: rt.lastExecutionTime ?? pendingInfo?.lastFireTime,
        runCount: rt.runCount ?? pendingInfo?.runCount,
        status: pendingInfo?.status,
        webhookUrl: pendingInfo?.webhookUrl,
      };

      if (!isEnabled || (!hasTrigger && !matchedByAnyProfile)) {
        disabled.push(cardExec);
      } else {
        enabled.push(cardExec);
      }
    }

    return { running, enabled, disabled };
  }, [orchestrations, activeData, mainPaneProfileFilter, orchMatchesProfileFilter]);

  const orchestrationById = useMemo(() => {
    return new Map(orchestrations.map(orch => [orch.id, orch]));
  }, [orchestrations]);

  const searchedOrchestrationView = useMemo(() => {
    const matchesSearch = (exec: CardExecution) => activeOrchestrationMatchesSearch(
      exec,
      orchestrationById.get(exec.orchestrationId),
      mainPaneSearchQuery,
    );

    return {
      running: orchestrationView.running.filter(matchesSearch),
      enabled: orchestrationView.enabled.filter(matchesSearch),
      disabled: orchestrationView.disabled.filter(matchesSearch),
    };
  }, [orchestrationView, orchestrationById, mainPaneSearchQuery]);

  const activeStatusCounts: Record<ActiveStatusFilter, number> = useMemo(() => ({
    all: searchedOrchestrationView.running.length + searchedOrchestrationView.enabled.length + searchedOrchestrationView.disabled.length,
    running: searchedOrchestrationView.running.length,
    enabled: searchedOrchestrationView.enabled.length,
    disabled: searchedOrchestrationView.disabled.length,
  }), [searchedOrchestrationView]);

  const filteredOrchestrationView = useMemo(() => ({
    running: activeStatusFilter === 'all' || activeStatusFilter === 'running'
      ? searchedOrchestrationView.running
      : [],
    enabled: activeStatusFilter === 'all' || activeStatusFilter === 'enabled'
      ? searchedOrchestrationView.enabled
      : [],
    disabled: activeStatusFilter === 'all' || activeStatusFilter === 'disabled'
      ? searchedOrchestrationView.disabled
      : [],
  }), [activeStatusFilter, searchedOrchestrationView]);

  /**
   * Set of runIds that currently have at least one pending HITL wait. Used to
   * stamp the "Waiting" chip on running cards. <c>runId === executionId</c> for
   * active runs, so a card whose <c>executionId</c> is in this set is paused.
   */
  const awaitingRunIds = useMemo(() => {
    return new Set(pendingInputs.list.map(r => r.runId));
  }, [pendingInputs.list]);

  const hasActiveOrchestrationFilters = mainPaneSearchQuery.trim().length > 0
    || mainPaneProfileFilter.length > 0
    || activeStatusFilter !== 'all';

  // ── Filtered / enabled orchestrations ─────────────────────────────────────

  const filteredOrchestrations = useMemo(() => {
    let result = orchestrations;

    // Text search filter (searches name, description, trigger type, step names, and tags)
    if (searchQuery) {
      result = result.filter(o => orchestrationMatchesSearch(o, searchQuery));
    }

    // Multi-profile filter (union: orchestration matches if ANY selected profile includes it)
    if (profileFilter.length > 0) {
      result = result.filter(o => orchestrationMatchesProfileFilter(o.id, o.tags, profileFilter, profiles));
    }

    return result;
  }, [orchestrations, searchQuery, profileFilter, profiles]);

  const enabledOrchestrations = useMemo(() =>
    filteredOrchestrations.filter(o => o.enabled !== false),
    [filteredOrchestrations]
  );

  // Filter history to optionally hide incomplete/early-exit executions
  const filteredHistory = useMemo(() => {
    if (!hideIncomplete) return history;
    return history.filter(exec => !isIncompleteExecution(exec));
  }, [history, hideIncomplete]);

  // ── SSE helper factories ──────────────────────────────────────────────────
  // Both runOrchestration and attachToExecution share identical SSE wiring.
  // We extract the helpers into a factory to avoid duplication.

  function wireEventSource(
    eventSource: EventSource,
    initialStatuses: Record<string, StepStatusValue>,
    knownExecutionId?: string,
  ): void {
    // Track state locally for batching updates
    const stepEvents: Record<string, StepEvent[]> = {};
    const stepStatuses: Record<string, string> = { ...initialStatuses };
    const stepResults: Record<string, string> = {};
    const stepTraces: Record<string, TraceData> = {};
    const stepAuditLogs: Record<string, AuditLogEntry[]> = {};
    const hookExecutions: HookExecution[] = [];
    const stepSavedFiles: Record<string, string[]> = {};
    const savedFiles: string[] = [];
    let streamingContent = '';
    let finalResult = '';
    // Mutable tracker for execution ID (may be set later by execution-started event)
    let trackedExecutionId = knownExecutionId;
    // Accumulate reasoning content per step
    const reasoningAccumulators: Record<string, string> = {};

    // ── Actor-keyed streaming buffers (Phase 1: backend now stamps every event
    // with an `actor`; older Hosts won't, so we maintain a per-step temporal-
    // scope stack of currently-active sub-agent toolCallIds as a fallback). ──
    const stepActorStreams: Record<string, StepActorStreams> = {};
    /** Per-step stack of active sub-agent toolCallIds (innermost on top). */
    const subagentScopeByStep: Record<string, string[]> = {};
    /** Per-step lookup: toolCallId → ActorContext (filled on subagent-started). */
    const subagentActorByToolCallId: Record<string, Record<string, ActorContext>> = {};

    const ensureMainStream = (stepName: string, startedAt?: string): ActorStream => {
      if (!stepActorStreams[stepName]) {
        stepActorStreams[stepName] = {
          main: {
            key: 'main',
            actor: null,
            content: '',
            reasoning: '',
            events: [],
            // Prefer the server-supplied timestamp from the event payload so a late-attaching
            // SSE client (replay path) sees the actual step start time. Without it, the elapsed
            // counter resets to zero every time the user opens the execution view.
            startedAt: startedAt ?? new Date().toISOString(),
            status: 'running',
          },
          subagents: [],
        };
      } else if (startedAt && stepActorStreams[stepName].main.startedAt > startedAt) {
        // The bucket was created lazily by a delta event before step-started arrived; fix it
        // up with the authoritative timestamp now that we have one.
        stepActorStreams[stepName].main.startedAt = startedAt;
      }
      return stepActorStreams[stepName].main;
    };

    const ensureSubagentStream = (stepName: string, actor: ActorContext, startedAt?: string): ActorStream => {
      const bucket = stepActorStreams[stepName] ?? (() => {
        ensureMainStream(stepName);
        return stepActorStreams[stepName];
      })();
      let stream = bucket.subagents.find(s => s.key === actor.toolCallId);
      if (!stream) {
        stream = {
          key: actor.toolCallId,
          actor,
          content: '',
          reasoning: '',
          events: [],
          // See ensureMainStream — server-supplied timestamp wins so replays render the
          // actual elapsed duration instead of resetting to zero on each open.
          startedAt: startedAt ?? new Date().toISOString(),
          status: 'running',
        };
        bucket.subagents.push(stream);
      }
      return stream;
    };

    /**
     * Resolves the right actor stream for an event:
     *  1. If the wire payload carries `actor`, honor it (most precise).
     *  2. Otherwise fall back to the per-step temporal-scope stack
     *     (back-compat for older Hosts that don't stamp `actor`).
     *  3. If no sub-agent is active, route to the main stream.
     */
    const resolveActorStream = (
      stepName: string,
      wireActor: ActorContext | undefined,
    ): ActorStream => {
      if (wireActor) {
        return ensureSubagentStream(stepName, wireActor);
      }
      const scope = subagentScopeByStep[stepName];
      if (scope && scope.length > 0) {
        const top = scope[scope.length - 1];
        const actor = subagentActorByToolCallId[stepName]?.[top];
        if (actor) {
          return ensureSubagentStream(stepName, actor);
        }
      }
      return ensureMainStream(stepName);
    };

    const flushActorStreams = () => {
      setExecutionModal(prev => ({
        ...prev,
        stepActorStreams: { ...stepActorStreams },
      }));
    };

    const updateHookExecutions = () => {
      setExecutionModal(prev => ({
        ...prev,
        hookExecutions: [...hookExecutions],
      }));
    };

    const addSavedFile = (stepName: string | undefined, filePath: string | undefined) => {
      if (!stepName || !filePath) return;
      if (!stepSavedFiles[stepName]) {
        stepSavedFiles[stepName] = [];
      }
      if (!stepSavedFiles[stepName].includes(filePath)) {
        stepSavedFiles[stepName].push(filePath);
      }
      if (!savedFiles.includes(filePath)) {
        savedFiles.push(filePath);
      }
      setExecutionModal(prev => ({
        ...prev,
        savedFiles: [...savedFiles],
        stepSavedFiles: { ...stepSavedFiles },
      }));
    };

    // ---- local helpers ----

    const addStepEvent = (stepName: string | undefined, type: string, data: Record<string, unknown>) => {
      if (!stepName) return;
      if (!stepEvents[stepName]) {
        stepEvents[stepName] = [];
      }
      stepEvents[stepName].push({
        time: new Date().toLocaleTimeString(),
        timestamp: new Date().toISOString(),
        type,
        ...data,
      } as StepEvent);
      setExecutionModal(prev => ({
        ...prev,
        stepEvents: { ...stepEvents },
      }));
    };

    const updateStepResult = (stepName: string | undefined, content: string) => {
      if (!stepName) return;
      stepResults[stepName] = content;
      setExecutionModal(prev => ({
        ...prev,
        stepResults: { ...stepResults },
      }));
    };

    const updateStepTrace = (stepName: string | undefined, trace: TraceData) => {
      if (!stepName) return;
      stepTraces[stepName] = trace;
      setExecutionModal(prev => ({
        ...prev,
        stepTraces: { ...stepTraces },
      }));
    };

    const updateStepStatus = (stepName: string | undefined, status: string) => {
      if (!stepName) return;
      stepStatuses[stepName] = status;
      setExecutionModal(prev => ({
        ...prev,
        stepStatuses: { ...stepStatuses },
      }));
    };

    // ---- SSE listeners ----

    // execution-info (sent when attaching to a running execution)
    eventSource.addEventListener('execution-info', (e: MessageEvent) => {
      try {
        const data: SSEEventData = JSON.parse(e.data);
        if (data.status === 'Cancelling') {
          setExecutionModal(prev => ({ ...prev, status: 'cancelling' }));
        }
      } catch { /* ignore */ }
    });

    // execution-snapshot (sent as the FIRST frame of every /run and /attach response).
    //
    // The server's authoritative per-step state is folded into the snapshot in parallel
    // with the circular event-log replay, so a Portal user who reopens the modal mid-run
    // — even on a very long DAG where the earliest step-started/step-completed events
    // have rolled off the buffer — sees correctly colored nodes and per-step trace/output
    // immediately. Subsequent replay/live events for the same steps are idempotent: they
    // overwrite the same map keys, so the snapshot priming never conflicts with later
    // updates.
    eventSource.addEventListener('execution-snapshot', (e: MessageEvent) => {
      try {
        const snapshot = JSON.parse(e.data) as ExecutionStateSnapshot;
        if (!snapshot) return;

        // Hydrate the local mutable state used by subsequent handlers so they see the
        // primed values rather than the empty initial dictionaries.
        if (snapshot.steps && typeof snapshot.steps === 'object') {
          for (const [stepName, step] of Object.entries(snapshot.steps)) {
            if (!step) continue;
            stepStatuses[stepName] = step.status;
            if (typeof step.output === 'string' && step.output.length > 0) {
              stepResults[stepName] = step.output;
            } else if (typeof step.contentPreview === 'string' && step.contentPreview.length > 0 && !stepResults[stepName]) {
              stepResults[stepName] = step.contentPreview;
            }
            if (step.trace) {
              stepTraces[stepName] = step.trace as unknown as TraceData;
            }
            if (Array.isArray(step.savedFiles) && step.savedFiles.length > 0) {
              stepSavedFiles[stepName] = [...step.savedFiles];
            }
            if (Array.isArray(step.auditEntries) && step.auditEntries.length > 0) {
              stepAuditLogs[stepName] = step.auditEntries as unknown as AuditLogEntry[];
            }
          }
        }

        // Push the hydrated state to React in a single batched update.
        setExecutionModal(prev => ({
          ...prev,
          stepStatuses: { ...stepStatuses },
          stepResults: { ...stepResults },
          stepTraces: { ...stepTraces },
          stepSavedFiles: { ...stepSavedFiles },
          stepAuditLogs: { ...stepAuditLogs },
          ...(snapshot.runContext ? { runContext: snapshot.runContext as unknown as RunContext } : {}),
          ...(snapshot.status === 'Cancelling' ? { status: 'cancelling' as const } : {}),
        }));

        if (snapshot.executionId && !trackedExecutionId) {
          trackedExecutionId = snapshot.executionId;
        }
      } catch { /* ignore */ }
    });

    // replay-truncated (sent when the client's Last-Event-Id is older than the server's
    // replay buffer). The snapshot still gives us authoritative state for every step; this
    // banner just lets the user know that intermediate streaming events from before the
    // truncation point are not available to scroll back through.
    eventSource.addEventListener('replay-truncated', (e: MessageEvent) => {
      try {
        const data = JSON.parse(e.data) as { requestedLastEventId: number | null; resumeFromSequence: number };
        console.warn(
          `Orchestra SSE: replay truncated (requested ${data.requestedLastEventId}, resuming from ${data.resumeFromSequence}). ` +
          'Authoritative state from snapshot is still in use.',
        );
      } catch { /* ignore */ }
    });

    // run-context (sent when the run context is available)
    eventSource.addEventListener('run-context', (e: MessageEvent) => {
      try {
        const data = JSON.parse(e.data) as RunContext;
        setExecutionModal(prev => ({ ...prev, runContext: data }));
        // Also store in the per-execution map so cards can display it
        if (trackedExecutionId) {
          runContextsRef.current.set(trackedExecutionId, data);
        }
      } catch { /* ignore */ }
    });

    eventSource.addEventListener('hook-executed', (e: MessageEvent) => {
      try {
        const data = JSON.parse(e.data) as HookExecution;
        hookExecutions.push(data);
        updateHookExecutions();

        if (data.stepName) {
          addStepEvent(data.stepName, 'hook-executed', data as unknown as Record<string, unknown>);
        }
      } catch { /* ignore */ }
    });

    // step-started
    eventSource.addEventListener('step-started', (e: MessageEvent) => {
      try {
        const data: SSEEventData = JSON.parse(e.data);
        updateStepStatus(data.stepName, 'running');
        if (data.stepName) {
          // Pre-create the main stream so reasoning/content can stream into a
          // ready bucket even before the first delta. Server stamps `startedAt`
          // on the event so replay produces correct elapsed durations.
          ensureMainStream(data.stepName, typeof data.startedAt === 'string' ? data.startedAt : undefined);
          flushActorStreams();
        }
        addStepEvent(data.stepName, 'step-started', data as Record<string, unknown>);
      } catch { /* ignore */ }
    });

    // step-completed
    eventSource.addEventListener('step-completed', (e: MessageEvent) => {
      try {
        const data: SSEEventData = JSON.parse(e.data);
        updateStepStatus(data.stepName, 'completed');
        if (data.stepName && stepActorStreams[data.stepName]) {
          stepActorStreams[data.stepName].main.status = 'completed';
          // Use the server-supplied completedAt so late-attaching clients see the actual
          // completion time. Without this, replay rewrote the timestamp to "now" and the
          // elapsed counter reset every time the modal was opened.
          stepActorStreams[data.stepName].main.completedAt =
            typeof data.completedAt === 'string'
              ? data.completedAt
              : new Date().toISOString();
          flushActorStreams();
        }
        // Backfill stepResults from contentPreview for older Hosts that do not yet emit
        // full step-output immediately when non-streaming steps complete.
        if (data.stepName && typeof data.contentPreview === 'string' && data.contentPreview.length > 0) {
          if (!stepResults[data.stepName]) {
            updateStepResult(data.stepName, data.contentPreview);
          }
        }
        addStepEvent(data.stepName, 'step-completed', data as Record<string, unknown>);
      } catch { /* ignore */ }
    });

    // step-error
    eventSource.addEventListener('step-error', (e: MessageEvent) => {
      try {
        const data: SSEEventData = JSON.parse(e.data);
        updateStepStatus(data.stepName, 'failed');
        if (data.stepName && stepActorStreams[data.stepName]) {
          stepActorStreams[data.stepName].main.status = 'failed';
          stepActorStreams[data.stepName].main.completedAt =
            typeof data.completedAt === 'string'
              ? data.completedAt
              : new Date().toISOString();
          stepActorStreams[data.stepName].main.errorMessage = data.error ?? data.message;
          flushActorStreams();
        }
        addStepEvent(data.stepName, 'step-error', data as Record<string, unknown>);
      } catch { /* ignore */ }
    });

    // step-cancelled
    eventSource.addEventListener('step-cancelled', (e: MessageEvent) => {
      try {
        const data: SSEEventData = JSON.parse(e.data);
        updateStepStatus(data.stepName, 'cancelled');
        if (data.stepName && stepActorStreams[data.stepName]) {
          stepActorStreams[data.stepName].main.status = 'cancelled';
          stepActorStreams[data.stepName].main.completedAt =
            typeof data.completedAt === 'string'
              ? data.completedAt
              : new Date().toISOString();
          flushActorStreams();
        }
        addStepEvent(data.stepName, 'step-cancelled', data as Record<string, unknown>);
      } catch { /* ignore */ }
    });

    // step-skipped
    eventSource.addEventListener('step-skipped', (e: MessageEvent) => {
      try {
        const data: SSEEventData = JSON.parse(e.data);
        updateStepStatus(data.stepName, 'skipped');
        addStepEvent(data.stepName, 'step-skipped', data as Record<string, unknown>);
      } catch { /* ignore */ }
    });

    // step-trace
    eventSource.addEventListener('step-trace', (e: MessageEvent) => {
      try {
        const data: SSEEventData = JSON.parse(e.data);
        updateStepTrace(data.stepName, data as unknown as TraceData);
        // Forward the configured-vs-actual provider pair so the step detail can label it
        // live (the trace event is the first place the resolved provider is known).
        addStepEvent(data.stepName, 'step-trace', {
          hasTrace: true,
          configuredProvider: data.configuredProvider,
          actualProvider: data.actualProvider,
        });
      } catch { /* ignore */ }
    });

    // audit-log
    eventSource.addEventListener('audit-log', (e: MessageEvent) => {
      try {
        const data = JSON.parse(e.data) as { stepName: string } & AuditLogEntry;
        const { stepName, ...entry } = data;
        if (!stepName) return;
        if (!stepAuditLogs[stepName]) {
          stepAuditLogs[stepName] = [];
        }
        stepAuditLogs[stepName].push(entry);
        // Sort by sequence
        stepAuditLogs[stepName].sort((a, b) => a.sequence - b.sequence);
        setExecutionModal(prev => ({
          ...prev,
          stepAuditLogs: { ...stepAuditLogs },
        }));
      } catch { /* ignore */ }
    });

    // content-delta
    eventSource.addEventListener('content-delta', (e: MessageEvent) => {
      try {
        const data: SSEEventData = JSON.parse(e.data);
        if (data.chunk) {
          streamingContent += data.chunk;
          setExecutionModal(prev => ({
            ...prev,
            streamingContent,
          }));
        }
        if (data.stepName) {
          // Bucket the chunk into the right actor stream.
          const stream = resolveActorStream(data.stepName, data.actor as ActorContext | undefined);
          if (data.chunk) {
            stream.content += data.chunk;
            flushActorStreams();
          }
          addStepEvent(data.stepName, 'content-delta', data as Record<string, unknown>);
        }
      } catch { /* ignore */ }
    });

    // tool events — route into the actor stream that owns them.
    (['tool-started', 'tool-completed'] as const).forEach(eventType => {
      eventSource.addEventListener(eventType, (e: MessageEvent) => {
        try {
          const data: SSEEventData = JSON.parse(e.data);
          if (data.stepName) {
            const stream = resolveActorStream(data.stepName, data.actor as ActorContext | undefined);
            stream.events.push({
              time: new Date().toLocaleTimeString(),
              timestamp: new Date().toISOString(),
              type: eventType,
              ...data,
            } as StepEvent);
            flushActorStreams();
          }
          addStepEvent(data.stepName, eventType, data as Record<string, unknown>);
        } catch { /* ignore */ }
      });
    });

    // subagent events — drive the temporal-scope stack and lifecycle status.
    eventSource.addEventListener('subagent-started', (e: MessageEvent) => {
      try {
        const data = JSON.parse(e.data) as SSEEventData & {
          toolCallId?: string;
          agentName?: string;
          displayName?: string;
          description?: string;
          startedAt?: string;
        };
        if (data.stepName && data.toolCallId && data.agentName) {
          const actor: ActorContext = {
            agentName: data.agentName,
            displayName: data.displayName,
            toolCallId: data.toolCallId,
            depth: (subagentScopeByStep[data.stepName]?.length ?? 0) + 1,
          };
          // Push onto the per-step scope stack and remember actor by toolCallId
          // for the temporal-fallback path.
          (subagentScopeByStep[data.stepName] ??= []).push(data.toolCallId);
          (subagentActorByToolCallId[data.stepName] ??= {})[data.toolCallId] = actor;
          // Pre-create the stream so the UI can render an empty card immediately.
          // Use the server-supplied startedAt so replays show the correct elapsed time.
          ensureSubagentStream(data.stepName, actor, data.startedAt);
          flushActorStreams();
        }
        addStepEvent(data.stepName, 'subagent-started', data as Record<string, unknown>);
      } catch { /* ignore */ }
    });

    const handleSubagentEnd = (
      eventType: 'subagent-completed' | 'subagent-failed',
      data: SSEEventData & { toolCallId?: string; error?: string; completedAt?: string },
    ): void => {
      if (data.stepName && data.toolCallId) {
        const scope = subagentScopeByStep[data.stepName];
        if (scope) {
          const idx = scope.lastIndexOf(data.toolCallId);
          if (idx >= 0) scope.splice(idx, 1);
        }
        const bucket = stepActorStreams[data.stepName];
        const stream = bucket?.subagents.find(s => s.key === data.toolCallId);
        if (stream) {
          // Server-supplied completedAt wins so replays render the correct elapsed time
          // for sub-agents that completed before the user attached.
          stream.completedAt =
            typeof data.completedAt === 'string'
              ? data.completedAt
              : new Date().toISOString();
          stream.status = eventType === 'subagent-failed' ? 'failed' : 'completed';
          if (eventType === 'subagent-failed') {
            stream.errorMessage = data.error;
          }
          flushActorStreams();
        }
      }
      addStepEvent(data.stepName, eventType, data as Record<string, unknown>);
    };

    eventSource.addEventListener('subagent-completed', (e: MessageEvent) => {
      try {
        handleSubagentEnd('subagent-completed', JSON.parse(e.data));
      } catch { /* ignore */ }
    });

    eventSource.addEventListener('subagent-failed', (e: MessageEvent) => {
      try {
        handleSubagentEnd('subagent-failed', JSON.parse(e.data));
      } catch { /* ignore */ }
    });

    // subagent-selected and subagent-deselected: pass through (no stack change)
    (['subagent-selected', 'subagent-deselected'] as const).forEach(eventType => {
      eventSource.addEventListener(eventType, (e: MessageEvent) => {
        try {
          const data: SSEEventData = JSON.parse(e.data);
          addStepEvent(data.stepName, eventType, data as Record<string, unknown>);
        } catch { /* ignore */ }
      });
    });

    // session warning and info events
    (['session-warning', 'session-info'] as const).forEach(eventType => {
      eventSource.addEventListener(eventType, (e: MessageEvent) => {
        try {
          const data: SSEEventData = JSON.parse(e.data);
          addStepEvent(data.stepName, eventType, data as Record<string, unknown>);
        } catch { /* ignore */ }
      });
    });

    // MCP server lifecycle events
    (['mcp-servers-loaded', 'mcp-server-status-changed'] as const).forEach(eventType => {
      eventSource.addEventListener(eventType, (e: MessageEvent) => {
        try {
          const data: SSEEventData = JSON.parse(e.data);
          addStepEvent(data.stepName, eventType, data as Record<string, unknown>);
        } catch { /* ignore */ }
      });
    });

    // step-retry
    eventSource.addEventListener('step-retry', (e: MessageEvent) => {
      try {
        const data = JSON.parse(e.data) as SSEEventData & {
          attempt?: number;
          maxRetries?: number;
          delaySeconds?: number;
        };
        const attempt = data.attempt ?? '?';
        const maxRetries = data.maxRetries ?? '?';
        const error = data.error || data.message || 'Unknown error';
        const delaySeconds = data.delaySeconds ?? 0;
        const message = `[Retry] Attempt ${attempt}/${maxRetries}: ${error} (waiting ${delaySeconds}s)`;
        addStepEvent(data.stepName, 'step-retry', {
          ...data as Record<string, unknown>,
          content: message,
        });
      } catch { /* ignore */ }
    });

    // checkpoint-saved
    eventSource.addEventListener('checkpoint-saved', (e: MessageEvent) => {
      try {
        const data = JSON.parse(e.data) as SSEEventData & {
          completedSteps?: number;
          totalSteps?: number;
        };
        const stepName = data.stepName || 'unknown';
        const completedSteps = data.completedSteps ?? '?';
        const totalSteps = data.totalSteps ?? '?';
        const message = `[Checkpoint] Step '${stepName}' saved (${completedSteps}/${totalSteps})`;
        addStepEvent(data.stepName, 'checkpoint-saved', {
          ...data as Record<string, unknown>,
          content: message,
        });
      } catch { /* ignore */ }
    });

    // reasoning-delta — accumulate silently, don't flood the event list.
    // The full reasoning is available in the step trace after completion.
    eventSource.addEventListener('reasoning-delta', (e: MessageEvent) => {
      try {
        const data = JSON.parse(e.data) as SSEEventData & { chunk?: string };
        const step = data.stepName;
        if (step && data.chunk) {
          if (!reasoningAccumulators[step]) {
            reasoningAccumulators[step] = '';
          }
          reasoningAccumulators[step] += data.chunk;
          // Also bucket reasoning per actor so the modal can render a dim,
          // collapsed-by-default reasoning subsection per main/sub-agent.
          const stream = resolveActorStream(step, data.actor as ActorContext | undefined);
          stream.reasoning += data.chunk;
          flushActorStreams();
          // Don't call addStepEvent per delta — reasoning arrives at ~30ms intervals
          // and would produce hundreds of near-identical events.  The accumulated
          // reasoning is surfaced in the step trace panel instead.
        }
      } catch { /* ignore */ }
    });

    // step-output
    eventSource.addEventListener('step-output', (e: MessageEvent) => {
      try {
        const data: SSEEventData = JSON.parse(e.data);
        addStepEvent(data.stepName, 'step-output', data as Record<string, unknown>);
        if (data.content) {
          updateStepResult(data.stepName, data.content);
          finalResult = data.content;
          setExecutionModal(prev => ({ ...prev, finalResult }));
        }
      } catch { /* ignore */ }
    });

    // saved-file
    eventSource.addEventListener('saved-file', (e: MessageEvent) => {
      try {
        const data: SSEEventData = JSON.parse(e.data);
        addSavedFile(data.stepName, data.filePath);
        addStepEvent(data.stepName, 'saved-file', data as Record<string, unknown>);
      } catch { /* ignore */ }
    });

    // usage, loop-iteration, model-mismatch
    (['usage', 'loop-iteration', 'model-mismatch'] as const).forEach(eventType => {
      eventSource.addEventListener(eventType, (e: MessageEvent) => {
        try {
          const data: SSEEventData = JSON.parse(e.data);
          addStepEvent(data.stepName, eventType, data as Record<string, unknown>);
        } catch { /* ignore */ }
      });
    });

    // SDK 0.3.0 telemetry: auto-mode switches, system notifications, quota snapshots.
    // These flow through the same per-step event timeline so the ExecutionModal can
    // render them inline without bespoke routing.
    (['auto-mode-switch-requested', 'auto-mode-switch-completed', 'system-notification', 'quota-snapshot'] as const).forEach(eventType => {
      eventSource.addEventListener(eventType, (e: MessageEvent) => {
        try {
          const data: SSEEventData = JSON.parse(e.data);
          addStepEvent(data.stepName, eventType, data as Record<string, unknown>);
        } catch { /* ignore */ }
      });
    });

    // execution-started
    eventSource.addEventListener('execution-started', (e: MessageEvent) => {
      try {
        const data: SSEEventData = JSON.parse(e.data);
        if (data.executionId) {
          trackedExecutionId = data.executionId as string;
          const restoredStatuses = buildRestoredStepStatusUpdates(data.stepsRestored);
          Object.assign(stepStatuses, restoredStatuses);
          setExecutionModal(prev => ({
            ...prev,
            executionId: data.executionId as string,
            stepStatuses: { ...prev.stepStatuses, ...restoredStatuses },
            retriedFromRunId: (data.retriedFromRunId as string | undefined) ?? prev.retriedFromRunId ?? null,
            retryMode: (data.retryMode as string | undefined) ?? prev.retryMode ?? null,
          }));
          loadData();
        }
      } catch { /* ignore */ }
    });

    // status-changed
    eventSource.addEventListener('status-changed', (e: MessageEvent) => {
      try {
        const data: SSEEventData = JSON.parse(e.data);
        if (data.status === 'Cancelling') {
          setExecutionModal(prev => ({ ...prev, status: 'cancelling' }));
        }
        loadData();
      } catch { /* ignore */ }
    });

    // orchestration-done
    eventSource.addEventListener('orchestration-done', (e: MessageEvent) => {
      const data: SSEEventData = JSON.parse(e.data);

      // Determine orchestration-level modal status
      const isEarlyCompletion = data.status === 'Succeeded' && !!data.completionReason;
      const modalStatus = isEarlyCompletion
        ? 'completed_early'
        : data.status === 'Succeeded' ? 'success' : 'failed';

      // Update per-step statuses from the final results (handles NoAction, etc.)
      const statusMap: Record<string, string> = {
        'Succeeded': 'completed',
        'Failed': 'failed',
        'Cancelled': 'cancelled',
        'Skipped': 'skipped',
        'NoAction': 'noaction',
      };
      if (data.results) {
        const updatedStatuses: Record<string, string> = {};
        for (const [stepName, stepData] of Object.entries(data.results as Record<string, FinalStepResultData>)) {
          if (stepData.status) {
            const nextStatus = statusMap[stepData.status] || 'completed';
            updatedStatuses[stepName] = nextStatus === 'completed' && stepStatuses[stepName] === 'completed_restored'
              ? 'completed_restored'
              : nextStatus;
          }
          if (typeof stepData.contentPreview === 'string' && stepData.contentPreview.length > 0 && !stepResults[stepName]) {
            stepResults[stepName] = stepData.contentPreview;
          }
          if (stepData.savedFiles && stepData.savedFiles.length > 0) {
            stepSavedFiles[stepName] = [...stepData.savedFiles];
            for (const filePath of stepData.savedFiles) {
              if (!savedFiles.includes(filePath)) savedFiles.push(filePath);
            }
          }
        }
        const orchestrationSavedFiles = Array.isArray(data.savedFiles) ? data.savedFiles : [];
        for (const filePath of orchestrationSavedFiles) {
          if (!savedFiles.includes(filePath)) savedFiles.push(filePath);
        }
        // Mark the step that triggered early completion with a distinct status for DAG visualization
        if (data.completedByStep && updatedStatuses[data.completedByStep]) {
          updatedStatuses[data.completedByStep] = 'completed_early';
        }
        setExecutionModal(prev => ({
          ...prev,
          stepStatuses: { ...prev.stepStatuses, ...updatedStatuses },
          stepResults: { ...stepResults },
          savedFiles: [...savedFiles],
          stepSavedFiles: { ...stepSavedFiles },
          status: modalStatus,
          completedByStep: data.completedByStep || null,
        }));
      } else {
        setExecutionModal(prev => ({
          ...prev,
          status: modalStatus,
          completedByStep: data.completedByStep || null,
        }));
      }

      eventSource.close();
      eventSourceRef.current = null;
      loadData();
    });

    // orchestration-cancelled
    eventSource.addEventListener('orchestration-cancelled', () => {
      setExecutionModal(prev => ({ ...prev, status: 'cancelled' }));
      eventSource.close();
      eventSourceRef.current = null;
      loadData();
    });

    // orchestration-error
    eventSource.addEventListener('orchestration-error', (e: MessageEvent) => {
      try {
        const data: SSEEventData = JSON.parse(e.data);
        setExecutionModal(prev => ({
          ...prev,
          status: 'error',
          errorMessage: data.error || data.message || 'An error occurred during execution',
        }));
        eventSource.close();
        eventSourceRef.current = null;
        loadData();
      } catch { /* ignore */ }
    });

    // onerror
    eventSource.onerror = () => {
      if (eventSource.readyState === EventSource.CLOSED) {
        eventSource.close();
        eventSourceRef.current = null;
      } else {
        console.error('EventSource error');
        eventSource.close();
        eventSourceRef.current = null;
        setExecutionModal(prev => ({
          ...prev,
          status: 'error',
          errorMessage: 'Connection to server lost. The orchestration may still be running.',
        }));
      }
    };
  }

  // ── Build initial step statuses from an orchestration ─────────────────────

  function buildInitialStatuses(orchestration: Orchestration | undefined): Record<string, StepStatusValue> {
    const statuses: Record<string, StepStatusValue> = {};
    if (orchestration?.steps) {
      orchestration.steps.forEach((step: Step | string) => {
        const stepName = typeof step === 'string' ? step : step.name;
        statuses[stepName] = 'pending';
      });
    }
    return statuses;
  }

  // ── Run orchestration ─────────────────────────────────────────────────────

  const runOrchestration = async (id: string | undefined, params: Record<string, string> = {}): Promise<void> => {
    if (!id) return;

    // Close any existing EventSource before opening a new one
    if (eventSourceRef.current) {
      eventSourceRef.current.close();
      eventSourceRef.current = null;
    }

    const orchestration = orchestrations.find(o => o.id === id);
    const initialStatuses = buildInitialStatuses(orchestration);

    setExecutionModal({
      open: true,
      orchestration: orchestration || null,
      executionId: null,
      stepStatuses: initialStatuses,
      stepEvents: {},
      stepResults: {},
      stepTraces: {},
      stepAuditLogs: {},
      stepActorStreams: {},
      streamingContent: '',
      finalResult: '',
      status: 'running',
      errorMessage: null,
      completedByStep: null,
      runContext: null,
      hookExecutions: [],
      savedFiles: [],
      stepSavedFiles: {},
      retriedFromRunId: null,
      retryMode: null,
      historicalRun: null,
    });

    try {
      const queryParams = Object.keys(params).length > 0
        ? `?params=${encodeURIComponent(JSON.stringify(params))}`
        : '';
      const eventSource = new EventSource(`/api/orchestrations/${id}/run${queryParams}`);
      eventSourceRef.current = eventSource;
      wireEventSource(eventSource, initialStatuses);
    } catch (err) {
      console.error('Run error:', err);
      const message = err instanceof Error ? err.message : 'Failed to start orchestration';
      setExecutionModal(prev => ({
        ...prev,
        status: 'error',
        errorMessage: message,
      }));
    }
  };

  // ── Retry a historical execution (whole or from a specific step) ─────────

  const retryExecution = async (
    orchestrationName: string,
    sourceRunId: string,
    mode: 'failed' | 'all' | 'from-step',
    fromStep?: string,
    /**
     * Optional parameter overrides. Only honored for mode='all' (the server
     * rejects them for failed/from-step because those replay checkpointed
     * step outputs derived from the original parameter set). When supplied
     * and non-empty, the server records the run with retryMode='all-edited'.
     */
    paramsOverride?: Record<string, string> | null,
  ): Promise<void> => {
    if (!orchestrationName || !sourceRunId) return;

    // Close any existing EventSource before opening a new one
    if (eventSourceRef.current) {
      eventSourceRef.current.close();
      eventSourceRef.current = null;
    }

    const orchestration = orchestrations.find(o =>
      o.name === orchestrationName || o.id === orchestrationName,
    );
    const initialStatuses = buildInitialStatuses(orchestration);

    const hasParamsOverride = !!(paramsOverride && Object.keys(paramsOverride).length > 0);
    const recordedMode = mode === 'from-step'
      ? `from-step:${fromStep ?? ''}`
      : (mode === 'all' && hasParamsOverride ? 'all-edited' : mode);

    setExecutionModal({
      open: true,
      orchestration: orchestration || null,
      executionId: null,
      stepStatuses: initialStatuses,
      stepEvents: {},
      stepResults: {},
      stepTraces: {},
      stepAuditLogs: {},
      stepActorStreams: {},
      streamingContent: '',
      finalResult: '',
      status: 'running',
      errorMessage: null,
      completedByStep: null,
      runContext: null,
      hookExecutions: [],
      savedFiles: [],
      stepSavedFiles: {},
      retriedFromRunId: sourceRunId,
      retryMode: recordedMode,
      historicalRun: null,
    });

    try {
      const params = new URLSearchParams({ mode });
      if (mode === 'from-step' && fromStep) {
        params.set('step', fromStep);
      }
      if (hasParamsOverride) {
        // The server-side endpoint parses ?params=<URL-encoded JSON object>
        // identically to /api/orchestrations/{id}/run, so the encoded shape
        // matches what runOrchestration already produces.
        params.set('params', JSON.stringify(paramsOverride));
      }
      const url = `/api/history/${encodeURIComponent(orchestrationName)}/${encodeURIComponent(sourceRunId)}/retry?${params.toString()}`;
      const eventSource = new EventSource(url);
      eventSourceRef.current = eventSource;
      wireEventSource(eventSource, initialStatuses);
    } catch (err) {
      console.error('Retry error:', err);
      const message = err instanceof Error ? err.message : 'Failed to start retry';
      setExecutionModal(prev => ({
        ...prev,
        status: 'error',
        errorMessage: message,
      }));
    }
  };

  // ── Cancel running orchestration ──────────────────────────────────────────

  const cancelExecution = async (executionId: string | null, reason?: string): Promise<void> => {
    if (!executionId) return;
    try {
      setExecutionModal(prev => ({ ...prev, status: 'cancelling' }));
      // Send a structured cancel body so the run record attributes the cancel to the Portal
      // UI (instead of a generic "REST endpoint was hit"). `reason` is optional free-text the
      // user can supply when prompted; `source` is a fixed client-type label.
      await api.post(`/api/active/${executionId}/cancel`, {
        source: 'portal-ui',
        reason: reason && reason.trim().length > 0 ? reason.trim() : undefined,
      });

      if (!eventSourceRef.current) {
        setExecutionModal(prev => ({ ...prev, status: 'cancelled' }));
        loadData();
      }
    } catch (err) {
      console.error('Failed to cancel:', err);
      setExecutionModal(prev => ({ ...prev, status: 'error', errorMessage: 'Failed to cancel execution' }));
    }
  };

  // ── Delete orchestration ──────────────────────────────────────────────────

  const deleteOrchestration = async (orchestrationId: string, e?: React.MouseEvent): Promise<void> => {
    if (e) {
      e.stopPropagation();
    }
    if (!confirm('Are you sure you want to remove this orchestration?')) return;
    try {
      await api.delete(`/api/orchestrations/${orchestrationId}`);
      if (selectedOrchId === orchestrationId) {
        setSelectedOrchId(null);
      }
      loadData();
    } catch (err) {
      console.error('Failed to delete orchestration:', err);
      const message = err instanceof Error ? err.message : 'Unknown error';
      alert('Failed to delete orchestration: ' + message);
    }
  };

  // ── Toggle orchestration enabled/disabled ─────────────────────────────────

  const toggleOrchestration = async (orchestrationId: string, currentlyEnabled: boolean | undefined, e?: React.MouseEvent): Promise<void> => {
    if (e) {
      e.stopPropagation();
    }
    try {
      const endpoint = currentlyEnabled
        ? `/api/orchestrations/${orchestrationId}/disable`
        : `/api/orchestrations/${orchestrationId}/enable`;
      await api.post(endpoint);
      loadData();
    } catch (err) {
      console.error('Failed to toggle orchestration:', err);
      const message = err instanceof Error ? err.message : 'Unknown error';
      alert('Failed to toggle orchestration: ' + message);
    }
  };

  // ── Attach to a running execution ─────────────────────────────────────────

  const attachToExecution = async (
    execution: { executionId?: string; status?: string },
    orchestration: Orchestration | undefined,
  ): Promise<void> => {
    if (!execution?.executionId) return;

    // Close any existing EventSource before opening a new one
    if (eventSourceRef.current) {
      eventSourceRef.current.close();
      eventSourceRef.current = null;
    }

    const initialStatuses = buildInitialStatuses(orchestration);

    setExecutionModal({
      open: true,
      orchestration: orchestration || null,
      executionId: execution.executionId,
      stepStatuses: initialStatuses,
      stepEvents: {},
      stepResults: {},
      stepTraces: {},
      stepAuditLogs: {},
      stepActorStreams: {},
      streamingContent: '',
      finalResult: '',
      status: execution.status === 'Cancelling' ? 'cancelling' : 'running',
      errorMessage: null,
      completedByStep: null,
      runContext: null,
      hookExecutions: [],
      savedFiles: [],
      stepSavedFiles: {},
    });

    try {
      const eventSource = new EventSource(`/api/execution/${execution.executionId}/attach`);
      eventSourceRef.current = eventSource;
      wireEventSource(eventSource, initialStatuses, execution.executionId);
    } catch (err) {
      console.error('Attach error:', err);
      const message = err instanceof Error ? err.message : 'Failed to attach to execution';
      setExecutionModal(prev => ({
        ...prev,
        status: 'error',
        errorMessage: message,
      }));
    }
  };

  // ── View a historical (completed) execution ───────────────────────────────

  const viewHistoricalExecution = async (exec: { orchestrationName?: string; runId?: string }): Promise<void> => {
    if (!exec?.orchestrationName || !exec?.runId) return;

    const orchestration = orchestrations?.find(o =>
      o.name === exec.orchestrationName || o.id === exec.orchestrationName
    );

    // Show loading state
    setExecutionModal({
      open: true,
      orchestration: orchestration || null,
      executionId: exec.runId,
      stepStatuses: {},
      stepEvents: {},
      stepResults: {},
      stepTraces: {},
      stepAuditLogs: {},
      stepActorStreams: {},
      streamingContent: '',
      finalResult: '',
      status: 'loading',
      errorMessage: null,
      completedByStep: null,
      runContext: null,
      hookExecutions: [],
      savedFiles: [],
      stepSavedFiles: {},
      retriedFromRunId: null,
      retryMode: null,
      historicalRun: { name: exec.orchestrationName, runId: exec.runId },
    });

    try {
      const details = await api.get<ExecutionDetailsResponse>(
        `/api/history/${encodeURIComponent(exec.orchestrationName)}/${encodeURIComponent(exec.runId)}`
      );

      const stepStatuses: Record<string, string> = {};
      const stepEvents: Record<string, StepEvent[]> = {};
      const stepResults: Record<string, string> = {};
      const stepTraces: Record<string, TraceData> = {};
      const stepAuditLogs: Record<string, AuditLogEntry[]> = {};
      const stepSavedFiles: Record<string, string[]> = {};
      const stepChildRuns: Record<string, { executionId: string; orchestrationName: string; status?: string | null }> = {};
      const finalResult = details.finalContent || '';
      const hookExecutions = details.hookExecutions || [];
      const savedFiles = details.savedFiles || [];

      if (details.steps) {
        details.steps.forEach((step: ExecutionDetailStep) => {
          const statusMap: Record<string, string> = {
            'Succeeded': 'completed',
            'Failed': 'failed',
            'Cancelled': 'cancelled',
            'Skipped': 'skipped',
            'Running': 'running',
            'Pending': 'pending',
            'NoAction': 'noaction',
          };
          stepStatuses[step.name] = statusMap[step.status] || 'pending';

          if (step.content !== undefined) {
            stepResults[step.name] = step.content;
          }

          if (step.savedFiles && step.savedFiles.length > 0) {
            stepSavedFiles[step.name] = step.savedFiles;
          }

          // For Orchestration steps the API surfaces the child run lineage so we can
          // render a clickable "view child run" badge next to the step name.
          if (step.childExecutionId && step.childOrchestrationName) {
            stepChildRuns[step.name] = {
              executionId: step.childExecutionId,
              orchestrationName: step.childOrchestrationName,
              status: step.childStatus ?? null,
            };
          }

          if (step.trace) {
            stepTraces[step.name] = step.trace as unknown as TraceData;
            // Extract audit log from trace if available
            const traceAny = step.trace as unknown as TraceData;
            if (traceAny.auditLog && traceAny.auditLog.length > 0) {
              stepAuditLogs[step.name] = traceAny.auditLog;
            }
          }

          // Create events from step data
          stepEvents[step.name] = [];

          // Add step started event
          stepEvents[step.name].push({
            time: step.startedAt ? new Date(step.startedAt).toLocaleTimeString() : '',
            type: 'step-started',
          } as StepEvent);

          // Add usage info if available
          if (step.usage) {
            stepEvents[step.name].push({
              time: step.completedAt ? new Date(step.completedAt).toLocaleTimeString() : '',
              type: 'usage',
              model: step.actualModel,
              inputTokens: step.usage.inputTokens,
              outputTokens: step.usage.outputTokens,
            } as StepEvent);
          }

          // Add tool call events from trace
          if (step.trace?.toolCalls) {
            step.trace.toolCalls.forEach(tc => {
              stepEvents[step.name].push({
                time: tc.startedAt ? new Date(tc.startedAt).toLocaleTimeString() : '',
                type: 'tool-call',
                toolName: tc.toolName,
                mcpServer: tc.mcpServer,
                success: tc.success,
              } as StepEvent);
            });
          }

          // Add completion or error event
          if (step.status === 'Succeeded') {
            stepEvents[step.name].push({
              time: step.completedAt ? new Date(step.completedAt).toLocaleTimeString() : '',
              type: 'step-completed',
              actualModel: step.actualModel,
              selectedModel: step.selectedModel,
              requestedModelInfo: step.requestedModelInfo,
              selectedModelInfo: step.selectedModelInfo,
              actualModelInfo: step.actualModelInfo,
              configuredProvider: step.configuredProvider,
              actualProvider: step.actualProvider,
              contentPreview: step.content
                ? step.content.substring(0, 200) + (step.content.length > 200 ? '...' : '')
                : undefined,
            } as StepEvent);
          } else if (step.status === 'NoAction') {
            stepEvents[step.name].push({
              time: step.completedAt ? new Date(step.completedAt).toLocaleTimeString() : '',
              type: 'step-completed',
              actualModel: step.actualModel,
              selectedModel: step.selectedModel,
              requestedModelInfo: step.requestedModelInfo,
              selectedModelInfo: step.selectedModelInfo,
              actualModelInfo: step.actualModelInfo,
              configuredProvider: step.configuredProvider,
              actualProvider: step.actualProvider,
            } as StepEvent);
          } else if (step.errorMessage) {
            stepEvents[step.name].push({
              time: step.completedAt ? new Date(step.completedAt).toLocaleTimeString() : '',
              type: 'step-error',
              error: step.errorMessage,
            } as StepEvent);
          }
        });
      }

      // Mark the step that triggered early completion with a distinct status for DAG visualization
      if (details.completedByStep && stepStatuses[details.completedByStep]) {
        stepStatuses[details.completedByStep] = 'completed_early';
      }

      // Determine overall status
      const overallStatusMap: Record<string, string> = {
        'Succeeded': 'success',
        'Failed': 'failed',
        'Cancelled': 'cancelled',
      };
      const isEarlyCompletion = details.status === 'Succeeded' && !!details.completionReason;
      const modalStatus = isEarlyCompletion
        ? 'completed_early'
        : overallStatusMap[details.status] || 'success';

      setExecutionModal({
        open: true,
        orchestration: orchestration || null,
        executionId: exec.runId,
        stepStatuses,
        stepEvents,
        stepResults,
        stepTraces,
        stepAuditLogs,
        stepActorStreams: {},
        streamingContent: finalResult,
        finalResult,
        status: modalStatus,
        errorMessage: null,
        completedByStep: details.completedByStep || null,
        runContext: details.context || null,
        hookExecutions,
        savedFiles,
        stepSavedFiles,
        retriedFromRunId: details.retriedFromRunId ?? null,
        retryMode: details.retryMode ?? null,
        historicalRun: { name: exec.orchestrationName, runId: exec.runId },
        stepChildRuns,
      });
    } catch (err) {
      console.error('Failed to load execution details:', err);
      const message = err instanceof Error ? err.message : 'Failed to load execution details';
      setExecutionModal(prev => ({
        ...prev,
        status: 'error',
        errorMessage: message,
      }));
    }
  };

  // ── Delete a history entry ────────────────────────────────────────────────

  const deleteHistoryEntry = async (exec: HistoryListEntry, e?: React.MouseEvent): Promise<void> => {
    if (e) {
      e.stopPropagation();
      e.preventDefault();
    }
    if (!exec?.orchestrationName || !exec?.runId) return;

    // Don't allow deleting running executions
    if (exec.isActive) return;

    try {
      await api.delete(`/api/history/${encodeURIComponent(exec.orchestrationName)}/${encodeURIComponent(exec.runId)}`);
      setHistory(prev => prev.filter(h => h.runId !== exec.runId));
    } catch (err) {
      console.error('Failed to delete history entry:', err);
    }
  };

  // ── Keyboard shortcuts (Escape closes sidebar) ────────────────────────────
  useKeyboardShortcuts({
    onEscape: useCallback(() => {
      if (sidebarOpen) setSidebarOpen(false);
      if (profileDropdownOpen) setProfileDropdownOpen(false);
    }, [sidebarOpen, profileDropdownOpen]),
  });

  // ── Profile filter toggle helpers ──

  const toggleSidebarProfileFilter = (profileId: string) => {
    setProfileFilter(prev =>
      prev.includes(profileId)
        ? prev.filter(id => id !== profileId)
        : [...prev, profileId]
    );
  };

  const toggleMainPaneProfileFilter = (profileId: string) => {
    setMainPaneProfileFilter(prev =>
      prev.includes(profileId)
        ? prev.filter(id => id !== profileId)
        : [...prev, profileId]
    );
  };

  // Close sidebar dropdown on outside click
  const sidebarDropdownRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    const handler = (e: MouseEvent) => {
      if (profileDropdownOpen && sidebarDropdownRef.current && !sidebarDropdownRef.current.contains(e.target as Node)) {
        setProfileDropdownOpen(false);
      }
    };
    document.addEventListener('mousedown', handler);
    return () => document.removeEventListener('mousedown', handler);
  }, [profileDropdownOpen]);

  /** Render a multi-profile checkbox dropdown */
  const renderProfileMultiSelect = (
    selectedIds: string[],
    onToggle: (id: string) => void,
    isOpen: boolean,
    setIsOpen: (v: boolean) => void,
    onClear: () => void,
    label: string,
    dropdownRef: React.RefObject<HTMLDivElement | null>,
  ) => (
    <div className="profile-multiselect" ref={dropdownRef as React.RefObject<HTMLDivElement>}>
      <button
        className={`profile-multiselect-trigger ${selectedIds.length > 0 ? 'has-selection' : ''}`}
        onClick={() => setIsOpen(!isOpen)}
        aria-label={label}
      >
        <Icons.Filter />
        {selectedIds.length === 0 ? 'All profiles' : `${selectedIds.length} profile${selectedIds.length > 1 ? 's' : ''}`}
        <span className="profile-multiselect-caret">{isOpen ? '\u25B2' : '\u25BC'}</span>
      </button>
      {isOpen && (
        <div className="profile-multiselect-dropdown">
          {profiles.map(p => (
            <label key={p.id} className="profile-multiselect-option">
              <input
                type="checkbox"
                checked={selectedIds.includes(p.id)}
                onChange={() => onToggle(p.id)}
              />
              <span className={`status-dot ${p.isActive ? 'enabled' : 'disabled'}`}></span>
              <span className="profile-multiselect-name">{p.name}</span>
              {p.filter.tags?.includes('*') && <span className="tag-chip tag-wildcard tag-chip-small" style={{ marginLeft: 'auto' }}>all</span>}
            </label>
          ))}
          {selectedIds.length > 0 && (
            <button className="profile-multiselect-clear" onClick={() => { onClear(); setIsOpen(false); }}>
              Clear filter
            </button>
          )}
        </div>
      )}
    </div>
  );

  // ── Render ────────────────────────────────────────────────────────────────

  return (
    <div className="app-container">
      {/* Offline / server-unreachable banner */}
      <OfflineBanner onlineStatus={onlineStatus} />

      {/* Skip to main content link for keyboard users */}
      <a href="#main-content" className="skip-link">Skip to main content</a>

      {/* Mobile sidebar overlay */}
      <div
        className={`sidebar-overlay ${sidebarOpen ? 'visible' : ''}`}
        onClick={() => setSidebarOpen(false)}
        aria-hidden="true"
      />

      {/* Left Pane */}
      <nav
        className={`left-pane ${sidebarOpen ? 'open' : ''}`}
        aria-label="Orchestrations sidebar"
      >
        <div className="left-header">
          <div className="app-title">
            <Icons.Workflow />
            Orchestra Portal
          </div>
          <div className="search-row" role="search">
            <div className="search-box">
              <span className="search-icon" aria-hidden="true"><Icons.Search /></span>
              <input
                type="text"
                placeholder="Search orchestrations, tags..."
                value={searchQuery}
                onChange={(e: React.ChangeEvent<HTMLInputElement>) => setSearchQuery(e.target.value)}
                aria-label="Search orchestrations"
              />
            </div>
            {profiles.length > 0 && (
              renderProfileMultiSelect(
                profileFilter,
                toggleSidebarProfileFilter,
                profileDropdownOpen,
                setProfileDropdownOpen,
                () => setProfileFilter([]),
                'Filter orchestrations by profile',
                sidebarDropdownRef,
              )
            )}
          </div>
          <div className="header-btn-row">
            <button className="btn btn-primary" onClick={() => { setAddModal({ open: true }); setSidebarOpen(false); }}>
              <Icons.Workflow /> Orchestrations
            </button>
            <button className="btn btn-primary" onClick={() => { setProfilesModal(true); setSidebarOpen(false); }}>
              <Icons.Shield /> Profiles
            </button>
          </div>
          <div className="header-btn-row">
            <button className="btn" onClick={() => { setBuilderModal(true); setSidebarOpen(false); }}>
              <Icons.Steps aria-hidden="true" /> Visual Builder
            </button>
            <button className="btn" onClick={() => { setMcpsModal({ open: true }); setSidebarOpen(false); }}>
              <Icons.Tool /> MCP Tools
            </button>
          </div>
          <div className="header-btn-row">
            <button
              className="btn"
              onClick={() => { setWaitingInputsModal(true); setSidebarOpen(false); }}
              aria-label={pendingInputs.count > 0
                ? `Waiting for input (${pendingInputs.count} pending)`
                : 'Waiting for input'}
            >
              <Icons.Hand /> Waiting for Input
              {pendingInputs.count > 0 && (
                <span className="waiting-inputs-badge" aria-hidden="true">
                  {pendingInputs.count}
                </span>
              )}
            </button>
          </div>
        </div>

        <div className={`orchestrations-section ${orchestrationsCollapsed ? 'collapsed' : ''}`} aria-label="Orchestrations">
          <div
            className="orchestrations-header"
            onClick={toggleOrchestrationsCollapsed}
            style={{ cursor: 'pointer' }}
            role="button"
            aria-expanded={!orchestrationsCollapsed}
            tabIndex={0}
            onKeyDown={(e: React.KeyboardEvent) => {
              if (e.key === 'Enter' || e.key === ' ') {
                e.preventDefault();
                toggleOrchestrationsCollapsed();
              }
            }}
          >
            <span className="orchestrations-title" id="orchestrations-title">
              <span className="orchestrations-collapse-caret">{orchestrationsCollapsed ? '\u25B6' : '\u25BC'}</span>
              Orchestrations
              {orchestrationsCollapsed && filteredOrchestrations.length > 0 && (
                <span className="orchestrations-count-badge">{filteredOrchestrations.length}</span>
              )}
            </span>
          </div>
          {!orchestrationsCollapsed && (
          <div className="orchestrations-list" role="listbox" aria-label="Orchestrations" aria-labelledby="orchestrations-title">
          {loading ? (
            <div className="empty-state">
              <div className="spinner"></div>
            </div>
          ) : filteredOrchestrations.length === 0 ? (
            <div className="empty-state">
              <div className="empty-text">No orchestrations found</div>
            </div>
          ) : (
            filteredOrchestrations.map(orch => (
              <div
                key={orch.id}
                data-orchestration-id={orch.id}
                className={`orch-item ${selectedOrchId === orch.id ? 'active' : ''}`}
                role="option"
                aria-selected={selectedOrchId === orch.id}
                tabIndex={0}
                onClick={() => {
                  setSelectedOrchId(orch.id);
                  setViewerModal({ open: true, orchestration: orch });
                  setSidebarOpen(false);
                }}
                onKeyDown={(e: React.KeyboardEvent) => {
                  if (e.key === 'Enter' || e.key === ' ') {
                    e.preventDefault();
                    setSelectedOrchId(orch.id);
                    setViewerModal({ open: true, orchestration: orch });
                    setSidebarOpen(false);
                  }
                }}
              >
                <div className="orch-item-header">
                  <span className="orch-name">{orch.name}</span>
                  <div className="orch-status" style={{ display: 'flex', alignItems: 'center', gap: '4px' }}>
                    <span className={`status-dot ${orch.status === 'Running' ? 'running' : orch.triggerType === 'Manual' || !orch.triggerType ? 'manual' : orch.enabled ? 'enabled' : 'disabled'}`}></span>
                    {orch.triggerType && orch.triggerType !== 'Manual' ? (
                      <button
                        className={`btn-icon btn-toggle ${orch.enabled ? 'enabled' : ''}`}
                        onClick={(e: React.MouseEvent) => toggleOrchestration(orch.id, orch.enabled, e)}
                        title={orch.enabled ? 'Disable trigger' : 'Enable trigger'}
                        aria-label={orch.enabled ? `Disable ${orch.name} trigger` : `Enable ${orch.name} trigger`}
                      >
                        {orch.enabled ? <Icons.Check /> : <Icons.Play />}
                      </button>
                    ) : (
                      <button
                        className="btn-icon"
                        onClick={(e: React.MouseEvent) => {
                          e.stopPropagation();
                          if ((orch as RuntimeOrchestration)?.hasParameters) {
                            setRunModal({ open: true, orchestration: orch });
                          } else {
                            runOrchestration(orch.id);
                          }
                          setSidebarOpen(false);
                        }}
                        title="Run orchestration"
                        aria-label={`Run ${orch.name}`}
                      >
                        <Icons.Play />
                      </button>
                    )}
                    <button
                      className="btn-icon btn-delete-small"
                      onClick={(e: React.MouseEvent) => deleteOrchestration(orch.id, e)}
                      title="Remove orchestration"
                      aria-label={`Remove ${orch.name}`}
                    >
                      <Icons.X />
                    </button>
                  </div>
                </div>
                <div className="orch-meta">
                  <span className="orch-meta-item">
                    <Icons.Steps /> {orch.stepCount || 0} steps
                  </span>
                  <span className={`badge badge-${orch.triggerType?.toLowerCase() || 'trigger'}`}>
                    {orch.triggerType || 'Manual'}
                  </span>
                </div>
                {orch.tags && orch.tags.length > 0 && (
                  <div className="orch-tags">
                    {orch.tags.map(tag => (
                      <span key={tag} className={`tag-chip ${tag === '*' ? 'tag-wildcard' : ''}`}>
                        <Icons.Tag />{tag}
                      </span>
                    ))}
                  </div>
                )}
                {(() => {
                  const matchedProfiles = getProfilesForOrchestration(orch);
                  return matchedProfiles.length > 0 ? (
                    <div className="orch-profiles">
                      {matchedProfiles.map(p => (
                        <span key={p.id} className={`profile-badge ${p.isActive ? 'active' : ''}`}>
                          <Icons.Shield />{p.name}
                        </span>
                      ))}
                    </div>
                  ) : null;
                })()}
              </div>
            ))
          )}
          </div>
          )}
        </div>

        {/* History Section */}
        <div className={`history-section ${historyCollapsed ? 'collapsed' : ''}`} aria-label="Recent executions">
          <div className="history-header" onClick={toggleHistoryCollapsed} style={{ cursor: 'pointer' }} role="button" aria-expanded={!historyCollapsed} tabIndex={0} onKeyDown={(e: React.KeyboardEvent) => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); toggleHistoryCollapsed(); } }}>
            <span className="history-title" id="history-title">
              <span className="history-collapse-caret">{historyCollapsed ? '\u25B6' : '\u25BC'}</span>
              Recent Executions
              {historyCollapsed && filteredHistory.length > 0 && (
                <span className="history-count-badge">{filteredHistory.length}</span>
              )}
            </span>
            <div className="history-header-actions" onClick={(e: React.MouseEvent) => e.stopPropagation()}>
              <HistoryFilterSelector
                state={historyFilters}
                onChange={updateHistoryFilters}
                onShowAllRequested={() => { setHistoryModal({ open: true }); setSidebarOpen(false); }}
              />
            </div>
          </div>
          {!historyCollapsed && (
          <div className="history-list" role="list" aria-labelledby="history-title">
            {historyLoading ? (
              <div className="empty-state" style={{ padding: '20px' }}>
                <div className="spinner" style={{ width: '16px', height: '16px' }}></div>
              </div>
            ) : filteredHistory.length === 0 ? (
              <div className="empty-state" style={{ padding: '20px' }}>
                <div className="empty-text">
                  {history.length === 0 ? 'No executions yet' : 'No matching executions'}
                </div>
                {history.length > 0 && hideIncomplete && (
                  <button className="btn btn-sm" style={{ marginTop: '8px' }} onClick={toggleHideIncomplete}>
                    Show incomplete
                  </button>
                )}
              </div>
            ) : (
              filteredHistory.map(exec => (
                <HistoryRow
                  key={exec.runId}
                  exec={exec}
                  onSelect={(target) => {
                    if (target.isActive && target.executionId) {
                      const orch = orchestrations?.find(o => o.id === target.orchestrationId);
                      attachToExecution(target as HistoryListEntry, orch);
                    } else {
                      viewHistoricalExecution(target as HistoryListEntry);
                    }
                    setSidebarOpen(false);
                  }}
                  onDelete={(target, e) => deleteHistoryEntry(target as HistoryListEntry, e)}
                  onViewSourceRun={(sourceRunId) => {
                    viewHistoricalExecution({
                      orchestrationName: exec.orchestrationName,
                      runId: sourceRunId,
                    } as HistoryListEntry);
                    setSidebarOpen(false);
                  }}
                  onViewParentRun={(parentRunId) => {
                    if (exec.parentOrchestrationName) {
                      viewHistoricalExecution({
                        orchestrationName: exec.parentOrchestrationName,
                        runId: parentRunId,
                      } as HistoryListEntry);
                      setSidebarOpen(false);
                    }
                  }}
                />
              ))
            )}
          </div>
          )}
        </div>
      </nav>

      {/* Main Pane */}
      <main id="main-content" className="main-pane">
        <div className="main-header">
          <div className="main-heading">
            <button
              className="mobile-menu-btn"
              onClick={() => setSidebarOpen(prev => !prev)}
              aria-label="Toggle sidebar menu"
            >
              <Icons.Menu />
            </button>
            <span className="main-title">Active Orchestrations</span>
          </div>
          <div className="main-search-area" role="search" aria-label="Search active orchestrations">
            <div className="search-box main-search-box">
              <span className="search-icon" aria-hidden="true"><Icons.Search /></span>
              <input
                type="text"
                placeholder="Search active orchestrations, tags, steps..."
                value={mainPaneSearchQuery}
                onChange={(e: React.ChangeEvent<HTMLInputElement>) => setMainPaneSearchQuery(e.target.value)}
                aria-label="Search active orchestrations"
              />
              {mainPaneSearchQuery && (
                <button
                  className="search-clear-btn"
                  onClick={() => setMainPaneSearchQuery('')}
                  aria-label="Clear active orchestrations search"
                  title="Clear search"
                  type="button"
                >
                  <Icons.X />
                </button>
              )}
              {profiles.length > 0 && (
                <ProfileSelector
                  profiles={profiles}
                  selectedProfileIds={mainPaneProfileFilter}
                  onToggleProfile={toggleMainPaneProfileFilter}
                  onClearFilter={() => setMainPaneProfileFilter([])}
                  onProfileChanged={loadProfiles}
                  onManageProfiles={() => setProfilesModal(true)}
                />
              )}
              <select
                className="active-status-select"
                value={activeStatusFilter}
                onChange={(e: React.ChangeEvent<HTMLSelectElement>) => setActiveStatusFilter(e.target.value as ActiveStatusFilter)}
                aria-label="Filter active orchestration status"
              >
                {ACTIVE_STATUS_FILTERS.map(filter => (
                  <option key={filter.id} value={filter.id}>
                    {filter.label} ({activeStatusCounts[filter.id]})
                  </option>
                ))}
              </select>
            </div>
          </div>
          <div className="main-actions">
            <button className="btn" onClick={loadData}>Refresh</button>
          </div>
        </div>

        <div className="cards-container" style={{ overflow: 'auto' }}>
          {filteredOrchestrationView.running.length === 0
            && filteredOrchestrationView.enabled.length === 0
            && filteredOrchestrationView.disabled.length === 0 ? (
            <div className="empty-state">
              <div className="empty-icon"><Icons.Activity /></div>
              <div className="empty-title">
                {hasActiveOrchestrationFilters ? 'No Matching Orchestrations' : 'No Orchestrations'}
              </div>
              <div className="empty-text">
                {hasActiveOrchestrationFilters
                  ? 'No orchestrations match the current search or filters.'
                  : 'Add orchestrations to get started.'}
              </div>
              {hasActiveOrchestrationFilters && (
                <button
                  className="btn btn-sm"
                  style={{ marginTop: '12px' }}
                  onClick={() => {
                    setMainPaneSearchQuery('');
                    setMainPaneProfileFilter([]);
                    setActiveStatusFilter('all');
                  }}
                >
                  Clear filters
                </button>
              )}
            </div>
          ) : (
            <div style={{ display: 'flex', flexDirection: 'column', gap: '24px' }}>
              {/* Running Section */}
              {filteredOrchestrationView.running.length > 0 && (
                <div>
                  <div className="cards-section-header">
                    <span className="cards-section-dot cards-section-dot-running"></span>
                    Running ({filteredOrchestrationView.running.length})
                  </div>
                  <div className="cards-grid">
                    {filteredOrchestrationView.running.map(exec => (
                      <ActiveOrchestrationCard
                        key={exec.executionId || exec.orchestrationId}
                        execution={exec}
                        type="running"
                        orchestrations={orchestrations}
                        profiles={profiles}
                        awaitingInput={!!exec.executionId && awaitingRunIds.has(exec.executionId)}
                        onView={(execution, orch) => {
                          attachToExecution(execution, orch);
                        }}
                        onCancel={async (executionId: string) => {
                          await cancelExecution(executionId);
                          loadData();
                        }}
                        onRun={(orch: Orchestration) => {
                          if ((orch as RuntimeOrchestration)?.hasParameters) {
                            setRunModal({ open: true, orchestration: orch });
                          } else {
                            runOrchestration(orch?.id);
                          }
                        }}
                      />
                    ))}
                  </div>
                </div>
              )}

              {/* Enabled Section */}
              {filteredOrchestrationView.enabled.length > 0 && (
                <div>
                  <div className="cards-section-header">
                    <span className="cards-section-dot cards-section-dot-pending"></span>
                    Enabled ({filteredOrchestrationView.enabled.length})
                  </div>
                  <div className="cards-grid">
                    {filteredOrchestrationView.enabled.map(exec => (
                      <ActiveOrchestrationCard
                        key={exec.orchestrationId}
                        execution={exec}
                        type="pending"
                        orchestrations={orchestrations}
                        profiles={profiles}
                        onView={(_execution, orch) => {
                          if (orch) {
                            setViewerModal({ open: true, orchestration: orch });
                          }
                        }}
                        onRun={(orch: Orchestration) => {
                          if ((orch as RuntimeOrchestration)?.hasParameters) {
                            setRunModal({ open: true, orchestration: orch });
                          } else {
                            runOrchestration(orch?.id);
                          }
                        }}
                        onToggleTrigger={(id, enabled) => toggleOrchestration(id, enabled)}
                      />
                    ))}
                  </div>
                </div>
              )}

              {/* Disabled Section */}
              {filteredOrchestrationView.disabled.length > 0 && (
                <div>
                  <div className="cards-section-header">
                    <span className="cards-section-dot cards-section-dot-disabled"></span>
                    Disabled ({filteredOrchestrationView.disabled.length})
                  </div>
                  <div className="cards-grid">
                    {filteredOrchestrationView.disabled.map(exec => (
                      <ActiveOrchestrationCard
                        key={exec.orchestrationId}
                        execution={exec}
                        type="disabled"
                        orchestrations={orchestrations}
                        profiles={profiles}
                        onView={(_execution, orch) => {
                          if (orch) {
                            setViewerModal({ open: true, orchestration: orch });
                          }
                        }}
                        onRun={(orch: Orchestration) => {
                          if ((orch as RuntimeOrchestration)?.hasParameters) {
                            setRunModal({ open: true, orchestration: orch });
                          } else {
                            runOrchestration(orch?.id);
                          }
                        }}
                        onToggleTrigger={(id, enabled) => toggleOrchestration(id, enabled)}
                      />
                    ))}
                  </div>
                </div>
              )}
            </div>
          )}
        </div>
      </main>

      {/* Modals */}
      <ViewerModal
        {...viewerModal}
        onClose={() => setViewerModal({ open: false, orchestration: null })}
        onRun={() => {
          const orch = viewerModal.orchestration;
          if ((orch as RuntimeOrchestration)?.hasParameters) {
            setViewerModal({ open: false, orchestration: null });
            setRunModal({ open: true, orchestration: orch });
          } else {
            runOrchestration(orch?.id);
          }
        }}
        onTagsChanged={() => { loadData(); loadProfiles(); }}
      />
      <HistoryModal
        {...historyModal}
        onClose={() => setHistoryModal({ open: false })}
        onAttachToExecution={attachToExecution}
        onViewExecution={viewHistoricalExecution}
        onRetryExecution={retryExecution}
        orchestrations={orchestrations}
      />
      <AddModal
        {...addModal}
        onClose={() => setAddModal({ open: false })}
        onAdded={loadData}
      />
      <RunModal
        {...runModal}
        onClose={() => setRunModal({ open: false, orchestration: null, retryContext: null, initialValues: null })}
        onRun={(params: Record<string, string>) => {
          // Snapshot the retry context before we tear the modal down so the
          // setState below doesn't strip it from the closure.
          const retryCtx = runModal.retryContext;
          const orch = runModal.orchestration;
          setRunModal({ open: false, orchestration: null, retryContext: null, initialValues: null });
          if (retryCtx) {
            // "Re-run with edits" flow: route through the retry endpoint so the
            // new run is linked to its source (retriedFromRunId lineage) and
            // tagged retryMode='all-edited' in the run record.
            retryExecution(retryCtx.orchestrationName, retryCtx.sourceRunId, 'all', undefined, params);
          } else {
            runOrchestration(orch?.id, params);
          }
        }}
      />
      <ExecutionModal
        {...executionModal}
        onClose={() => {
          if (eventSourceRef.current) {
            eventSourceRef.current.close();
            eventSourceRef.current = null;
          }
          setExecutionModal({
            open: false,
            orchestration: null,
            executionId: null,
            stepStatuses: {},
            stepEvents: {},
            stepResults: {},
            stepTraces: {},
            stepAuditLogs: {},
            stepActorStreams: {},
            streamingContent: '',
            finalResult: '',
            status: 'idle',
            errorMessage: null,
            completedByStep: null,
            runContext: null,
            hookExecutions: [],
            savedFiles: [],
            stepSavedFiles: {},
            retriedFromRunId: null,
            retryMode: null,
            historicalRun: null,
          });
        }}
        onCancel={() => cancelExecution(executionModal.executionId)}
        onRetry={(mode, fromStep) => {
          // Prefer the historicalRun lineage if present (modal was opened from
          // History). Otherwise fall back to the in-memory orchestration name +
          // executionId so retries work for a freshly-completed live run too.
          const name = executionModal.historicalRun?.name
            ?? executionModal.orchestration?.name
            ?? null;
          const sourceRunId = executionModal.historicalRun?.runId
            ?? executionModal.executionId
            ?? null;
          if (!name || !sourceRunId) return;

          if (mode === 'all-with-edits') {
            // Re-locate the orchestration definition so the RunModal can render the
            // typed-input form with the current schema, even if the source run was
            // produced by an older version. Pre-fill values come from runContext
            // (already loaded for both historical and live-completed runs); a
            // missing runContext is tolerated -- the user just sees empty defaults.
            const orch = orchestrations.find(o => o.name === name || o.id === name) ?? null;
            if (!orch) {
              console.warn('Re-run with edits: orchestration not found in registry; cannot open modal.');
              return;
            }
            setRunModal({
              open: true,
              orchestration: orch,
              initialValues: executionModal.runContext?.parameters ?? null,
              retryContext: { orchestrationName: name, sourceRunId },
              title: `Re-run ${orch.name}`,
              submitLabel: 'Re-run',
            });
            return;
          }

          retryExecution(name, sourceRunId, mode, fromStep);
        }}
        onViewSourceRun={(sourceRunId) => {
          if (executionModal.orchestration?.name) {
            viewHistoricalExecution({
              orchestrationName: executionModal.orchestration.name,
              runId: sourceRunId,
            });
          }
        }}
        onViewChildRun={(orchestrationName, executionId) => {
          // Navigate from a parent step's child-run badge into the child run's
          // historical detail view. Mirrors the inverse parent→child navigation
          // already exposed on HistoryRow.
          viewHistoricalExecution({
            orchestrationName,
            runId: executionId,
          });
        }}
      />
      <McpsModal
        {...mcpsModal}
        onClose={() => setMcpsModal({ open: false })}
      />
      <WaitingInputsModal
        open={waitingInputsModal}
        onClose={() => setWaitingInputsModal(false)}
        records={pendingInputs.list}
        loading={pendingInputs.loading}
        onResponded={(orchestrationName, runId, stepName) => {
          pendingInputs.removeLocal(orchestrationName, runId, stepName);
        }}
      />
      <ActiveModal
        {...activeModal}
        orchestrations={orchestrations}
        onClose={() => setActiveModal({ open: false, data: null, loading: false })}
        onRefresh={async () => {
          setActiveModal(prev => ({ ...prev, loading: true }));
          try {
            const data = await api.get<ActiveData>('/api/active');
            setActiveModal({ open: true, data, loading: false });
          } catch (err) {
            console.error('Failed to refresh active:', err);
            setActiveModal(prev => ({ ...prev, loading: false }));
          }
        }}
        onViewOrchestration={(orch: Orchestration) => {
          setActiveModal({ open: false, data: null, loading: false });
          setViewerModal({ open: true, orchestration: orch });
        }}
        onViewRunningExecution={(exec, orch) => {
          setActiveModal({ open: false, data: null, loading: false });
          attachToExecution(exec, orch);
        }}
        onCancelExecution={async (executionId: string) => {
          await cancelExecution(executionId);
          try {
            const data = await api.get<ActiveData>('/api/active');
            setActiveModal(prev => ({ ...prev, data }));
          } catch (err) {
            console.error('Failed to refresh after cancel:', err);
          }
        }}
      />
      <BuilderModal
        open={builderModal}
        onClose={() => setBuilderModal(false)}
        onSave={async (json: string) => {
          try {
            await api.post('/api/orchestrations/json', { json, mcpJson: null });
            setBuilderModal(false);
            loadData();
          } catch (err) {
            console.error('Failed to save orchestration from builder:', err);
          }
        }}
      />
      <ProfilesModal
        open={profilesModal}
        onClose={() => { setProfilesModal(false); loadProfiles(); }}
      />

      {/* Status Bar */}
      <StatusBar status={serverStatus} onlineStatus={onlineStatus} />
    </div>
  );
}

export default App;
