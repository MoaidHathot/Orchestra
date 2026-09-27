import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, fireEvent, waitFor, within } from '@testing-library/react';
import '@testing-library/jest-dom';
import HostModal, { levelClass } from './HostModal';
import type { HealthReport, LogTail, PruneResult } from '../../types';

vi.mock('../../hostApi', () => ({
  hostApi: {
    getHealth: vi.fn(),
    getLogTail: vi.fn(),
    getLogSince: vi.fn(),
    pruneRuns: vi.fn(),
  },
  formatUptime: (s: string) => `up(${s})`,
}));

import { hostApi } from '../../hostApi';

const mocked = hostApi as unknown as {
  getHealth: ReturnType<typeof vi.fn>;
  getLogTail: ReturnType<typeof vi.fn>;
  getLogSince: ReturnType<typeof vi.fn>;
  pruneRuns: ReturnType<typeof vi.fn>;
};

const healthy: HealthReport = {
  status: 'healthy',
  version: '0.9.1',
  timestamp: '2026-09-27T10:00:00Z',
  uptime: '00:10:00',
  activeRuns: 2,
  checks: [
    { name: 'dataPath', status: 'healthy', detail: '/data' },
    { name: 'agentProviders', status: 'healthy', detail: 'copilot (default: copilot)' },
  ],
};

const tail: LogTail = {
  directory: '/data/logs',
  file: 'orchestra-2026-09-27.log',
  path: '/data/logs/orchestra-2026-09-27.log',
  sizeBytes: 100,
  offset: 100,
  truncated: false,
  enabled: true,
  lines: [
    '2026-09-27 10:00:00 [Information] Orchestra.Host: started',
    '2026-09-27 10:00:01 [Warning] Orchestra.Host: careful',
  ],
};

