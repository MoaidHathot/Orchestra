import React, { useState, useEffect, useMemo } from 'react';
import type { Orchestration } from '../../types';
import { api } from '../../api';
import { Icons } from '../../icons';
import { formatTime } from '../../utils';
import { useFocusTrap } from '../../hooks/useFocusTrap';

/** Shape of a single execution entry returned by /api/history. */
interface HistoryExecution {
  runId: string;
  executionId?: string;
  orchestrationId: string;
  orchestrationName: string;
  status?: string;
  isActive?: boolean;
  startedAt?: string;
  durationSeconds?: number;
  parameters?: Record<string, unknown>;
}

interface HistoryResponse {
  runs: HistoryExecution[];
}

type StatusFilter = 'all' | 'Succeeded' | 'Failed' | 'Cancelled' | 'Running';

interface Props {
  open: boolean;
  onClose: () => void;
  onAttachToExecution?: (exec: HistoryExecution, orchestration: Orchestration | undefined) => void;
  onViewExecution?: (exec: HistoryExecution) => void;
  orchestrations?: Orchestration[];
}

function HistoryModal({ open, onClose, onAttachToExecution, onViewExecution, orchestrations }: Props): React.JSX.Element {
  const trapRef = useFocusTrap<HTMLDivElement>(open, onClose);
  const [history, setHistory] = useState<HistoryExecution[]>([]);
  const [loading, setLoading] = useState(true);

  // Filter state
  const [searchQuery, setSearchQuery] = useState('');
  const [statusFilter, setStatusFilter] = useState<StatusFilter>('all');

  useEffect(() => {
    if (open) {
      setLoading(true);
      api.get<HistoryResponse>('/api/history?limit=100')
        .then(data => setHistory(data.runs || []))
        .finally(() => setLoading(false));
    }
  }, [open]);

  // Reset filters when modal opens
  useEffect(() => {
    if (open) {
      setSearchQuery('');
      setStatusFilter('all');
    }
  }, [open]);

  const filteredHistory = useMemo(() => {
    let results = history;

    // Filter by search query
    if (searchQuery) {
      const q = searchQuery.toLowerCase();
      results = results.filter(exec =>
        exec.orchestrationName?.toLowerCase().includes(q) ||
        exec.runId?.toLowerCase().includes(q)
      );
    }

    // Filter by status
    if (statusFilter !== 'all') {
      results = results.filter(exec => {
        if (statusFilter === 'Running') {
          return exec.isActive;
        }
        return exec.status === statusFilter;
      });
    }

    return results;
  }, [history, searchQuery, statusFilter]);

  // Unique orchestration names for the count display
  const statusCounts = useMemo(() => {
    const counts = { all: history.length, Succeeded: 0, Failed: 0, Cancelled: 0, Running: 0 };
    for (const exec of history) {
      if (exec.isActive) counts.Running++;
      else if (exec.status === 'Succeeded') counts.Succeeded++;
      else if (exec.status === 'Failed') counts.Failed++;
      else if (exec.status === 'Cancelled') counts.Cancelled++;
    }
    return counts;
  }, [history]);

  const handleClick = (exec: HistoryExecution) => {
    if (exec.isActive && exec.executionId && onAttachToExecution) {
      const orch = orchestrations?.find(o => o.id === exec.orchestrationId);
      onClose();
      onAttachToExecution(exec, orch);
    } else if (onViewExecution) {
      onClose();
      onViewExecution(exec);
    }
  };

  const statusFilters: { value: StatusFilter; label: string; color: string }[] = [
    { value: 'all', label: 'All', color: 'var(--text-muted)' },
    { value: 'Succeeded', label: 'Succeeded', color: 'var(--success)' },
    { value: 'Failed', label: 'Failed', color: 'var(--error)' },
    { value: 'Cancelled', label: 'Cancelled', color: 'var(--text-muted)' },
    { value: 'Running', label: 'Running', color: 'var(--warning)' },
  ];

  return (
    <div className={`modal-overlay ${open ? 'visible' : ''}`} ref={trapRef} onClick={(e) => e.target === e.currentTarget && onClose()}>
      <div className="modal modal-lg" role="dialog" aria-modal="true" aria-label="Execution history">
        <div className="modal-header">
          <div className="modal-title">Execution History</div>
          <button className="modal-close" aria-label="Close" onClick={onClose}><Icons.X /></button>
        </div>
        <div className="modal-body">
          {/* Filter controls */}
          <div style={{ marginBottom: '16px', display: 'flex', flexDirection: 'column', gap: '10px' }}>
            {/* Search bar */}
            <div className="search-box" role="search">
              <span className="search-icon" aria-hidden="true"><Icons.Search /></span>
              <input
                type="text"
                placeholder="Search by name or run ID..."
                value={searchQuery}
                onChange={(e: React.ChangeEvent<HTMLInputElement>) => setSearchQuery(e.target.value)}
                aria-label="Search execution history"
                data-autofocus
              />
            </div>

            {/* Status filter pills */}
            <div style={{ display: 'flex', gap: '6px', flexWrap: 'wrap' }} role="group" aria-label="Filter by status">
              {statusFilters.map(f => (
                <button
                  key={f.value}
                  className={`btn btn-sm ${statusFilter === f.value ? 'btn-primary' : ''}`}
                  onClick={() => setStatusFilter(f.value)}
                  aria-pressed={statusFilter === f.value}
                  style={statusFilter === f.value ? {} : { borderColor: f.color, color: f.color }}
                >
                  {f.label} ({statusCounts[f.value]})
                </button>
              ))}
            </div>
          </div>

          {loading ? (
            <div className="empty-state"><div className="spinner"></div></div>
          ) : filteredHistory.length === 0 ? (
            <div className="empty-state">
              <div className="empty-title">
                {history.length === 0 ? 'No Execution History' : 'No matching executions'}
              </div>
              {history.length > 0 && (
                <div className="empty-text">
                  Try adjusting your search or filter criteria
                </div>
              )}
            </div>
          ) : (
            <div style={{ display: 'flex', flexDirection: 'column', gap: '8px' }} role="list" aria-label="Execution results">
              {filteredHistory.map(exec => (
                <div
                  key={exec.runId}
                  className="history-item"
                  role="listitem"
                  tabIndex={0}
                  style={{ background: 'var(--surface)', borderRadius: '6px', cursor: 'pointer' }}
                  onClick={() => handleClick(exec)}
                  onKeyDown={(e: React.KeyboardEvent) => {
                    if (e.key === 'Enter' || e.key === ' ') {
                      e.preventDefault();
                      handleClick(exec);
                    }
                  }}
                  aria-label={`${exec.orchestrationName} - ${exec.status || 'Running'} - ${formatTime(exec.startedAt)}`}
                >
                  <div className={`history-status-icon ${exec.status?.toLowerCase() || 'running'}`} aria-hidden="true">
                    {exec.isActive ? (
                      <span className="spinner" style={{ width: '12px', height: '12px' }}></span>
                    ) : exec.status === 'Succeeded' ? (
                      <Icons.Check />
                    ) : exec.status === 'Failed' ? (
                      <Icons.X />
                    ) : exec.status === 'Cancelled' ? (
                      <Icons.X />
                    ) : (
                      <span>...</span>
                    )}
                  </div>
                  <div className="history-info">
                    <div className="history-name">
                      {exec.orchestrationName}
                      {exec.isActive && (
                        <span className="step-status-badge running" style={{
                          marginLeft: '8px',
                          fontSize: '10px',
                          padding: '2px 6px'
                        }}>
                          {exec.status === 'Cancelling' ? 'Cancelling' : 'Running'}
                        </span>
                      )}
                    </div>
                    <div className="history-time">{formatTime(exec.startedAt)}</div>
                  </div>
                  <div style={{ fontSize: '12px', color: 'var(--text-muted)' }}>
                    {exec.isActive ? 'In progress' : `${exec.durationSeconds}s`}
                  </div>
                </div>
              ))}
            </div>
          )}
        </div>
        <div className="modal-footer">
          <span style={{ fontSize: '12px', color: 'var(--text-muted)', marginRight: 'auto' }}>
            Showing {filteredHistory.length} of {history.length} executions
          </span>
          <button className="btn" onClick={onClose}>Close</button>
        </div>
      </div>
    </div>
  );
}

export default HistoryModal;
