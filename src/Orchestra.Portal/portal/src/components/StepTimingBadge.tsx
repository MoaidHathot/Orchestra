import React, { useEffect, useState } from 'react';
import type { StepTiming } from '../types';

/** Format a millisecond span as a compact human string: 850ms, 12.3s, 2m 5s, 1h 3m. */
function formatMs(ms: number): string {
  const clamped = Math.max(0, ms);
  if (clamped < 1000) return `${Math.round(clamped)}ms`;
  if (clamped < 60_000) return `${(clamped / 1000).toFixed(1)}s`;
  if (clamped < 3_600_000) {
    const m = Math.floor(clamped / 60_000);
    const s = Math.floor((clamped % 60_000) / 1000);
    return `${m}m ${s}s`;
  }
  const h = Math.floor(clamped / 3_600_000);
  const m = Math.floor((clamped % 3_600_000) / 60_000);
  return `${h}h ${m}m`;
}

/** Statuses that represent a finished step for which a final duration is meaningful. */
const DURATION_STATUSES: ReadonlySet<string> = new Set([
  'completed',
  'completed_restored',
  'completed_early',
  'failed',
  'cancelled',
]);

function toMsSinceEpoch(iso?: string | null): number | null {
  if (!iso) return null;
  const t = Date.parse(iso);
  return Number.isNaN(t) ? null : t;
}

interface Props {
  /** Portal step status token (e.g. 'running', 'completed', 'failed'). */
  status: string | null | undefined;
  /** Timing for this step, if known. */
  timing?: StepTiming | null;
}

/**
 * Shows how long a step has taken:
 *  - while RUNNING, a live counter that ticks every second ("running 12.0s");
 *  - when finished, the final duration ("2m 5s").
 *
 * Renders nothing when there is no usable timing data (e.g. a pending or skipped
 * step, or a step for which no timestamps are available).
 */
export default function StepTimingBadge({ status, timing }: Props): React.JSX.Element | null {
  const startedAt = timing?.startedAt ?? null;
  const completedAt = timing?.completedAt ?? null;
  const durationSeconds = timing?.durationSeconds ?? null;
  const isRunning = status === 'running' && !completedAt && !!startedAt;

  // Re-render once per second while the step is running so the live counter advances.
  const [, force] = useState(0);
  useEffect(() => {
    if (!isRunning) return;
    const id = window.setInterval(() => force(n => n + 1), 1000);
    return () => window.clearInterval(id);
  }, [isRunning]);

  // Finished: show the final duration. Prefer the server-provided seconds; otherwise
  // derive it from the timestamps.
  if (status && DURATION_STATUSES.has(status)) {
    let ms: number | null = null;
    if (typeof durationSeconds === 'number' && durationSeconds >= 0) {
      ms = durationSeconds * 1000;
    } else {
      const start = toMsSinceEpoch(startedAt);
      const end = toMsSinceEpoch(completedAt);
      if (start !== null && end !== null) ms = end - start;
    }
    if (ms === null) return null;
    return (
      <span className="step-timing" title="Step duration">
        {formatMs(ms)}
      </span>
    );
  }

  // Running: live elapsed since startedAt.
  if (isRunning && startedAt) {
    const start = toMsSinceEpoch(startedAt);
    if (start === null) return null;
    return (
      <span className="step-timing running" title="Running for" aria-live="polite">
        running {formatMs(Date.now() - start)}
      </span>
    );
  }

  // Pending / skipped / no timing → nothing to show.
  return null;
}
