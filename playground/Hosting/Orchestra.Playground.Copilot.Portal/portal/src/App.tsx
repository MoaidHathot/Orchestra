import React, { useState, useEffect, useCallback, useMemo, useRef } from 'react';
import './App.css';
import { api } from './api';
import { Icons } from './icons';
import { formatTime, isIncompleteExecution, profileFilterMatchesOrchestration, getMatchingProfiles, orchestrationMatchesProfileFilter, orchestrationMatchesSearch } from './utils';
import type {
  Orchestration,
  ActiveData,
  ServerStatus,
  ExecutionModalState,
  StepEvent,
  Step,
  TraceData,
  RunContext,
  Profile,
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
import ProfileSelector from './components/ProfileSelector';
import { useKeyboardShortcuts } from './hooks/useKeyboardShortcuts';
import { useOnlineStatus } from './hooks/useOnlineStatus';

// ── API response types ──────────────────────────────────────────────────────

interface OrchestrationsResponse {
  orchestrations: RuntimeOrchestration[];
}

interface HistoryResponse {
  runs: HistoryListEntry[];
}

/** Shape of a step in the detailed execution response from /api/history/:name/:runId */
interface ExecutionDetailStep {
  name: string;
  status: string;
  content?: string;
  startedAt?: string;
  completedAt?: string;
  actualModel?: string;
  errorMessage?: string;
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
}

interface ExecutionDetailsResponse {
  status: string;
  completionReason?: string;
  completedByStep?: string;
  isIncomplete?: boolean;
  finalContent?: string;
  steps?: ExecutionDetailStep[];
  context?: RunContext | null;
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
}

// ── Helpers for SSE event handling ──────────────────────────────────────────

/** Extended orchestration type for runtime fields returned by the API but not in the base Orchestration type. */
interface RuntimeOrchestration extends Orchestration {
  status?: string;
  stepCount?: number;
  triggerType?: string;
  hasParameters?: boolean;
  lastExecutionStatus?: string;
}

type StepStatusValue = 'pending' | 'running' | 'completed' | 'failed' | 'cancelled' | 'skipped';

interface SSEEventData {
  stepName?: string;
  status?: string;
  completionReason?: string;
  completedByStep?: string;
  executionId?: string;
  chunk?: string;
  content?: string;
  error?: string;
  message?: string;
  [key: string]: unknown;
}

// ── The App component ───────────────────────────────────────────────────────

function App(): React.JSX.Element {
  const [orchestrations, setOrchestrations] = useState<RuntimeOrchestration[]>([]);
  const [history, setHistory] = useState<HistoryListEntry[]>([]);
  const [searchQuery, setSearchQuery] = useState('');
  const [selectedOrchId, setSelectedOrchId] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [historyLoading, setHistoryLoading] = useState(true);
  const [activeData, setActiveData] = useState<ActiveData>({ running: [], pending: [] });
  const [sidebarOpen, setSidebarOpen] = useState(false);

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
    streamingContent: '',
    finalResult: '',
    status: 'idle',
    errorMessage: null,
    completedByStep: null,
    runContext: null,
  });
  const eventSourceRef = useRef<EventSource | null>(null);
  const [mcpsModal, setMcpsModal] = useState<McpsModalState>({ open: false });
  const [activeModal, setActiveModal] = useState<ActiveModalState>({ open: false, data: null, loading: false });
  const [builderModal, setBuilderModal] = useState(false);
  const [profilesModal, setProfilesModal] = useState(false);

  // Profile data for filtering & membership display
  const [profiles, setProfiles] = useState<Profile[]>([]);
  const [profileFilter, setProfileFilter] = useState<string[]>([]); // empty = all, array of profile ids = multi-select
  const [profileDropdownOpen, setProfileDropdownOpen] = useState(false);
  const [mainPaneProfileFilter, setMainPaneProfileFilter] = useState<string[]>([]); // same logic for main pane

  // History filter state (persisted in localStorage)
  const [hideIncomplete, setHideIncomplete] = useState<boolean>(() => {
    const stored = localStorage.getItem('orchestra-hide-incomplete');
    return stored === null ? true : stored === 'true';
  });
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
  const toggleHideIncomplete = useCallback(() => {
    setHideIncomplete(prev => {
      const next = !prev;
      localStorage.setItem('orchestra-hide-incomplete', String(next));
      return next;
    });
  }, []);

  // Status bar state
  const [serverStatus, setServerStatus] = useState<ServerStatus>({
    outlook: null,
    orchestrationCount: 0,
    activeTriggers: 0,
    runningExecutions: 0,
  });

  // Online/offline tracking
  const onlineStatus = useOnlineStatus();

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
        api.get<HistoryResponse>('/api/history?limit=15'),
        api.get<ActiveData>('/api/active'),
      ]);
      setHistory(histData.runs || []);
      setActiveData(activeDataResult || { running: [], pending: [] });
    } catch (err) {
      console.error('Failed to load history/active data:', err);
    } finally {
      setHistoryLoading(false);
    }
  }, []);

  useEffect(() => { loadData(); }, [loadData]);

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

  // Reload data when coming back online
  useEffect(() => {
    if (onlineStatus.isOnline && onlineStatus.isServerReachable) {
      loadData();
    }
  }, [onlineStatus.isOnline, onlineStatus.isServerReachable, loadData]);

  // Auto-refresh active orchestrations every 2 seconds when there are running or pending ones
  useEffect(() => {
    const hasActiveOrPending = activeData.running.length > 0 || activeData.pending.length > 0;
    const hasEnabledTriggers = orchestrations.some(o => o.enabled);

    if (hasActiveOrPending || hasEnabledTriggers) {
      const interval = setInterval(async () => {
        try {
          const data = await api.get<ActiveData>('/api/active');
          setActiveData(data || { running: [], pending: [] });
        } catch (err) {
          console.error('Failed to refresh active:', err);
        }
      }, 1000);
      return () => clearInterval(interval);
    }
  }, [activeData.running.length, activeData.pending.length, orchestrations]);

  // Auto-refresh orchestrations list every 5 seconds for external changes
  useEffect(() => {
    const interval = setInterval(async () => {
      try {
        const data = await api.get<OrchestrationsResponse>('/api/orchestrations');
        setOrchestrations(data.orchestrations || []);
      } catch (err) {
        console.error('Failed to refresh orchestrations:', err);
      }
    }, 5000);
    return () => clearInterval(interval);
  }, []);

  // Auto-refresh history every 5 seconds when there are enabled triggers or active executions
  useEffect(() => {
    const hasEnabledTriggers = orchestrations.some(o => o.enabled);
    const hasActiveInHistory = history.some(h => h.isActive);
    if (hasEnabledTriggers || hasActiveInHistory) {
      const interval = setInterval(async () => {
        try {
          const histData = await api.get<HistoryResponse>('/api/history?limit=15');
          setHistory(histData.runs || []);
        } catch (err) {
          console.error('Failed to refresh history:', err);
        }
      }, 5000);
      return () => clearInterval(interval);
    }
  }, [orchestrations, history]);

  // Poll server status (including Outlook connection status) every 5 seconds
  useEffect(() => {
    const fetchStatus = async () => {
      try {
        const status = await api.get<ServerStatus>('/api/status');
        setServerStatus({
          outlook: status.outlook || null,
          orchestrationCount: status.orchestrationCount || 0,
          activeTriggers: status.activeTriggers || 0,
          runningExecutions: status.runningExecutions || 0,
        });
      } catch (err) {
        console.error('Failed to fetch server status:', err);
      }
    };

    fetchStatus();
    const interval = setInterval(fetchStatus, 5000);
    return () => clearInterval(interval);
  }, []);

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

    // Build a set of running orchestration IDs from active data
    const runningExecsByOrchId = new Map<string, CardExecution[]>();
    for (const exec of activeData.running) {
      const existing = runningExecsByOrchId.get(exec.orchestrationId) || [];
      existing.push(exec);
      runningExecsByOrchId.set(exec.orchestrationId, existing);
    }

    const running: CardExecution[] = [];
    const enabled: CardExecution[] = [];
    const disabled: CardExecution[] = [];

    for (const orch of matchedOrchs) {
      const rt = orch as RuntimeOrchestration;
      const isEnabled = rt.enabled !== false; // default true for ManualTriggerConfig

      // Check if this orchestration has running executions
      const runningExecs = runningExecsByOrchId.get(orch.id);
      if (runningExecs && runningExecs.length > 0) {
        // Add all running executions as separate cards
        running.push(...runningExecs);
      }

      // Also build a card for the orchestration definition itself
      const cardExec: CardExecution = {
        orchestrationId: orch.id,
        orchestrationName: orch.name,
        stepCount: rt.stepCount || orch.steps?.length,
        triggeredBy: rt.triggerType || 'Manual',
      };

      if (isEnabled) {
        enabled.push(cardExec);
      } else {
        disabled.push(cardExec);
      }
    }

    return { running, enabled, disabled };
  }, [orchestrations, activeData, mainPaneProfileFilter, orchMatchesProfileFilter]);

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
  ): void {
    // Track state locally for batching updates
    const stepEvents: Record<string, StepEvent[]> = {};
    const stepStatuses: Record<string, string> = { ...initialStatuses };
    const stepResults: Record<string, string> = {};
    const stepTraces: Record<string, TraceData> = {};
    let streamingContent = '';
    let finalResult = '';

    // ---- local helpers ----

    const addStepEvent = (stepName: string | undefined, type: string, data: Record<string, unknown>) => {
      if (!stepName) return;
      if (!stepEvents[stepName]) {
        stepEvents[stepName] = [];
      }
      stepEvents[stepName].push({
        time: new Date().toLocaleTimeString(),
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

    // run-context (sent when the run context is available)
    eventSource.addEventListener('run-context', (e: MessageEvent) => {
      try {
        const data = JSON.parse(e.data) as RunContext;
        setExecutionModal(prev => ({ ...prev, runContext: data }));
      } catch { /* ignore */ }
    });

    // step-started
    eventSource.addEventListener('step-started', (e: MessageEvent) => {
      try {
        const data: SSEEventData = JSON.parse(e.data);
        updateStepStatus(data.stepName, 'running');
        addStepEvent(data.stepName, 'step-started', data as Record<string, unknown>);
      } catch { /* ignore */ }
    });

    // step-completed
    eventSource.addEventListener('step-completed', (e: MessageEvent) => {
      try {
        const data: SSEEventData = JSON.parse(e.data);
        updateStepStatus(data.stepName, 'completed');
        addStepEvent(data.stepName, 'step-completed', data as Record<string, unknown>);
      } catch { /* ignore */ }
    });

    // step-error
    eventSource.addEventListener('step-error', (e: MessageEvent) => {
      try {
        const data: SSEEventData = JSON.parse(e.data);
        updateStepStatus(data.stepName, 'failed');
        addStepEvent(data.stepName, 'step-error', data as Record<string, unknown>);
      } catch { /* ignore */ }
    });

    // step-cancelled
    eventSource.addEventListener('step-cancelled', (e: MessageEvent) => {
      try {
        const data: SSEEventData = JSON.parse(e.data);
        updateStepStatus(data.stepName, 'cancelled');
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
        addStepEvent(data.stepName, 'step-trace', { hasTrace: true });
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
          addStepEvent(data.stepName, 'content-delta', data as Record<string, unknown>);
        }
      } catch { /* ignore */ }
    });

    // tool events
    (['tool-started', 'tool-completed'] as const).forEach(eventType => {
      eventSource.addEventListener(eventType, (e: MessageEvent) => {
        try {
          const data: SSEEventData = JSON.parse(e.data);
          addStepEvent(data.stepName, eventType, data as Record<string, unknown>);
        } catch { /* ignore */ }
      });
    });

    // subagent events
    (['subagent-selected', 'subagent-started', 'subagent-completed', 'subagent-failed', 'subagent-deselected'] as const).forEach(eventType => {
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

    // usage, loop-iteration, model-mismatch
    (['usage', 'loop-iteration', 'model-mismatch'] as const).forEach(eventType => {
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
          setExecutionModal(prev => ({ ...prev, executionId: data.executionId as string }));
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
        for (const [stepName, stepData] of Object.entries(data.results as Record<string, { status?: string }>)) {
          if (stepData.status) {
            updatedStatuses[stepName] = statusMap[stepData.status] || 'completed';
          }
        }
        // Mark the step that triggered early completion with a distinct status for DAG visualization
        if (data.completedByStep && updatedStatuses[data.completedByStep]) {
          updatedStatuses[data.completedByStep] = 'completed_early';
        }
        setExecutionModal(prev => ({
          ...prev,
          stepStatuses: { ...prev.stepStatuses, ...updatedStatuses },
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
      streamingContent: '',
      finalResult: '',
      status: 'running',
      errorMessage: null,
      completedByStep: null,
      runContext: null,
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

  // ── Cancel running orchestration ──────────────────────────────────────────

  const cancelExecution = async (executionId: string | null): Promise<void> => {
    if (!executionId) return;
    try {
      setExecutionModal(prev => ({ ...prev, status: 'cancelling' }));
      await api.post(`/api/active/${executionId}/cancel`);

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
      streamingContent: '',
      finalResult: '',
      status: execution.status === 'Cancelling' ? 'cancelling' : 'running',
      errorMessage: null,
      completedByStep: null,
      runContext: null,
    });

    try {
      const eventSource = new EventSource(`/api/execution/${execution.executionId}/attach`);
      eventSourceRef.current = eventSource;
      wireEventSource(eventSource, initialStatuses);
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
      streamingContent: '',
      finalResult: '',
      status: 'loading',
      errorMessage: null,
      completedByStep: null,
      runContext: null,
    });

    try {
      const details = await api.get<ExecutionDetailsResponse>(
        `/api/history/${encodeURIComponent(exec.orchestrationName)}/${encodeURIComponent(exec.runId)}`
      );

      const stepStatuses: Record<string, string> = {};
      const stepEvents: Record<string, StepEvent[]> = {};
      const stepResults: Record<string, string> = {};
      const stepTraces: Record<string, TraceData> = {};
      const finalResult = details.finalContent || '';

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

          if (step.content) {
            stepResults[step.name] = step.content;
          }

          if (step.trace) {
            stepTraces[step.name] = step.trace as unknown as TraceData;
          }

          // Create events from step data
          stepEvents[step.name] = [];

          // Add step started event
          stepEvents[step.name].push({
            time: step.startedAt ? new Date(step.startedAt).toLocaleTimeString() : '',
            type: 'step-started',
          } as StepEvent);

          // Add model info if available
          if (step.actualModel) {
            stepEvents[step.name].push({
              time: step.startedAt ? new Date(step.startedAt).toLocaleTimeString() : '',
              type: 'session-started',
              selectedModel: step.actualModel,
            } as StepEvent);
          }

          // Add usage info if available
          if (step.usage) {
            stepEvents[step.name].push({
              time: step.completedAt ? new Date(step.completedAt).toLocaleTimeString() : '',
              type: 'usage',
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
              contentPreview: step.content
                ? step.content.substring(0, 200) + (step.content.length > 200 ? '...' : '')
                : undefined,
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
        streamingContent: finalResult,
        finalResult,
        status: modalStatus,
        errorMessage: null,
        completedByStep: details.completedByStep || null,
        runContext: details.context || null,
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
        </div>

        <div className="orchestrations-list" role="listbox" aria-label="Orchestrations">
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
              <button
                className={`history-filter-btn${hideIncomplete ? ' active' : ''}`}
                onClick={toggleHideIncomplete}
                title={hideIncomplete ? 'Showing completed only — click to show all' : 'Showing all — click to hide incomplete'}
                aria-label={hideIncomplete ? 'Show incomplete executions' : 'Hide incomplete executions'}
                aria-pressed={hideIncomplete}
              >
                <Icons.Filter />
              </button>
              <button className="btn btn-sm" onClick={() => { setHistoryModal({ open: true }); setSidebarOpen(false); }}>
                Show All
              </button>
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
                <div
                  key={exec.runId}
                  className="history-item"
                  role="listitem"
                  tabIndex={0}
                  onClick={() => {
                    if (exec.isActive && exec.executionId) {
                      const orch = orchestrations?.find(o => o.id === exec.orchestrationId);
                      attachToExecution(exec, orch);
                    } else {
                      viewHistoricalExecution(exec);
                    }
                    setSidebarOpen(false);
                  }}
                  onKeyDown={(e: React.KeyboardEvent) => {
                    if (e.key === 'Enter' || e.key === ' ') {
                      e.preventDefault();
                      if (exec.isActive && exec.executionId) {
                        const orch = orchestrations?.find(o => o.id === exec.orchestrationId);
                        attachToExecution(exec, orch);
                      } else {
                        viewHistoricalExecution(exec);
                      }
                      setSidebarOpen(false);
                    }
                  }}
                  aria-label={`${exec.orchestrationName} - ${exec.status || 'Running'} - ${formatTime(exec.startedAt)}`}
                >
                  <div className={`history-status-icon ${(exec.isIncomplete || exec.completionReason) && exec.status === 'Succeeded' ? 'completed-early' : exec.status?.toLowerCase() || 'running'}`} aria-hidden="true">
                    {exec.isActive ? (
                      <span className="spinner" style={{ width: '12px', height: '12px' }}></span>
                    ) : exec.status === 'Succeeded' && (exec.completionReason || exec.isIncomplete) ? (
                      <Icons.SkipForward />
                    ) : exec.status === 'Succeeded' ? (
                      <Icons.Check />
                    ) : exec.status === 'Failed' ? (
                      <Icons.X />
                    ) : exec.status === 'Cancelled' ? (
                      <Icons.Ban />
                    ) : (
                      '...'
                    )}
                  </div>
                  <div className="history-info">
                    <div className="history-name">
                      {exec.orchestrationName}
                      {exec.isActive && (
                        <span className="step-status-badge running" style={{
                          marginLeft: '8px',
                          fontSize: '10px',
                          padding: '2px 6px',
                        }}>
                          {exec.status === 'Cancelling' ? 'Cancelling' : 'Running'}
                        </span>
                      )}
                    </div>
                    <div className="history-time">{formatTime(exec.startedAt)}</div>
                  </div>
                  {!exec.isActive && (
                    <button
                      className="history-delete-btn"
                      onClick={(e: React.MouseEvent) => deleteHistoryEntry(exec, e)}
                      title="Delete execution"
                      aria-label={`Delete ${exec.orchestrationName} execution`}
                    >
                      <Icons.X />
                    </button>
                  )}
                </div>
              ))
            )}
          </div>
          )}
        </div>
      </nav>

      {/* Main Pane */}
      <main id="main-content" className="main-pane">
        <div className="main-header">
          <div style={{ display: 'flex', alignItems: 'center', gap: '10px' }}>
            <button
              className="mobile-menu-btn"
              onClick={() => setSidebarOpen(prev => !prev)}
              aria-label="Toggle sidebar menu"
            >
              <Icons.Menu />
            </button>
            <span className="main-title">Active Orchestrations</span>
          </div>
          <div className="main-actions">
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
            <span className="text-muted" style={{ fontSize: '12px', marginRight: '8px' }}>
              {orchestrationView.running.length} running, {orchestrationView.enabled.length} enabled
              {orchestrationView.disabled.length > 0 && (
                <>, {orchestrationView.disabled.length} disabled</>
              )}
            </span>
            <button className="btn" onClick={loadData}>Refresh</button>
          </div>
        </div>

        <div className="cards-container" style={{ overflow: 'auto' }}>
          {orchestrationView.running.length === 0
            && orchestrationView.enabled.length === 0
            && orchestrationView.disabled.length === 0 ? (
            <div className="empty-state">
              <div className="empty-icon"><Icons.Activity /></div>
              <div className="empty-title">No Orchestrations</div>
              <div className="empty-text">
                {mainPaneProfileFilter.length > 0
                  ? 'No orchestrations match the selected profile filter.'
                  : 'Add orchestrations to get started.'}
              </div>
            </div>
          ) : (
            <div style={{ display: 'flex', flexDirection: 'column', gap: '24px' }}>
              {/* Running Section */}
              {orchestrationView.running.length > 0 && (
                <div>
                  <div className="cards-section-header">
                    <span className="cards-section-dot cards-section-dot-running"></span>
                    Running ({orchestrationView.running.length})
                  </div>
                  <div className="cards-grid">
                    {orchestrationView.running.map(exec => (
                      <ActiveOrchestrationCard
                        key={exec.executionId || exec.orchestrationId}
                        execution={exec}
                        type="running"
                        orchestrations={orchestrations}
                        profiles={profiles}
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
              {orchestrationView.enabled.length > 0 && (
                <div>
                  <div className="cards-section-header">
                    <span className="cards-section-dot cards-section-dot-pending"></span>
                    Enabled ({orchestrationView.enabled.length})
                  </div>
                  <div className="cards-grid">
                    {orchestrationView.enabled.map(exec => (
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
                      />
                    ))}
                  </div>
                </div>
              )}

              {/* Disabled Section */}
              {orchestrationView.disabled.length > 0 && (
                <div>
                  <div className="cards-section-header">
                    <span className="cards-section-dot cards-section-dot-disabled"></span>
                    Disabled ({orchestrationView.disabled.length})
                  </div>
                  <div className="cards-grid">
                    {orchestrationView.disabled.map(exec => (
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
        orchestrations={orchestrations}
      />
      <AddModal
        {...addModal}
        onClose={() => setAddModal({ open: false })}
        onAdded={loadData}
      />
      <RunModal
        {...runModal}
        onClose={() => setRunModal({ open: false, orchestration: null })}
        onRun={(params: Record<string, string>) => {
          setRunModal({ open: false, orchestration: null });
          runOrchestration(runModal.orchestration?.id, params);
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
            streamingContent: '',
            finalResult: '',
            status: 'idle',
            errorMessage: null,
            completedByStep: null,
            runContext: null,
          });
        }}
        onCancel={() => cancelExecution(executionModal.executionId)}
      />
      <McpsModal
        {...mcpsModal}
        onClose={() => setMcpsModal({ open: false })}
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
