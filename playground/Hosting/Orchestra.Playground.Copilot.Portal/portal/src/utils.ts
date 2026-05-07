import type { Profile, ProfileFilter } from './types';

export type PortalStepStatus =
  | 'pending'
  | 'running'
  | 'completed'
  | 'completed_restored'
  | 'completed_early'
  | 'failed'
  | 'cancelled'
  | 'skipped'
  | 'noaction';

export function buildRestoredStepStatusUpdates(stepsRestored: unknown): Record<string, PortalStepStatus> {
  if (!Array.isArray(stepsRestored)) return {};

  const updates: Record<string, PortalStepStatus> = {};
  for (const stepName of stepsRestored) {
    if (typeof stepName === 'string' && stepName.length > 0) {
      updates[stepName] = 'completed_restored';
    }
  }
  return updates;
}

export function formatTimeAgo(dateStr: string | null | undefined): string {
  if (!dateStr) return 'Unknown';
  const date = new Date(dateStr);
  const now = new Date();
  const diffMs = now.getTime() - date.getTime();
  const diffSec = Math.floor(diffMs / 1000);
  if (diffSec < 60) return `${diffSec}s ago`;
  const diffMin = Math.floor(diffSec / 60);
  if (diffMin < 60) return `${diffMin}m ago`;
  const diffHr = Math.floor(diffMin / 60);
  return `${diffHr}h ${diffMin % 60}m ago`;
}

export function formatTimeUntil(dateStr: string | null | undefined): string {
  if (!dateStr) return 'Unknown';
  const date = new Date(dateStr);
  const now = new Date();
  const diffMs = date.getTime() - now.getTime();
  if (diffMs <= 0) return 'Now';
  const diffSec = Math.floor(diffMs / 1000);
  if (diffSec < 60) return `in ${diffSec}s`;
  const diffMin = Math.floor(diffSec / 60);
  if (diffMin < 60) return `in ${diffMin}m ${diffSec % 60}s`;
  const diffHr = Math.floor(diffMin / 60);
  return `in ${diffHr}h ${diffMin % 60}m`;
}

export function formatTime(dateStr: string | null | undefined): string {
  if (!dateStr) return '';
  const d = new Date(dateStr);
  return d.toLocaleString([], {
    year: 'numeric',
    month: 'short',
    day: 'numeric',
    hour: '2-digit',
    minute: '2-digit',
  });
}

/**
 * Returns true if a history entry represents an incomplete/early-exit execution.
 * An execution is considered incomplete when:
 * - `isIncomplete` flag is true, OR
 * - it has a `completionReason` AND status is 'Succeeded' (early exit via orchestra_complete)
 */
export function isIncompleteExecution(exec: {
  isActive?: boolean;
  isIncomplete?: boolean;
  completionReason?: string;
  status?: string;
}): boolean {
  if (exec.isActive) return false;
  return !!(exec.isIncomplete || (exec.completionReason && exec.status === 'Succeeded'));
}

// ── Profile / tag filtering helpers ──────────────────────────────────────────

/**
 * Checks whether a profile filter matches an orchestration based on its filter rules.
 * Uses case-insensitive tag comparison to match the backend ProfileFilter.Matches() behavior.
 */
export function profileFilterMatchesOrchestration(
  filter: ProfileFilter,
  orchestrationId: string,
  orchestrationTags: string[] | undefined,
): boolean {
  // Excluded IDs always take precedence
  if (filter.excludeOrchestrationIds?.length > 0 && filter.excludeOrchestrationIds.includes(orchestrationId))
    return false;
  // Explicit ID inclusion
  if (filter.orchestrationIds?.length > 0 && filter.orchestrationIds.includes(orchestrationId))
    return true;
  // Wildcard matches everything
  if (filter.tags?.includes('*'))
    return true;
  // Tag intersection (case-insensitive, matching backend OrdinalIgnoreCase behavior)
  if (filter.tags?.length > 0 && orchestrationTags?.length) {
    return filter.tags.some(t => orchestrationTags.some(ot => ot.toLowerCase() === t.toLowerCase()));
  }
  return false;
}

/**
 * Returns all profiles whose filter matches the given orchestration.
 */
