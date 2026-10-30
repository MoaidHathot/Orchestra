import React, { useEffect, useState } from 'react';
import type { AgentPlanRevision, PlanHistoryPage } from '../types';
import { mergePlanHistory, planRevisionKey } from '../agentPlans';
import MarkdownContent from './MarkdownContent';

const emptyPlans: AgentPlanRevision[] = [];

export default function AgentPlanPanel({ runId, stepName, plans = emptyPlans, enabled = true }: {
  runId: string | null; stepName: string; plans?: AgentPlanRevision[]; enabled?: boolean;
}): React.JSX.Element {
  const [historyOpen, setHistoryOpen] = useState(false);
  const [history, setHistory] = useState<AgentPlanRevision[]>([]);
  const [after, setAfter] = useState(0);
  const [nextCursor, setNextCursor] = useState<number | null>(null);
  const [selected, setSelected] = useState('current');
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [partial, setPartial] = useState(false);
  const [refresh, setRefresh] = useState(0);

  useEffect(() => { if (historyOpen) setHistory(previous => mergePlanHistory(previous, plans)); }, [plans, historyOpen]);

  useEffect(() => {
    if (!historyOpen || !runId) return;
    const abort = new AbortController();
    setLoading(true); setError(null);
    const query = new URLSearchParams({ stepName, after: String(after), limit: '100' });
    fetch(`/api/execution/${encodeURIComponent(runId)}/plans?${query}`, { signal: abort.signal })
      .then(async response => {
        if (!response.ok) throw new Error(`Plan history is unavailable (${response.status}).`);
        return await response.json() as PlanHistoryPage;
      })
      .then(page => {
        if (abort.signal.aborted) return;
        setHistory(previous => mergePlanHistory(previous, page.items));
        setNextCursor(page.nextCursor); setPartial(page.isPartial);
      })
      .catch((failure: unknown) => { if (!abort.signal.aborted) setError(failure instanceof Error ? failure.message : 'Could not load plan history.'); })
      .finally(() => { if (!abort.signal.aborted) setLoading(false); });
    return () => abort.abort();
  }, [runId, stepName, historyOpen, after, refresh]);

  const historical = history.find(item => planRevisionKey(item) === selected);
  const displayed = selected === 'current' ? plans : historical ? [historical] : [];
  const meaningful = displayed.filter(item => item.plan.tasks.length || item.plan.markdown || item.plan.captureError);

  return <section aria-label={`Plan for ${stepName}`} style={{ marginBottom: '12px', padding: '12px', border: '1px solid var(--border)', borderRadius: '6px', background: 'var(--surface)' }}>
    <div style={{ display: 'flex', alignItems: 'center', gap: '10px', flexWrap: 'wrap' }}>
      <h4 style={{ margin: 0 }}>Plan</h4>
      <span style={{ color: 'var(--text-dim)', fontSize: '12px' }}>Agent-reported progress</span>
      {runId && <button type="button" className="btn btn-sm" aria-expanded={historyOpen} onClick={() => setHistoryOpen(open => !open)}>Revision history</button>}
    </div>
    {!enabled && <p>Plan tracking is disabled for this step.</p>}
    {enabled && meaningful.length === 0 && <p style={{ color: 'var(--text-dim)' }}>No plan reported.</p>}
    {historyOpen && <div style={{ display: 'flex', gap: '8px', alignItems: 'center', flexWrap: 'wrap', marginTop: '10px' }}>
      <label style={{ maxWidth: '100%' }}>View revision <select aria-label="Plan revision" value={selected} onChange={event => setSelected(event.target.value)} style={{ maxWidth: '100%', background: 'var(--bg)', color: 'var(--text)', border: '1px solid var(--border)', borderRadius: '4px', padding: '4px' }}>
        <option value="current">Current plans</option>
        {history.map(item => <option key={planRevisionKey(item)} value={planRevisionKey(item)}>
          {item.plan.title ?? item.plan.source} · revision {item.revision} · {new Date(item.capturedAt).toLocaleString()} · {item.stepExecutionId.slice(0, 8)}
        </option>)}
      </select></label>
      <button type="button" className="btn btn-sm" disabled={loading} onClick={() => { setAfter(0); setRefresh(value => value + 1); }}>Refresh history</button>
      {nextCursor !== null && <button type="button" className="btn btn-sm" disabled={loading} onClick={() => setAfter(nextCursor)}>Load more revisions</button>}
      {loading && <span role="status">Loading history…</span>}
      {error && <span role="alert">{error}</span>}
      {partial && <span role="status">Some history records were incomplete.</span>}
    </div>}
    {meaningful.map(revision => {
      const plan = revision.plan;
      const completed = plan.tasks.filter(task => task.status === 'completed').length;
      return <article key={revision.streamId} style={{ marginTop: '12px' }}>
        <strong>{plan.title ?? 'Plan'}</strong>
        <div style={{ color: 'var(--text-dim)', fontSize: '12px' }}>
          {plan.provider} · {plan.source} · {plan.shared ? 'Shared scope' : plan.actorName ?? (plan.actorId ? `Agent ${plan.actorId}` : 'Main agent')}
          {' · '}Session {plan.sessionId} · revision {revision.revision} · {new Date(revision.capturedAt).toLocaleTimeString()}
          {revision.loopIteration != null && ` · iteration ${revision.loopIteration}`}
        </div>
        {plan.captureError && <p role="alert">Latest refresh failed: {plan.captureError}. Showing the last known plan when available.</p>}
        {plan.tasks.length > 0 && <>
          <p>{completed} of {plan.tasks.length} tasks completed</p>
          <ul style={{ listStyle: 'none', paddingLeft: 0 }}>
            {plan.tasks.map((task, index) => <li key={`${task.id ?? ''}:${index}`} style={{ marginBottom: '6px' }}>
              <label><input type="checkbox" checked={task.status === 'completed'} disabled readOnly /> {task.title}</label>
              <span style={{ color: 'var(--text-dim)', marginLeft: '8px' }}>{task.status.replace(/_/g, ' ')}</span>
              {task.description && task.description !== task.title && <div style={{ marginLeft: '22px' }}>{task.description}</div>}
            </li>)}
          </ul>
        </>}
        {plan.dependencies.length > 0 && <details><summary>Task dependencies</summary><ul>{plan.dependencies.map((edge, index) =>
          <li key={index}>{edge.taskId} depends on {edge.dependsOn}</li>)}</ul></details>}
        {plan.markdown && <MarkdownContent markdown={plan.markdown} compact />}
        {plan.documentPath && <div style={{ fontSize: '12px', color: 'var(--text-dim)', overflowWrap: 'anywhere' }}>{plan.documentPath}</div>}
      </article>;
    })}
  </section>;
}
