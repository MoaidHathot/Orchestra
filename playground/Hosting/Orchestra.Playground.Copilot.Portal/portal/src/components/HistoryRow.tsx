import React from 'react';
import { Icons, getOriginIcon } from '../icons';
import { formatTime, formatDuration } from '../utils';
import { classifyRunOrigin, type RunOrigin } from '../runFilters';

/**
 * Shape required to render a single history row. Matches a subset of
 * HistoryListEntry from App.tsx so the component remains decoupled from
 * the parent's full state shape.
 */
export interface HistoryRowEntry {
  runId: string;
  executionId?: string;
  orchestrationId?: string;
  orchestrationName: string;
  status?: string;
  completionReason?: string;
  isActive?: boolean;
  isIncomplete?: boolean;
  startedAt?: string;
  durationSeconds?: number;
  origin?: RunOrigin;
  triggeredBy?: string;
  retriedFromRunId?: string | null;
  retryMode?: string | null;
  parentExecutionId?: string | null;
  parentStepName?: string | null;
  parentOrchestrationName?: string | null;
}

export interface HistoryRowProps {
  exec: HistoryRowEntry;
  /** Click on the row body. */
  onSelect: (exec: HistoryRowEntry) => void;
  /** Click on the trash button (only shown for completed rows). */
  onDelete?: (exec: HistoryRowEntry, e: React.MouseEvent) => void;
  /**
   * Click on the retry badge. Receives the source run id. When omitted,
   * the badge is rendered without a link affordance.
   */
  onViewSourceRun?: (sourceRunId: string) => void;
  /**
   * Click on the parent badge. Receives the parent run id. When omitted,
   * the badge is rendered as a non-interactive label.
   */
  onViewParentRun?: (parentRunId: string) => void;
}

/**
 * Single row in the sidebar's "Recent Executions" list.
 *
 * Layout (left-to-right):
 *   [status icon] [origin icon] orchestration-name [retry badge] [parent badge]
 *                               started-at · duration                  [delete]
 *
 * The component is purely presentational. It does not poll, fetch, or filter —
 * the parent passes pre-filtered data and click handlers.
 */
export default function HistoryRow({
  exec,
  onSelect,
  onDelete,
  onViewSourceRun,
  onViewParentRun,
}: HistoryRowProps): React.JSX.Element {
  const origin = exec.origin ?? classifyRunOrigin(exec.triggeredBy);
  const statusClass = (exec.isIncomplete || exec.completionReason) && exec.status === 'Succeeded'
    ? 'completed-early'
    : (exec.status?.toLowerCase() ?? 'running');

  const handleClick = () => onSelect(exec);
  const handleKeyDown = (e: React.KeyboardEvent) => {
    if (e.key === 'Enter' || e.key === ' ') {
      e.preventDefault();
      onSelect(exec);
    }
  };

  const handleRetryBadgeClick = (e: React.MouseEvent) => {
    e.stopPropagation();
    if (exec.retriedFromRunId && onViewSourceRun) {
      onViewSourceRun(exec.retriedFromRunId);
    }
  };

  const handleParentBadgeClick = (e: React.MouseEvent) => {
    e.stopPropagation();
    if (exec.parentExecutionId && onViewParentRun) {
      onViewParentRun(exec.parentExecutionId);
    }
  };

  const ariaLabel = `${exec.orchestrationName} - ${exec.status || 'Running'} - ${formatTime(exec.startedAt)}`;
  const durationText = !exec.isActive ? formatDuration(exec.durationSeconds) : '';

  return (
    <div
      className="history-item"
      role="listitem"
      tabIndex={0}
      onClick={handleClick}
      onKeyDown={handleKeyDown}
      aria-label={ariaLabel}
    >
      <div className={`history-status-icon ${statusClass}`} aria-hidden="true">
        {exec.isActive ? (
          <span className="spinner" style={{ width: '12px', height: '12px' }} />
        ) : exec.status === 'Succeeded' && (exec.completionReason || exec.isIncomplete) ? (
          <Icons.SkipForward />
        ) : exec.status === 'Succeeded' ? (
          <Icons.Check />
        ) : exec.status === 'Failed' ? (
          <Icons.X />
        ) : exec.status === 'Cancelled' ? (
          <Icons.Ban />
        ) : (
          '...'
        )}
      </div>

      <div className="history-info">
        <div className="history-name">
          <span
            className={`history-origin-icon history-origin-${origin}`}
            aria-hidden="true"
            title={`Origin: ${exec.triggeredBy ?? origin}`}
          >
            {getOriginIcon(origin)}
          </span>
          <span className="history-name-text">{exec.orchestrationName}</span>
          {exec.isActive && (
            <span
              className="step-status-badge running"
              style={{ marginLeft: '6px', fontSize: '10px', padding: '2px 6px' }}
            >
              {exec.status === 'Cancelling' ? 'Cancelling' : 'Running'}
            </span>
          )}
          {exec.retriedFromRunId && (
            <button
              type="button"
              className="history-lineage-badge history-lineage-retry"
              onClick={handleRetryBadgeClick}
              title={`Retry${exec.retryMode ? ` (${exec.retryMode})` : ''} of run ${exec.retriedFromRunId}`}
              aria-label={`Retry of run ${exec.retriedFromRunId}`}
            >
              {'\u21A9'} <code>{exec.retriedFromRunId.slice(0, 8)}</code>
            </button>
          )}
          {exec.parentExecutionId && (
            <button
              type="button"
              className="history-lineage-badge history-lineage-parent"
              onClick={handleParentBadgeClick}
              title={`Invoked by ${exec.parentOrchestrationName ?? 'parent run'}${exec.parentStepName ? ` step '${exec.parentStepName}'` : ''}`}
              aria-label={`Invoked by ${exec.parentOrchestrationName ?? exec.parentExecutionId}`}
            >
              {'\u21B3'} {exec.parentOrchestrationName ?? exec.parentExecutionId.slice(0, 8)}
            </button>
          )}
        </div>
        <div className="history-time">
          <span>{formatTime(exec.startedAt)}</span>
          {durationText && (
            <>
              <span className="history-time-separator" aria-hidden="true"> · </span>
              <span className="history-duration">{durationText}</span>
            </>
          )}
        </div>
      </div>

      {!exec.isActive && onDelete && (
        <button
          className="history-delete-btn"
          onClick={(e: React.MouseEvent) => onDelete(exec, e)}
          title="Delete execution"
          aria-label={`Delete ${exec.orchestrationName} execution`}
        >
          <Icons.X />
        </button>
      )}
    </div>
  );
}
