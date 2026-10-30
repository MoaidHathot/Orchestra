import type { AgentPlanRevision } from './types';

export const planRevisionKey = (revision: AgentPlanRevision): string =>
  `${revision.stepExecutionId}:${revision.streamId}:${revision.revision}`;

/** Keep current-step observations authoritative when older SSE replay follows a snapshot. */
export function mergePlanRevision(current: AgentPlanRevision[], incoming: AgentPlanRevision): AgentPlanRevision[] {
  const differentAttempt = current.some(p => p.stepExecutionId !== incoming.stepExecutionId);
  if (differentAttempt && current.some(p => p.capturedAt > incoming.capturedAt)) return current;
  const plans = differentAttempt ? [] : current;
  const existing = plans.find(p => p.streamId === incoming.streamId);
  if (existing && existing.revision >= incoming.revision) return current;
  return [...plans.filter(p => p.streamId !== incoming.streamId), incoming];
}

export function mergePlanHistory(current: AgentPlanRevision[], incoming: AgentPlanRevision[]): AgentPlanRevision[] {
  const revisions = new Map(current.map(p => [planRevisionKey(p), p]));
  incoming.forEach(p => revisions.set(planRevisionKey(p), p));
  return [...revisions.values()].sort((a, b) => b.capturedAt.localeCompare(a.capturedAt));
}
