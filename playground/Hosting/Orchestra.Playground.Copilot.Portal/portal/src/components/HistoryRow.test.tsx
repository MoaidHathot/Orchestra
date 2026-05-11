import { describe, it, expect, vi } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import '@testing-library/jest-dom';
import HistoryRow, { type HistoryRowEntry } from './HistoryRow';

function makeExec(overrides: Partial<HistoryRowEntry> = {}): HistoryRowEntry {
  return {
    runId: 'run-abc12345',
    orchestrationName: 'test-orch',
    status: 'Succeeded',
    startedAt: new Date('2025-01-01T10:00:00Z').toISOString(),
    durationSeconds: 12.4,
    triggeredBy: 'manual',
    origin: 'manual',
    isActive: false,
    ...overrides,
  };
}

describe('HistoryRow', () => {
  it('renders the orchestration name and status icon', () => {
    render(<HistoryRow exec={makeExec()} onSelect={vi.fn()} />);

    expect(screen.getByText('test-orch')).toBeInTheDocument();
    // Status class is on a sibling element; assert via aria-label which folds in status text.
    expect(screen.getByRole('listitem')).toHaveAttribute('aria-label', expect.stringContaining('Succeeded'));
  });

  it('renders the duration alongside the start time for completed runs', () => {
    render(<HistoryRow exec={makeExec({ durationSeconds: 75 })} onSelect={vi.fn()} />);

    // "1m 15s" formatted by formatDuration
    expect(screen.getByText('1m 15s')).toBeInTheDocument();
  });

  it('omits the duration when the run is still active', () => {
    render(<HistoryRow exec={makeExec({ isActive: true, durationSeconds: 5 })} onSelect={vi.fn()} />);

    expect(screen.queryByText(/1m|5s/)).not.toBeInTheDocument();
  });

  it('renders a retry badge that links to the source run', () => {
    const onViewSourceRun = vi.fn();
    render(
      <HistoryRow
        exec={makeExec({
          retriedFromRunId: 'source-run-abcd1234',
          retryMode: 'from-step:judge',
          origin: 'retry',
          triggeredBy: 'retry',
        })}
        onSelect={vi.fn()}
        onViewSourceRun={onViewSourceRun}
      />,
    );

    const badge = screen.getByLabelText(/Retry of run/);
    expect(badge).toBeInTheDocument();
    fireEvent.click(badge);
    expect(onViewSourceRun).toHaveBeenCalledWith('source-run-abcd1234');
  });

  it('does NOT render a retry badge when retriedFromRunId is missing', () => {
    render(<HistoryRow exec={makeExec()} onSelect={vi.fn()} />);

    expect(screen.queryByLabelText(/Retry of run/)).not.toBeInTheDocument();
  });

  it('renders a parent badge that links to the parent run when invoked by another orchestration', () => {
    const onViewParentRun = vi.fn();
    render(
      <HistoryRow
        exec={makeExec({
          parentExecutionId: 'parent-run-id',
          parentOrchestrationName: 'parent-orch',
          parentStepName: 'invoke-child',
          origin: 'orchestration',
          triggeredBy: 'orchestration:parent-orch:parent-run-id',
        })}
        onSelect={vi.fn()}
        onViewParentRun={onViewParentRun}
      />,
    );

    const badge = screen.getByLabelText(/Invoked by parent-orch/);
    expect(badge).toBeInTheDocument();
    expect(badge).toHaveAttribute('title', expect.stringContaining("step 'invoke-child'"));

    fireEvent.click(badge);
    expect(onViewParentRun).toHaveBeenCalledWith('parent-run-id');
  });

  it('falls back to the parent run id (truncated) when parentOrchestrationName is missing', () => {
    render(
      <HistoryRow
        exec={makeExec({
          parentExecutionId: 'orphan-parent-deadbeef',
          parentOrchestrationName: null,
          origin: 'orchestration',
        })}
        onSelect={vi.fn()}
      />,
    );

    // 8-char truncation of parentExecutionId
    expect(screen.getByText(/orphan-p/)).toBeInTheDocument();
  });

  it('clicking the row body invokes onSelect', () => {
    const onSelect = vi.fn();
    const exec = makeExec();
    render(<HistoryRow exec={exec} onSelect={onSelect} />);

    fireEvent.click(screen.getByRole('listitem'));
    expect(onSelect).toHaveBeenCalledWith(exec);
  });

  it('Enter and Space on the row body activate onSelect', () => {
    const onSelect = vi.fn();
    const exec = makeExec();
    render(<HistoryRow exec={exec} onSelect={onSelect} />);

    fireEvent.keyDown(screen.getByRole('listitem'), { key: 'Enter' });
    fireEvent.keyDown(screen.getByRole('listitem'), { key: ' ' });
    expect(onSelect).toHaveBeenCalledTimes(2);
  });

  it('clicking the retry/parent badge does not also fire onSelect (event stops propagation)', () => {
    const onSelect = vi.fn();
    const onViewSourceRun = vi.fn();
    render(
      <HistoryRow
        exec={makeExec({ retriedFromRunId: 'src-1', retryMode: 'failed' })}
        onSelect={onSelect}
        onViewSourceRun={onViewSourceRun}
      />,
    );

    fireEvent.click(screen.getByLabelText(/Retry of run/));
    expect(onViewSourceRun).toHaveBeenCalledTimes(1);
    expect(onSelect).not.toHaveBeenCalled();
  });

  it('renders a delete button only for completed runs', () => {
    const { rerender } = render(<HistoryRow exec={makeExec()} onSelect={vi.fn()} onDelete={vi.fn()} />);
    expect(screen.getByLabelText(/Delete test-orch/)).toBeInTheDocument();

    rerender(<HistoryRow exec={makeExec({ isActive: true })} onSelect={vi.fn()} onDelete={vi.fn()} />);
    expect(screen.queryByLabelText(/Delete test-orch/)).not.toBeInTheDocument();
  });

  it('clicking the delete button fires onDelete and stops propagation', () => {
    const onSelect = vi.fn();
    const onDelete = vi.fn();
    render(<HistoryRow exec={makeExec()} onSelect={onSelect} onDelete={onDelete} />);

    fireEvent.click(screen.getByLabelText(/Delete test-orch/));
    expect(onDelete).toHaveBeenCalledTimes(1);
  });

  it('renders the Running badge for active runs', () => {
    render(<HistoryRow exec={makeExec({ isActive: true, status: 'Running' })} onSelect={vi.fn()} />);

    expect(screen.getByText('Running')).toBeInTheDocument();
  });

  it('renders Cancelling state distinctly', () => {
    render(<HistoryRow exec={makeExec({ isActive: true, status: 'Cancelling' })} onSelect={vi.fn()} />);

    expect(screen.getByText('Cancelling')).toBeInTheDocument();
  });

  it('classifies origin from triggeredBy when origin is missing', () => {
    // Server-projected `origin` is omitted; HistoryRow must still pick the right icon class.
    render(<HistoryRow exec={makeExec({ origin: undefined, triggeredBy: 'scheduler' })} onSelect={vi.fn()} />);

    const iconSpan = screen.getByTitle(/Origin: scheduler/);
    expect(iconSpan.className).toContain('history-origin-scheduler');
  });

  it('classifies orchestration:* origin correctly when only triggeredBy is present', () => {
    render(<HistoryRow exec={makeExec({ origin: undefined, triggeredBy: 'orchestration:parent:abc' })} onSelect={vi.fn()} />);

    const iconSpan = screen.getByTitle('Origin: orchestration:parent:abc');
    expect(iconSpan.className).toContain('history-origin-orchestration');
  });
});