export function getMatchingProfiles(
  profiles: Profile[],
  orchestrationId: string,
  orchestrationTags: string[] | undefined,
): Profile[] {
  return profiles.filter(p => profileFilterMatchesOrchestration(p.filter, orchestrationId, orchestrationTags));
}

/**
 * Checks if an orchestration (by ID) matches any of the given selected profile IDs.
 * Returns true if no profile filter is applied (empty selection = show all).
 */
export function orchestrationMatchesProfileFilter(
  orchId: string,
  orchTags: string[] | undefined,
  selectedProfileIds: string[],
  profiles: Profile[],
): boolean {
  if (selectedProfileIds.length === 0) return true;
  const selectedProfiles = profiles.filter(p => selectedProfileIds.includes(p.id));
  return selectedProfiles.some(sp => profileFilterMatchesOrchestration(sp.filter, orchId, orchTags));
}

type SearchableMcp = string | { name?: string };
type SearchableSubagent = { name?: string; displayName?: string; description?: string };
type SearchableStep = string | {
  name?: string;
  model?: string;
  mcps?: SearchableMcp[];
  skillDirectories?: string[];
  subagents?: SearchableSubagent[];
};
type SearchableOrchestration = {
  name?: string;
  description?: string;
  triggerType?: string;
  trigger?: { type?: string };
  tags?: string[];
  steps?: SearchableStep[];
  mcps?: SearchableMcp[];
  models?: string[];
};
type SearchableActiveExecution = {
  executionId?: string;
  orchestrationName?: string;
  status?: string;
  triggeredBy?: string;
  currentStep?: string;
  webhookUrl?: string;
};

function normalizeSearchQuery(query: string): string {
  return query.trim().toLowerCase();
}

function fieldMatchesSearch(value: string | undefined, query: string): boolean {
  return !!value?.toLowerCase().includes(query);
}

function mcpMatchesSearch(mcp: SearchableMcp, query: string): boolean {
  const name = typeof mcp === 'string' ? mcp : mcp.name;
  return fieldMatchesSearch(name, query);
}

/**
 * Checks whether an orchestration matches a text search query.
 * Searches name, description, trigger type, tags, step names, MCPs, models, skills, and subagents.
 */
export function orchestrationMatchesSearch(
  orch: SearchableOrchestration,
  query: string,
): boolean {
  const q = normalizeSearchQuery(query);
  if (!q) return true;

  return !!(
    fieldMatchesSearch(orch.name, q) ||
    fieldMatchesSearch(orch.description, q) ||
    fieldMatchesSearch(orch.triggerType, q) ||
    fieldMatchesSearch(orch.trigger?.type, q) ||
    orch.tags?.some(tag => fieldMatchesSearch(tag, q)) ||
    orch.mcps?.some(mcp => mcpMatchesSearch(mcp, q)) ||
    orch.models?.some(model => fieldMatchesSearch(model, q)) ||
    orch.steps?.some(step => {
      if (typeof step === 'string') return fieldMatchesSearch(step, q);
      return !!(
        fieldMatchesSearch(step.name, q) ||
        fieldMatchesSearch(step.model, q) ||
        step.mcps?.some(mcp => mcpMatchesSearch(mcp, q)) ||
        step.skillDirectories?.some(dir => fieldMatchesSearch(dir, q)) ||
        step.subagents?.some(subagent =>
          fieldMatchesSearch(subagent.name, q) ||
          fieldMatchesSearch(subagent.displayName, q) ||
          fieldMatchesSearch(subagent.description, q)
        )
      );
    })
  );
}

/**
 * Checks whether an Active Orchestrations card matches a text search query.
 * Includes runtime-only fields such as execution ID and current step.
 */
export function activeOrchestrationMatchesSearch(
  execution: SearchableActiveExecution,
  orchestration: SearchableOrchestration | undefined,
  query: string,
): boolean {
  const q = normalizeSearchQuery(query);
  if (!q) return true;

  return !!(
    (orchestration && orchestrationMatchesSearch(orchestration, q)) ||
    fieldMatchesSearch(execution.orchestrationName, q) ||
    fieldMatchesSearch(execution.executionId, q) ||
    fieldMatchesSearch(execution.status, q) ||
    fieldMatchesSearch(execution.triggeredBy, q) ||
    fieldMatchesSearch(execution.currentStep, q) ||
    fieldMatchesSearch(execution.webhookUrl, q)
  );
}
