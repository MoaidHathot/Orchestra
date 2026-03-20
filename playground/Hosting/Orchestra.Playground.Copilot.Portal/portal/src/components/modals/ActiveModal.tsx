import React from 'react';
import type { Orchestration, ActiveData } from '../../types';
import { Icons, getTriggerIcon } from '../../icons';
import { formatTimeAgo } from '../../utils';
import { useFocusTrap } from '../../hooks/useFocusTrap';

/** Runtime execution shape for the running list. */
interface RunningExecution {
  executionId: string;
  orchestrationId: string;
  orchestrationName: string;
  triggeredBy?: string;
  startedAt?: string;
}

/** Runtime shape for the pending trigger list. */
interface PendingTrigger {
  orchestrationId: string;
  orchestrationName: string;
  triggeredBy?: string;
  nextFireTime?: string;
}

interface Props {
  open: boolean;
  data: ActiveData | null;
  loading: boolean;
  onClose: () => void;
  onRefresh: () => void;
  orchestrations?: Orchestration[];
  onViewOrchestration?: (orchestration: Orchestration) => void;
  onViewRunningExecution?: (exec: RunningExecution, orchestration: Orchestration | undefined) => void;
  onCancelExecution?: (executionId: string) => void;
}

function ActiveModal({
  open,
  data,
  loading,
  onClose,
  onRefresh,
  orchestrations,
  onViewOrchestration,
  onViewRunningExecution,
  onCancelExecution,
}: Props): React.JSX.Element {
  const trapRef = useFocusTrap<HTMLDivElement>(open, onClose);
  const handleViewOrchestration = (exec: RunningExecution | PendingTrigger, isRunning = false) => {
    const orch = orchestrations?.find(o => o.id === exec.orchestrationId);
    if (isRunning && onViewRunningExecution && 'executionId' in exec) {
      // For running executions, open the running execution modal
      onViewRunningExecution(exec as RunningExecution, orch);
    } else if (orch && onViewOrchestration) {
      // For pending, open the viewer modal
      onViewOrchestration(orch);
    }
  };

  const running = (data?.running ?? []) as unknown as RunningExecution[];
  const pending = (data?.pending ?? []) as unknown as PendingTrigger[];

  return (
    <div className={`modal-overlay ${open ? 'visible' : ''}`} ref={trapRef} onClick={(e) => e.target === e.currentTarget && onClose()}>
      <div className="modal modal-lg" role="dialog" aria-modal="true" aria-label="Active orchestrations">
        <div className="modal-header">
          <div className="modal-title">
            <Icons.Activity /> Active Orchestrations
          </div>
          <button className="modal-close" aria-label="Close" onClick={onClose}><Icons.X /></button>
        </div>
        <div className="modal-body" style={{ minHeight: '300px' }}>
          {loading ? (
            <div className="empty-state"><div className="spinner"></div></div>
          ) : (
            <>
              {/* Running Section */}
              <div style={{ marginBottom: '20px' }}>
                <div style={{ fontSize: '14px', fontWeight: '600', marginBottom: '10px', display: 'flex', alignItems: 'center', gap: '8px' }}>
                  <span className="step-status-badge running" style={{ width: '8px', height: '8px', borderRadius: '50%' }}></span>
                  Running ({running.length})
                </div>
                {running.length === 0 ? (
                  <div style={{ color: 'var(--text-muted)', fontSize: '13px', padding: '10px', background: 'var(--surface)', borderRadius: '6px' }}>
                    No orchestrations currently running.
                  </div>
                ) : (
                  <div style={{ display: 'flex', flexDirection: 'column', gap: '8px' }}>
                    {running.map(exec => (
                      <div
                        key={exec.executionId}
                        className="history-item"
                        style={{
                          background: 'var(--surface)',
                          borderRadius: '6px',
                          borderLeft: '3px solid var(--primary)',
                          cursor: 'pointer'
                        }}
                        onClick={() => handleViewOrchestration(exec, true)}
                      >
                        <div className="history-status-icon running" style={{ background: 'var(--primary)', color: 'white' }}>
                          <Icons.Spinner />
                        </div>
                        <div className="history-info" style={{ flex: 1 }}>
                          <div className="history-name">{exec.orchestrationName}</div>
                          <div className="history-time" style={{ display: 'flex', alignItems: 'center', gap: '6px' }}>
                            {getTriggerIcon(exec.triggeredBy)}
                            <span>{exec.triggeredBy}</span>
                            <span style={{ margin: '0 4px' }}>&bull;</span>
                            <span>{formatTimeAgo(exec.startedAt)}</span>
                          </div>
                        </div>
                        <div style={{ display: 'flex', alignItems: 'center', gap: '8px' }}>
                          <div style={{ fontSize: '11px', color: 'var(--text-muted)', fontFamily: 'monospace' }}>
                            {exec.executionId?.slice(0, 8)}...
                          </div>
                          {onCancelExecution && (
                            <button
                              className="btn btn-danger btn-sm"
                              onClick={(e: React.MouseEvent) => { e.stopPropagation(); onCancelExecution(exec.executionId); }}
                              style={{ padding: '4px 8px', fontSize: '11px' }}
                            >
                              <Icons.X /> Cancel
                            </button>
                          )}
                          <button
                            className="btn btn-sm"
                            onClick={(e: React.MouseEvent) => { e.stopPropagation(); handleViewOrchestration(exec, true); }}
                            style={{ padding: '4px 8px', fontSize: '11px' }}
                          >
                            <Icons.Eye /> View
                          </button>
                        </div>
                      </div>
                    ))}
                  </div>
                )}
              </div>

              {/* Pending Section */}
              <div>
                <div style={{ fontSize: '14px', fontWeight: '600', marginBottom: '10px', display: 'flex', alignItems: 'center', gap: '8px' }}>
                  <span style={{ width: '8px', height: '8px', borderRadius: '50%', background: 'var(--warning)' }}></span>
                  Pending Triggers ({pending.length})
                </div>
                {pending.length === 0 ? (
                  <div style={{ color: 'var(--text-muted)', fontSize: '13px', padding: '10px', background: 'var(--surface)', borderRadius: '6px' }}>
                    No orchestrations pending trigger.
                  </div>
                ) : (
                  <div style={{ display: 'flex', flexDirection: 'column', gap: '8px' }}>
                    {pending.map((t, idx) => (
                      <div
                        key={t.orchestrationId || idx}
                        className="history-item"
                        style={{
                          background: 'var(--surface)',
                          borderRadius: '6px',
                          borderLeft: '3px solid var(--warning)',
                          cursor: 'pointer'
                        }}
                        onClick={() => handleViewOrchestration(t)}
                      >
                        <div className="history-status-icon" style={{ background: 'var(--warning)', color: 'white' }}>
                          <Icons.Clock />
                        </div>
                        <div className="history-info" style={{ flex: 1 }}>
                          <div className="history-name">{t.orchestrationName}</div>
                          <div className="history-time" style={{ display: 'flex', alignItems: 'center', gap: '6px' }}>
                            {getTriggerIcon(t.triggeredBy)}
                            <span>{t.triggeredBy}</span>
                          </div>
                        </div>
                        <div style={{ display: 'flex', alignItems: 'center', gap: '8px' }}>
                          <div style={{ fontSize: '12px', color: 'var(--text-muted)' }}>
                            Next: {t.nextFireTime ? new Date(t.nextFireTime).toLocaleString() : 'Unknown'}
                          </div>
                          <button
                            className="btn btn-sm"
                            onClick={(e: React.MouseEvent) => { e.stopPropagation(); handleViewOrchestration(t); }}
                            style={{ padding: '4px 8px', fontSize: '11px' }}
                          >
                            <Icons.Eye /> View
                          </button>
                        </div>
                      </div>
                    ))}
                  </div>
                )}
              </div>
            </>
          )}
        </div>
        <div className="modal-footer">
          <button className="btn btn-secondary" onClick={onRefresh}>Refresh</button>
          <button className="btn" onClick={onClose}>Close</button>
        </div>
      </div>
    </div>
  );
}

export default ActiveModal;