describe('HostModal', () => {
  beforeEach(() => {
    Element.prototype.scrollIntoView = vi.fn();
    mocked.getHealth.mockReset();
    mocked.getLogTail.mockReset();
    mocked.getLogSince.mockReset();
    mocked.pruneRuns.mockReset();
    mocked.getHealth.mockResolvedValue(healthy);
    mocked.getLogTail.mockResolvedValue(tail);
    mocked.getLogSince.mockResolvedValue({ ...tail, lines: [] });
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it('renders health checks with the overall status', async () => {
    render(<HostModal open onClose={() => {}} />);

    await waitFor(() => expect(screen.getByTestId('health-summary')).toBeInTheDocument());
    expect(screen.getByTestId('health-summary')).toHaveTextContent('healthy');
    expect(screen.getByTestId('health-summary')).toHaveTextContent('2 active runs');
    const rows = within(screen.getByTestId('health-checks')).getAllByRole('row');
    expect(rows).toHaveLength(2);
    expect(rows[0]).toHaveTextContent('dataPath');
    expect(rows[1]).toHaveTextContent('copilot (default: copilot)');
  });

  it('shows a degraded/unhealthy report as such (503 bodies are reports, not errors)', async () => {
    mocked.getHealth.mockResolvedValue({
      ...healthy,
      status: 'unhealthy',
      checks: [{ name: 'dataPath', status: 'unhealthy', detail: '/data: access denied' }],
    });
    render(<HostModal open onClose={() => {}} />);

    await waitFor(() => expect(screen.getByTestId('health-summary')).toHaveTextContent('unhealthy'));
    expect(screen.getByTestId('health-summary')).toHaveClass('unhealthy');
    expect(screen.getByText('/data: access denied')).toBeInTheDocument();
  });

  it('surfaces a health fetch failure without crashing', async () => {
    mocked.getHealth.mockRejectedValue(new Error('network down'));
    render(<HostModal open onClose={() => {}} />);

    await waitFor(() => expect(screen.getByRole('alert')).toHaveTextContent('network down'));
  });

  it('logs tab shows the tail, colours levels and filters lines', async () => {
    render(<HostModal open onClose={() => {}} initialTab="logs" />);

    await waitFor(() => expect(screen.getByTestId('log-lines')).toBeInTheDocument());
    expect(mocked.getLogTail).toHaveBeenCalledWith(300);
    expect(screen.getByTestId('log-lines').querySelectorAll('.host-log-line')).toHaveLength(2);
    expect(screen.getByText(/careful/)).toHaveClass('level-warning');
    expect(screen.getByText(tail.file)).toBeInTheDocument();

    fireEvent.change(screen.getByLabelText('Filter log lines'), { target: { value: 'careful' } });
    expect(screen.queryByText(/started/)).not.toBeInTheDocument();
    expect(screen.getByText(/careful/)).toBeInTheDocument();
  });

  it('logs tab follows by appending what the host wrote since the last offset', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    mocked.getLogSince.mockResolvedValue({ ...tail, offset: 160, lines: ['2026-09-27 10:00:02 [Error] Orchestra.Host: boom'] });
    render(<HostModal open onClose={() => {}} initialTab="logs" />);
    await waitFor(() => expect(screen.getByTestId('log-lines')).toBeInTheDocument());

    await vi.advanceTimersByTimeAsync(3_100);

    await waitFor(() => expect(screen.getByText(/boom/)).toBeInTheDocument());
    expect(mocked.getLogSince).toHaveBeenCalledWith(100, tail.file);
    expect(screen.getByText(/boom/)).toHaveClass('level-error');
  });

  it('logs tab announces a day roll', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    mocked.getLogSince.mockResolvedValue({ ...tail, file: 'orchestra-2026-09-28.log', offset: 10, lines: ['2026-09-28 00:00:01 [Information] Orchestra.Host: new day'] });
    render(<HostModal open onClose={() => {}} initialTab="logs" />);
    await waitFor(() => expect(screen.getByTestId('log-lines')).toBeInTheDocument());

    await vi.advanceTimersByTimeAsync(3_100);

    await waitFor(() => expect(screen.getByText(/log rolled to orchestra-2026-09-28.log/)).toBeInTheDocument());
  });

  it('logs tab: unchecking Follow stops polling', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    render(<HostModal open onClose={() => {}} initialTab="logs" />);
    await waitFor(() => expect(screen.getByTestId('log-lines')).toBeInTheDocument());

    fireEvent.click(screen.getByLabelText(/Follow/));
    await vi.advanceTimersByTimeAsync(7_000);

    expect(mocked.getLogSince).not.toHaveBeenCalled();
  });

  it('maintenance: preview is mandatory, then delete applies and reports', async () => {
    const preview: PruneResult = {
      dryRun: true,
      policy: { maxRunsPerOrchestration: 50, maxRunAgeDays: null, source: 'orchestra.json' },
      wouldDelete: 2,
      runs: [
        { runId: 'r1', orchestrationName: 'nightly', status: 'Succeeded', startedAt: '2026-09-01T00:00:00Z', folderPath: 'p1' },
        { runId: 'r2', orchestrationName: 'nightly', status: 'Failed', startedAt: '2026-09-02T00:00:00Z', folderPath: 'p2' },
      ],
    };
    const applied: PruneResult = { ...preview, dryRun: false, wouldDelete: undefined, deleted: 2, failed: 0 };
    mocked.pruneRuns.mockResolvedValueOnce(preview).mockResolvedValueOnce(applied);
    const onRunsPruned = vi.fn();
    render(<HostModal open onClose={() => {}} initialTab="maintenance" onRunsPruned={onRunsPruned} />);

    const deleteButton = screen.getByRole('button', { name: /Delete 0 runs/ });
    expect(deleteButton).toBeDisabled();

    fireEvent.click(screen.getByRole('button', { name: /Preview/ }));
    await waitFor(() => expect(screen.getByTestId('prune-preview')).toBeInTheDocument());
    expect(mocked.pruneRuns).toHaveBeenLastCalledWith({ dryRun: true, maxRuns: null, maxAgeDays: null });
    expect(screen.getByTestId('prune-policy')).toHaveTextContent('orchestra.json');
    expect(screen.getByTestId('prune-preview')).toHaveTextContent('2 runs would be deleted');

    const confirm = screen.getByRole('button', { name: /Delete 2 runs/ });
    expect(confirm).toBeEnabled();
    fireEvent.click(confirm);

    await waitFor(() => expect(screen.getByTestId('prune-result')).toBeInTheDocument());
    expect(mocked.pruneRuns).toHaveBeenLastCalledWith({ dryRun: false, maxRuns: null, maxAgeDays: null });
    expect(screen.getByTestId('prune-result')).toHaveTextContent('Deleted 2 runs');
    expect(onRunsPruned).toHaveBeenCalledWith(applied);
  });

  it('maintenance: overrides are sent when enabled and validated', async () => {
    mocked.pruneRuns.mockResolvedValue({ dryRun: true, policy: { maxRunsPerOrchestration: 5, source: 'request' }, wouldDelete: 0, runs: [] });
    render(<HostModal open onClose={() => {}} initialTab="maintenance" />);

    fireEvent.click(screen.getByLabelText(/Override the configured policy/));
    expect(screen.getByRole('button', { name: /Preview/ })).toBeDisabled();

    fireEvent.change(screen.getByLabelText('Max runs per orchestration'), { target: { value: '5' } });
    expect(screen.getByRole('button', { name: /Preview/ })).toBeEnabled();
    fireEvent.click(screen.getByRole('button', { name: /Preview/ }));

    await waitFor(() => expect(screen.getByTestId('prune-preview')).toBeInTheDocument());
    expect(mocked.pruneRuns).toHaveBeenCalledWith({ dryRun: true, maxRuns: 5, maxAgeDays: null });
    expect(screen.getByTestId('prune-preview')).toHaveTextContent('Nothing to delete');
    expect(screen.getByRole('button', { name: /Delete 0 runs/ })).toBeDisabled();
  });

  it('maintenance: a 400 from the host (no policy) is shown inline', async () => {
    mocked.pruneRuns.mockRejectedValue(new Error('No retention policy is configured.'));
    render(<HostModal open onClose={() => {}} initialTab="maintenance" />);

    fireEvent.click(screen.getByRole('button', { name: /Preview/ }));

    await waitFor(() => expect(screen.getByRole('alert')).toHaveTextContent('No retention policy is configured.'));
  });
});

describe('levelClass', () => {
  it.each([
    ['2026-09-27 10:00:00 [Information] X: y', 'level-information'],
    ['2026-09-27 10:00:00 [Warning] X: y', 'level-warning'],
    ['2026-09-27 10:00:00 [Error] X: y', 'level-error'],
    ['   at Something.Do()', 'continuation'],
  ])('%s -> %s', (line, expected) => {
    expect(levelClass(line)).toBe(expected);
  });
});
