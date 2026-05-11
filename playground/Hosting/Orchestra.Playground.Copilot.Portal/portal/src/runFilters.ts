/**
 * Run origin classification — mirrors the C# RunOriginKind enum.
 *
 * Wire-format string returned on the `origin` field of every row from
 * /api/history, /api/history/all, and /api/history/search. The portal uses it
 * to drive icon selection, the filter combo, and the "Children only" /
 * "Roots only" scope filter.
 *
 * Source of truth: src/Orchestra.Host/Api/RunOriginClassifier.cs (ToWireValue)
 */
export type RunOrigin =
  | 'manual'
  | 'scheduler'
  | 'loop'
  | 'webhook'
  | 'mcp'
  | 'orchestration'
  | 'retry'
  | 'resume'
  | 'unknown';

export const ALL_RUN_ORIGINS: readonly RunOrigin[] = [
  'manual', 'scheduler', 'loop', 'webhook', 'mcp', 'orchestration', 'retry', 'resume',
] as const;

/** Human-readable label for each origin (used in the filter combo). */
export const RUN_ORIGIN_LABELS: Record<RunOrigin, string> = {
  manual: 'Manual',
  scheduler: 'Scheduler',
  loop: 'Loop',
  webhook: 'Webhook',
  mcp: 'MCP',
  orchestration: 'Child orchestration',
  retry: 'Retry',
  resume: 'Resume',
  unknown: 'Unknown',
};

/**
 * Classifies a free-form `triggeredBy` string the same way the server does.
 * Used as a fallback when the server hasn't projected the `origin` field
 * (older endpoints, mocks). The server-side projection is preferred.
 */
export function classifyRunOrigin(triggeredBy: string | undefined | null): RunOrigin {
  if (!triggeredBy) return 'unknown';
  const t = triggeredBy.toLowerCase();
  if (t.startsWith('orchestration:')) return 'orchestration';
  switch (t) {
    case 'manual': return 'manual';
    case 'scheduler': return 'scheduler';
    case 'loop': return 'loop';
    case 'webhook': return 'webhook';
    case 'mcp': return 'mcp';
    case 'retry': return 'retry';
    case 'resume': return 'resume';
    default: return 'unknown';
  }
}

// ── Status filter ────────────────────────────────────────────────────────────

/** Statuses the filter combo offers. Match the server's ExecutionStatus + active states. */
export type RunStatusFilterValue =
  | 'Running'
  | 'Succeeded'
  | 'Failed'
  | 'Cancelled';

export const ALL_RUN_STATUS_FILTERS: readonly RunStatusFilterValue[] =
  ['Running', 'Succeeded', 'Failed', 'Cancelled'] as const;

// ── Scope filter ─────────────────────────────────────────────────────────────

/**
 * Tri-state scope filter:
 *  - 'all'      — no scope filter (default)
 *  - 'roots'    — only top-level runs (ParentExecutionId is null)
 *  - 'children' — only nested runs (ParentExecutionId is not null)
 */
export type RunScopeFilter = 'all' | 'roots' | 'children';

// ── Aggregate filter state ──────────────────────────────────────────────────

/**
 * Persisted shape of the sidebar filter combo state. Stored in localStorage
 * under {@link FILTER_STORAGE_KEY}.
 */
export interface HistoryFilterState {
  scope: RunScopeFilter;
  /** Allow-list. When the set equals ALL_RUN_ORIGINS no `?origins=` query is sent. */
  origins: RunOrigin[];
  /** Allow-list. When the set equals ALL_RUN_STATUS_FILTERS no `?statuses=` query is sent. */
  statuses: RunStatusFilterValue[];
  /** Mirrors the previous standalone "Hide incomplete" button. */
  hideIncomplete: boolean;
}

export const DEFAULT_FILTER_STATE: HistoryFilterState = {
  scope: 'all',
  origins: [...ALL_RUN_ORIGINS],
  statuses: [...ALL_RUN_STATUS_FILTERS],
  hideIncomplete: true,
};

export const FILTER_STORAGE_KEY = 'orchestra-history-filters.v1';

/** Returns true when the user has deviated from the default state. */
export function isFilterStateDefault(state: HistoryFilterState): boolean {
  return (
    state.scope === DEFAULT_FILTER_STATE.scope &&
    state.hideIncomplete === DEFAULT_FILTER_STATE.hideIncomplete &&
    state.origins.length === ALL_RUN_ORIGINS.length &&
    state.statuses.length === ALL_RUN_STATUS_FILTERS.length
  );
}

/**
 * Builds the query string fragment for /api/history.
 *
 * Returns an empty string when the state matches the defaults so URLs stay
 * tidy and the response cache key is stable for unfiltered requests.
 */
export function buildFilterQueryString(state: HistoryFilterState): string {
  const parts: string[] = [];

  // Origins: only emit the parameter when the user has narrowed the set.
  if (state.origins.length < ALL_RUN_ORIGINS.length && state.origins.length > 0) {
    parts.push(`origins=${state.origins.join(',')}`);
  }

  // Statuses: same rule.
  if (state.statuses.length < ALL_RUN_STATUS_FILTERS.length && state.statuses.length > 0) {
    parts.push(`statuses=${state.statuses.join(',')}`);
  }

  // Scope: tri-state -> ?roots= boolean (omit for 'all').
  if (state.scope === 'roots') {
    parts.push('roots=true');
  } else if (state.scope === 'children') {
    parts.push('roots=false');
  }

  return parts.length === 0 ? '' : `&${parts.join('&')}`;
}

/**
 * Loads the filter state from localStorage, falling back to {@link DEFAULT_FILTER_STATE}
 * for missing or malformed entries. Always returns a fully-populated state so consumers
 * can treat `undefined` as a non-issue.
 */
export function loadFilterState(): HistoryFilterState {
  try {
    const raw = localStorage.getItem(FILTER_STORAGE_KEY);
    if (!raw) return DEFAULT_FILTER_STATE;
    const parsed = JSON.parse(raw) as Partial<HistoryFilterState> | null;
    if (!parsed || typeof parsed !== 'object') return DEFAULT_FILTER_STATE;

    const scope: RunScopeFilter =
      parsed.scope === 'roots' || parsed.scope === 'children' || parsed.scope === 'all'
        ? parsed.scope
        : DEFAULT_FILTER_STATE.scope;

    const origins = Array.isArray(parsed.origins)
      ? (parsed.origins.filter((v): v is RunOrigin => ALL_RUN_ORIGINS.includes(v as RunOrigin)))
      : [...ALL_RUN_ORIGINS];

    const statuses = Array.isArray(parsed.statuses)
      ? (parsed.statuses.filter((v): v is RunStatusFilterValue => ALL_RUN_STATUS_FILTERS.includes(v as RunStatusFilterValue)))
      : [...ALL_RUN_STATUS_FILTERS];

    const hideIncomplete = typeof parsed.hideIncomplete === 'boolean'
      ? parsed.hideIncomplete
      : DEFAULT_FILTER_STATE.hideIncomplete;

    return { scope, origins, statuses, hideIncomplete };
  } catch {
    return DEFAULT_FILTER_STATE;
  }
}

/** Persists the state to localStorage. Failures are swallowed (private mode etc.). */
export function saveFilterState(state: HistoryFilterState): void {
  try {
    localStorage.setItem(FILTER_STORAGE_KEY, JSON.stringify(state));
  } catch {
    /* ignore */
  }
}
