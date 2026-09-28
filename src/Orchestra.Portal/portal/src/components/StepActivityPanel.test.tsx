import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, fireEvent, act } from '@testing-library/react';
import '@testing-library/jest-dom';
import StepActivityPanel from './StepActivityPanel';
import type { StepProcess } from '../types';

vi.mock('../icons', () => ({
  Icons: {
    Copy: () => <span data-testid="icon-copy" />,
    Check: () => <span data-testid="icon-check" />,
  },
}));

const process: StepProcess = {
  stepName: 'refresh-active-run-statuses',
  processId: 31108,
  fileName: 'pwsh',
  arguments: '-NoProfile -File C:\\Temp\\orchestra-de4996bb.ps1',
  scriptPath: 'C:\\Temp\\orchestra-de4996bb.ps1',
  workingDirectory: 'W:\\work',
  startedAt: new Date(Date.now() - 34 * 60_000).toISOString(),
};

describe('StepActivityPanel', () => {
  beforeEach(() => {
    vi.useFakeTimers({ shouldAdvanceTime: false });
  });
  afterEach(() => {
    vi.useRealTimers();
  });

  it('renders nothing for steps that do not run a process and have no process report', () => {
    const { container } = render(
      <StepActivityPanel stepName="think" status="running" stepType="Prompt" hasOutput={false} />,
    );
    expect(container).toBeEmptyDOMElement();
  });

  it('describes a running script process: pid, command line, script path, cwd, elapsed', () => {
    render(
      <StepActivityPanel
        stepName="refresh-active-run-statuses"
        status="running"
        stepType="Script"
        process={process}
        timeoutSeconds={3600}
        hasOutput={false}
      />,
    );

    expect(screen.getByText('pwsh')).toBeInTheDocument();
    expect(screen.getByText('pid 31108')).toBeInTheDocument();
    expect(screen.getByText('pwsh -NoProfile -File C:\\Temp\\orchestra-de4996bb.ps1')).toBeInTheDocument();
    expect(screen.getByText('C:\\Temp\\orchestra-de4996bb.ps1')).toBeInTheDocument();
    expect(screen.getByText('W:\\work')).toBeInTheDocument();
    expect(screen.getByTestId('step-activity-elapsed').textContent).toMatch(/running for 34m/);
    expect(screen.getByTestId('step-activity-timeout').textContent).toMatch(/1h 0m \(fires in 2[56]m/);
    expect(screen.getByTestId('step-activity-output').textContent).toMatch(/nothing written to stdout yet/);
  });

  it('warns loudly when a running step has no timeout at all', () => {
    render(
      <StepActivityPanel stepName="s" status="running" stepType="Script" process={process} hasOutput={true} />,
    );

    const timeout = screen.getByTestId('step-activity-timeout');
    expect(timeout.textContent).toMatch(/none configured/);
    expect(timeout).toHaveClass('step-activity-warning');
    expect(screen.getByTestId('step-activity-output').textContent).toMatch(/streaming below/);
  });

  it('treats an explicit 0 timeout as none', () => {
    render(
      <StepActivityPanel stepName="s" status="running" stepType="Command" process={process} timeoutSeconds={0} hasOutput={false} />,
    );
    expect(screen.getByTestId('step-activity-timeout').textContent).toMatch(/none configured/);
  });

  it('shows a placeholder while the process has not been reported yet', () => {
    render(<StepActivityPanel stepName="s" status="running" stepType="Command" hasOutput={false} />);
    expect(screen.getByText(/starting command process/)).toBeInTheDocument();
  });

  it('keeps the process facts for a finished step but drops the live counters', () => {
    render(
      <StepActivityPanel stepName="s" status="completed" stepType="Script" process={process} timeoutSeconds={60} hasOutput={true} />,
    );

    expect(screen.getByText('(exited)')).toBeInTheDocument();
    expect(screen.queryByTestId('step-activity-elapsed')).not.toBeInTheDocument();
    expect(screen.queryByTestId('step-activity-output')).not.toBeInTheDocument();
    expect(screen.getByTestId('step-activity-timeout').textContent).toBe('1m 0s');
  });

  it('copies the script path', async () => {
    const writeText = vi.fn().mockResolvedValue(undefined);
    Object.assign(navigator, { clipboard: { writeText } });
    render(<StepActivityPanel stepName="s" status="running" stepType="Script" process={process} hasOutput={false} />);

    await act(async () => {
      fireEvent.click(screen.getByRole('button', { name: 'Copy script path' }));
      await Promise.resolve();
    });

    expect(writeText).toHaveBeenCalledWith('C:\\Temp\\orchestra-de4996bb.ps1');
  });
});
describe('StepActivityPanel - timeout already reached', () => {
  it('says so when the elapsed time exceeds the configured timeout', () => {
    render(
      <StepActivityPanel stepName="s" status="running" stepType="Script" process={process} timeoutSeconds={900} hasOutput={false} />,
    );
    expect(screen.getByTestId('step-activity-timeout').textContent).toMatch(/15m 0s \(reached/);
  });
});