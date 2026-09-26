import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen, act, cleanup } from '@testing-library/react';
import '@testing-library/jest-dom';
import StepTimingBadge from './StepTimingBadge';

afterEach(() => {
  cleanup();
  vi.useRealTimers();
});

describe('StepTimingBadge', () => {
  it('shows a live "running" counter that advances each second', () => {
    vi.useFakeTimers();
    // Clock is frozen at "now"; the step started 3s ago.
    const startedAt = new Date(Date.now() - 3000).toISOString();
    render(<StepTimingBadge status="running" timing={{ startedAt }} />);

    expect(screen.getByText('running 3.0s')).toBeInTheDocument();

    // The internal 1s interval re-renders; the counter must climb with wall-clock time.
    act(() => {
      vi.advanceTimersByTime(2000);
    });
    expect(screen.getByText('running 5.0s')).toBeInTheDocument();
  });

  it('shows the final duration from the server durationSeconds when completed', () => {
    render(
      <StepTimingBadge
        status="completed"
        timing={{ startedAt: '2026-07-20T10:00:00.000Z', completedAt: '2026-07-20T10:00:12.400Z', durationSeconds: 12.4 }}
      />,
    );
    expect(screen.getByText('12.4s')).toBeInTheDocument();
  });

  it('derives the duration from timestamps when durationSeconds is absent', () => {
    render(
      <StepTimingBadge
        status="failed"
        timing={{ startedAt: '2026-07-20T10:00:00.000Z', completedAt: '2026-07-20T10:02:05.000Z' }}
      />,
    );
    // 125s → "2m 5s"
    expect(screen.getByText('2m 5s')).toBeInTheDocument();
  });

  it('renders nothing for a pending step', () => {
    const { container } = render(<StepTimingBadge status="pending" timing={{}} />);
    expect(container).toBeEmptyDOMElement();
  });

  it('renders nothing for a skipped step (no meaningful duration)', () => {
    const { container } = render(<StepTimingBadge status="skipped" timing={{ durationSeconds: 0 }} />);
    expect(container).toBeEmptyDOMElement();
  });

  it('renders nothing while running when the start time is unknown', () => {
    const { container } = render(<StepTimingBadge status="running" timing={{}} />);
    expect(container).toBeEmptyDOMElement();
  });
});
