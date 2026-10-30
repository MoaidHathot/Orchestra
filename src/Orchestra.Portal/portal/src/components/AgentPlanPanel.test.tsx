import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import '@testing-library/jest-dom';
import AgentPlanPanel from './AgentPlanPanel';
import type { AgentPlanRevision } from '../types';
import { mergePlanRevision, planRevisionKey } from '../agentPlans';

const revision = (number: number, status = 'pending', attempt = 'attempt'): AgentPlanRevision => ({
  schemaVersion: 1, run: { runId: 'run', orchestrationName: 'workflow', orchestrationVersion: '1', triggeredBy: 'manual', startedAt: '2026-10-30T00:00:00Z' },
  stepName: 'step', stepExecutionId: attempt, streamId: 'stream', revision: number, capturedAt: `2026-10-30T00:00:0${number}Z`,
  plan: { provider: 'copilot', sessionId: 'session', source: 'sql-todos', shared: true, title: 'Tasks', tasks: [{ id: 'task', title: 'Verify change', status }], dependencies: [] },
});

afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

describe('AgentPlanPanel', () => {
  it('updates reported progress without enabling edits', () => {
    const view = render(<AgentPlanPanel runId="run" stepName="step" plans={[revision(1)]} />);
    expect(screen.getByText('0 of 1 tasks completed')).toBeInTheDocument();
    expect(screen.getByRole('checkbox', { name: 'Verify change' })).toBeDisabled();
    view.rerender(<AgentPlanPanel runId="run" stepName="step" plans={[revision(2, 'completed')]} />);
    expect(screen.getByText('1 of 1 tasks completed')).toBeInTheDocument();
    expect(screen.getByRole('checkbox', { name: 'Verify change' })).toBeChecked();
  });

  it('shows empty, disabled, and stale states', () => {
    const view = render(<AgentPlanPanel runId={null} stepName="step" />);
    expect(screen.getByText('No plan reported.')).toBeInTheDocument();
    view.rerender(<AgentPlanPanel runId={null} stepName="step" enabled={false} />);
    expect(screen.getByText('Plan tracking is disabled for this step.')).toBeInTheDocument();
    const stale = revision(1); stale.plan.captureError = 'read timeout';
    view.rerender(<AgentPlanPanel runId={null} stepName="step" plans={[stale]} />);
    expect(screen.getByRole('alert')).toHaveTextContent('read timeout');
    expect(screen.getByText('Verify change')).toBeInTheDocument();
  });

  it('renders Markdown through the sanitized document renderer', () => {
    const plan = revision(1); plan.plan.markdown = '# Verification document\n<script>alert(1)</script>\n**Run tests**';
    const view = render(<AgentPlanPanel runId={null} stepName="step" plans={[plan]} />);
    expect(screen.getByRole('heading', { name: 'Verification document' })).toBeInTheDocument();
    expect(view.container.querySelector('script')).toBeNull();
  });

  it('loads paginated durable history and can view a previous revision', async () => {
    const fetcher = vi.fn()
      .mockResolvedValueOnce({ ok: true, json: async () => ({ items: [revision(1)], nextCursor: 1, isPartial: false }) })
      .mockResolvedValueOnce({ ok: true, json: async () => ({ items: [revision(0)], nextCursor: null, isPartial: false }) });
    vi.stubGlobal('fetch', fetcher);
    render(<AgentPlanPanel runId="run" stepName="step" plans={[revision(2, 'completed')]} />);
    fireEvent.click(screen.getByRole('button', { name: 'Revision history' }));
    await screen.findByRole('option', { name: /revision 1/ });
    fireEvent.change(screen.getByRole('combobox', { name: 'Plan revision' }), { target: { value: planRevisionKey(revision(1)) } });
    expect(screen.getByText('0 of 1 tasks completed')).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Load more revisions' }));
    await screen.findByRole('option', { name: /revision 0/ });
    expect(fetcher.mock.calls[1][0]).toContain('after=1');
    expect(fetcher.mock.calls[0][1].signal).toBeDefined();
  });

  it('cancels in-flight history requests when the panel is removed', () => {
    const fetcher = vi.fn(() => new Promise(() => {}));
    vi.stubGlobal('fetch', fetcher);
    const view = render(<AgentPlanPanel runId="run" stepName="step" />);
    fireEvent.click(screen.getByRole('button', { name: 'Revision history' }));
    const signal = (fetcher.mock.calls[0] as unknown as [string, RequestInit])[1].signal!;
    view.unmount();
    expect(signal.aborted).toBe(true);
  });

  it('reads a restored loop plan from its captured run and original step name', async () => {
    const empty = revision(2); empty.plan.tasks = [];
    const fetcher = vi.fn().mockResolvedValue({ ok: true, json: async () => ({ items: [empty], nextCursor: null, isPartial: false }) });
    vi.stubGlobal('fetch', fetcher);
    render(<AgentPlanPanel runId="retried-run" stepName="step:iteration-1" plans={[empty]} />);
    fireEvent.click(screen.getByRole('button', { name: 'Revision history' }));
    await screen.findByRole('option', { name: /revision 2/ });
    expect(fetcher.mock.calls[0][0]).toContain('/api/execution/run/plans?stepName=step&');
    fireEvent.change(screen.getByLabelText('Plan revision'), { target: { value: planRevisionKey(empty) } });
    expect(screen.getByText('This revision contains no tasks or plan text.')).toBeInTheDocument();
  });

  it('resets history and aborts the old request when switching runs', () => {
    const fetcher = vi.fn(() => new Promise(() => {}));
    vi.stubGlobal('fetch', fetcher);
    const view = render(<AgentPlanPanel runId="one" stepName="step" />);
    fireEvent.click(screen.getByRole('button', { name: 'Revision history' }));
    const signal = (fetcher.mock.calls[0] as unknown as [string, RequestInit])[1].signal!;
    view.rerender(<AgentPlanPanel runId="two" stepName="step" />);
    expect(signal.aborted).toBe(true);
    expect(screen.queryByLabelText('Plan revision')).not.toBeInTheDocument();
  });

  it('shows the API diagnostic when durable history is unavailable', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(JSON.stringify({ title: 'Unavailable', detail: 'This run store has no plan history.' }), { status: 501 })));
    render(<AgentPlanPanel runId="run" stepName="step" />);
    fireEvent.click(screen.getByRole('button', { name: 'Revision history' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('This run store has no plan history.');
  });

  it('preserves snapshot state when stale replay or a previous attempt arrives', () => {
    const current = [revision(3, 'completed')];
    expect(mergePlanRevision(current, revision(1))).toBe(current);
    const next = revision(4, 'pending', 'next-attempt');
    expect(mergePlanRevision(current, next)).toEqual([next]);
    expect(mergePlanRevision([next], revision(3))).toEqual([next]);
  });
});
