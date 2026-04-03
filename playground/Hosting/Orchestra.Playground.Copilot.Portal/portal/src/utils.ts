import type { Profile, ProfileFilter } from './types';

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
  return d.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
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

/**
 * Checks whether an orchestration matches a text search query.
 * Searches name, description, trigger type, tags, and step names.
 */
export function orchestrationMatchesSearch(
  orch: { name?: string; description?: string; triggerType?: string; tags?: string[]; steps?: ({ name?: string } | string)[] },
  query: string,
): boolean {
  if (!query) return true;
  const q = query.toLowerCase();
  return !!(
    orch.name?.toLowerCase().includes(q) ||
    orch.description?.toLowerCase().includes(q) ||
    orch.triggerType?.toLowerCase().includes(q) ||
    orch.tags?.some(tag => tag.toLowerCase().includes(q)) ||
    orch.steps?.some(step => {
      const stepName = typeof step === 'string' ? step : step.name;
      return stepName?.toLowerCase().includes(q);
    })
  );
}
