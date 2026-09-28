import React, { useEffect, useState } from 'react';
import type { StepProcess } from '../types';
import CopyButton from './CopyButton';
import { formatMs } from './StepTimingBadge';

/** Step types that run an operating-system process the panel can describe. */
const PROCESS_STEP_TYPES: ReadonlySet<string> = new Set(['script', 'command']);

export interface StepActivityPanelProps {
  stepName: string;
  /** Portal step status token ('running', 'completed', 'failed', ...). */
  status: string | null | undefined;
  /** Step type from the orchestration definition (Script, Command, Prompt, ...). */
  stepType?: string | null;
  /** Process reported by the engine for this step, when it spawned one. */
  process?: StepProcess | null;
  /** Server-stamped start time of the step (ISO), used for the elapsed counter. */
  startedAt?: string | null;
  /** Configured step timeout; null/undefined means the step may run indefinitely. */
  timeoutSeconds?: number | null;
  /** Whether any stdout has been streamed for this step so far. */
  hasOutput: boolean;
}

function parseIso(iso?: string | null): number | null {
  if (!iso) return null;
  const t = Date.parse(iso);
  return Number.isNaN(t) ? null : t;
}

/**
 * Explains what a Script/Command step is doing while it runs: the process it spawned (pid,
 * command line, inline script file, working directory), how long it has been running, whether
 * a timeout will ever stop it, and whether it has produced any output yet. Before this panel
 * a stuck step showed nothing but "step-started" for its entire duration.
 */
export default function StepActivityPanel({
  stepName,
  status,
  stepType,
  process,
  startedAt,
  timeoutSeconds,
  hasOutput,
}: StepActivityPanelProps): React.JSX.Element | null {
  const isProcessStep = !!stepType && PROCESS_STEP_TYPES.has(stepType.toLowerCase());
  const isRunning = status === 'running';
  const [now, setNow] = useState(() => Date.now());

  useEffect(() => {
    if (!isRunning) return undefined;
    const id = setInterval(() => setNow(Date.now()), 1000);
    return () => clearInterval(id);
  }, [isRunning]);

  if (!process && !(isProcessStep && isRunning)) return null;

  const startMs = parseIso(process?.startedAt) ?? parseIso(startedAt);
  const elapsedMs = startMs === null ? null : Math.max(0, now - startMs);
  const commandLine = process ? `${process.fileName} ${process.arguments}`.trim() : null;

  let timeoutText: string;
  let timeoutWarning = false;
  if (timeoutSeconds && timeoutSeconds > 0) {
    const limitMs = timeoutSeconds * 1000;
    if (isRunning && elapsedMs !== null) {
      const remaining = limitMs - elapsedMs;
      timeoutText = remaining > 0
        ? `${formatMs(limitMs)} (fires in ${formatMs(remaining)})`
        : `${formatMs(limitMs)} (reached - the engine is stopping the process)`;
    } else {
      timeoutText = formatMs(limitMs);
    }
  } else {
    timeoutText = 'none configured - this step can run indefinitely';
    timeoutWarning = isRunning;
  }

  return (
    <div className="step-activity" data-testid="step-activity" style={{ marginBottom: '8px' }}>
      <div className="step-activity-title">
        Activity
        {isRunning && elapsedMs !== null && (
          <span className="step-activity-elapsed" data-testid="step-activity-elapsed">
            running for {formatMs(elapsedMs)}
          </span>
        )}
      </div>

      <div className="step-activity-grid">
        {process ? (
          <>
            <div className="step-activity-label">Process</div>
            <div className="step-activity-value">
              <span className="step-activity-mono">{process.fileName}</span>
              <span className="step-activity-dim"> pid {process.processId}</span>
              {!isRunning && <span className="step-activity-dim"> (exited)</span>}
            </div>

            <div className="step-activity-label">Command</div>
            <div className="step-activity-value step-activity-value-copyable">
              <span className="step-activity-mono step-activity-wrap" title={commandLine ?? undefined}>
                {commandLine}
              </span>
              {commandLine && <CopyButton value={commandLine} label="command line" />}
            </div>

            {process.scriptPath && (
              <>
                <div className="step-activity-label">Script</div>
                <div className="step-activity-value step-activity-value-copyable">
                  <span className="step-activity-mono step-activity-wrap" title={process.scriptPath}>
                    {process.scriptPath}
                  </span>
                  <CopyButton value={process.scriptPath} label="script path" />
                </div>
              </>
            )}

            {process.workingDirectory && (
              <>
                <div className="step-activity-label">Directory</div>
                <div className="step-activity-value step-activity-value-copyable">
                  <span className="step-activity-mono step-activity-wrap" title={process.workingDirectory}>
                    {process.workingDirectory}
                  </span>
                  <CopyButton value={process.workingDirectory} label="working directory" />
                </div>
              </>
            )}
          </>
        ) : (
          <>
            <div className="step-activity-label">Process</div>
            <div className="step-activity-value step-activity-dim">
              starting {stepType?.toLowerCase() === 'command' ? 'command' : 'script'} process...
            </div>
          </>
        )}

        <div className="step-activity-label">Timeout</div>
        <div
          className={`step-activity-value${timeoutWarning ? ' step-activity-warning' : ''}`}
          data-testid="step-activity-timeout"
        >
          {timeoutText}
        </div>

        {isRunning && (
          <>
            <div className="step-activity-label">Output</div>
            <div className="step-activity-value step-activity-dim" data-testid="step-activity-output">
              {hasOutput
                ? 'streaming below'
                : 'nothing written to stdout yet - if this persists, inspect the process above'}
            </div>
          </>
        )}
      </div>
      <span className="visually-hidden">{`Activity for step ${stepName}`}</span>
    </div>
  );
}
