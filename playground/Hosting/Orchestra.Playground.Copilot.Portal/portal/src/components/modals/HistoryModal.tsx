import React, { useState, useEffect, useMemo, useRef, useCallback } from 'react';
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
  completionReason?: string;
  completedByStep?: string;
  isActive?: boolean;
  isIncomplete?: boolean;
  startedAt?: string;
  durationSeconds?: number;
  parameters?: Record<string, unknown>;
  hookExecutionCount?: number;
}

interface PaginatedHistoryResponse {
  total: number;
  offset: number;
  limit: number;
  count: number;
  runs: HistoryExecution[];
}

interface SearchHistoryResponse {
  total: number;
  count: number;
  runs: HistoryExecution[];
}

const PAGE_SIZE = 300;

type StatusFilter = 'all' | 'Succeeded' | 'Failed' | 'Cancelled' | 'Running' | 'Incomplete';

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
  const [loadingMore, setLoadingMore] = useState(false);
  const [totalExecutions, setTotalExecutions] = useState(0);

  // Filter state
  const [searchQuery, setSearchQuery] = useState('');
  const [statusFilter, setStatusFilter] = useState<StatusFilter>('all');

  // "Search all" toggle -- when enabled, searches across ALL stored orchestrations via the backend
  const [searchAll, setSearchAll] = useState(false);
  const [serverSearchResults, setServerSearchResults] = useState<HistoryExecution[] | null>(null);
  const [serverSearchLoading, setServerSearchLoading] = useState(false);
  const searchDebounceRef = useRef<ReturnType<typeof setTimeout> | null>(null);

  useEffect(() => {
    if (open) {
      setLoading(true);
      api.get<PaginatedHistoryResponse>(`/api/history/all?offset=0&limit=${PAGE_SIZE}`)
        .then(data => {
          setHistory(data.runs || []);
          setTotalExecutions(data.total ?? 0);
        })
        .finally(() => setLoading(false));
    }
  }, [open]);

  const loadMore = () => {
    setLoadingMore(true);
    const nextOffset = history.length;
    api.get<PaginatedHistoryResponse>(`/api/history/all?offset=${nextOffset}&limit=${PAGE_SIZE}`)
      .then(data => {
        setHistory(prev => [...prev, ...(data.runs || [])]);
        setTotalExecutions(data.total ?? totalExecutions);
      })
      .finally(() => setLoadingMore(false));
  };

  const hasMore = history.length < totalExecutions;

  // Reset filters when modal opens
  useEffect(() => {
    if (open) {
      setSearchQuery('');
      setStatusFilter('all');
      setServerSearchResults(null);
      setServerSearchLoading(false);
    }
  }, [open]);

  // Debounced server-side search when searchAll is enabled
  const performServerSearch = useCallback((query: string) => {
    if (!query.trim()) {
      setServerSearchResults(null);
      setServerSearchLoading(false);
      return;
    }

    setServerSearchLoading(true);
    api.get<SearchHistoryResponse>(`/api/history/search?query=${encodeURIComponent(query)}`)
      .then(data => {
        setServerSearchResults(data.runs || []);
      })
      .catch(() => {
        setServerSearchResults(null);
      })
      .finally(() => setServerSearchLoading(false));
  }, []);

  // When searchAll is toggled on or search query changes with searchAll enabled, trigger server search
  useEffect(() => {
    if (!searchAll || !searchQuery.trim()) {
      setServerSearchResults(null);
      return;
    }

    // Debounce the server search
    if (searchDebounceRef.current) {
      clearTimeout(searchDebounceRef.current);
    }
    searchDebounceRef.current = setTimeout(() => {
      performServerSearch(searchQuery);
    }, 300);

    return () => {
      if (searchDebounceRef.current) {
        clearTimeout(searchDebounceRef.current);
      }
    };
  }, [searchAll, searchQuery, performServerSearch]);

  // Determine which results to show based on search mode
  const displayResults = useMemo(() => {
    // If searchAll is on and we have a query, use server results
    if (searchAll && searchQuery.trim() && serverSearchResults !== null) {
      let results = serverSearchResults;

      // Apply status filter on top of server results
      if (statusFilter !== 'all') {
        results = results.filter(exec => {
          if (statusFilter === 'Running') return exec.isActive;
          if (statusFilter === 'Incomplete') return exec.isIncomplete || (exec.completionReason && exec.status === 'Succeeded');
          if (statusFilter === 'Succeeded') return exec.status === 'Succeeded' && !exec.isIncomplete && !exec.completionReason;
          return exec.status === statusFilter;
        });
      }

      return results;
    }

    // Otherwise, filter locally from loaded history
    let results = history;

    if (searchQuery) {
      const q = searchQuery.toLowerCase();
      results = results.filter(exec =>
        exec.orchestrationName?.toLowerCase().includes(q) ||
        exec.runId?.toLowerCase().includes(q)
      );
    }

    if (statusFilter !== 'all') {
      results = results.filter(exec => {
        if (statusFilter === 'Running') return exec.isActive;
        if (statusFilter === 'Incomplete') return exec.isIncomplete || (exec.completionReason && exec.status === 'Succeeded');
        if (statusFilter === 'Succeeded') return exec.status === 'Succeeded' && !exec.isIncomplete && !exec.completionReason;
        return exec.status === statusFilter;
      });
    }

    return results;
  }, [history, searchQuery, statusFilter, searchAll, serverSearchResults]);

  // Status counts based on loaded history (always from local data)
  const statusCounts = useMemo(() => {
    const source = (searchAll && searchQuery.trim() && serverSearchResults !== null)
      ? serverSearchResults
      : history;
    const counts = { all: source.length, Succeeded: 0, Failed: 0, Cancelled: 0, Running: 0, Incomplete: 0 };
    for (const exec of source) {
      if (exec.isActive) counts.Running++;
      else if (exec.isIncomplete || (exec.completionReason && exec.status === 'Succeeded')) counts.Incomplete++;
      else if (exec.status === 'Succeeded') counts.Succeeded++;
      else if (exec.status === 'Failed') counts.Failed++;
      else if (exec.status === 'Cancelled') counts.Cancelled++;
    }
    return counts;
  }, [history, searchAll, searchQuery, serverSearchResults]);

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
    { value: 'Incomplete', label: 'Incomplete', color: 'var(--text-muted)' },
    { value: 'Failed', label: 'Failed', color: 'var(--error)' },
    { value: 'Cancelled', label: 'Cancelled', color: 'var(--warning)' },
    { value: 'Running', label: 'Running', color: 'var(--warning)' },
  ];

  const isSearching = serverSearchLoading;

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
            {/* Search bar with "Search All" toggle */}
            <div style={{ display: 'flex', gap: '8px', alignItems: 'center' }}>
              <div className="search-box" role="search" style={{ flex: 1 }}>
                <span className="search-icon" aria-hidden="true"><Icons.Search /></span>
                <input
                  type="text"
                  placeholder={searchAll ? 'Search all orchestrations...' : 'Search by name or run ID...'}
                  value={searchQuery}
                  onChange={(e: React.ChangeEvent<HTMLInputElement>) => setSearchQuery(e.target.value)}
                  aria-label="Search execution history"
                  data-autofocus
                />
                {isSearching && (
                  <span style={{ position: 'absolute', right: '10px', top: '50%', transform: 'translateY(-50%)' }}>
                    <span className="spinner" style={{ width: '14px', height: '14px' }}></span>
                  </span>
                )}
              </div>
              <button
                className={`btn btn-sm ${searchAll ? 'btn-primary' : ''}`}
                onClick={() => setSearchAll(prev => !prev)}
                title={searchAll
                  ? 'Searching all stored orchestrations — click to search loaded only'
                  : 'Searching loaded orchestrations only — click to search all'}
                aria-pressed={searchAll}
                style={{
                  whiteSpace: 'nowrap',
                  minWidth: 'fit-content',
                  ...(searchAll ? {} : { borderColor: 'var(--text-muted)', color: 'var(--text-muted)' }),
                }}
              >
                <Icons.Search /> Search All
              </button>
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
          ) : displayResults.length === 0 ? (
            <div className="empty-state">
              <div className="empty-title">
                {history.length === 0 && !searchQuery ? 'No Execution History' : 'No matching executions'}
              </div>
              {(history.length > 0 || searchQuery) && (
                <div className="empty-text">
                  {searchAll && searchQuery
                    ? 'No results found across all stored orchestrations'
                    : 'Try adjusting your search or filter criteria'}
                  {!searchAll && searchQuery && (
                    <div style={{ marginTop: '8px' }}>
                      <button className="btn btn-sm btn-primary" onClick={() => setSearchAll(true)}>
                        Search All Orchestrations
                      </button>
                    </div>
                  )}
                </div>
              )}
            </div>
          ) : (
            <div style={{ display: 'flex', flexDirection: 'column', gap: '8px' }} role="list" aria-label="Execution results">
              {displayResults.map(exec => (
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
                  <div className={`history-status-icon ${(exec.isIncomplete || exec.completionReason) && exec.status === 'Succeeded' ? 'completed-early' : exec.status?.toLowerCase() || 'running'}`} aria-hidden="true">
                    {exec.isActive ? (
                      <span className="spinner" style={{ width: '12px', height: '12px' }}></span>
                    ) : exec.status === 'Succeeded' && (exec.completionReason || exec.isIncomplete) ? (
                      <Icons.SkipForward />
                    ) : exec.status === 'Succeeded' ? (
                      <Icons.Check />
                    ) : exec.status === 'Failed' ? (
                      <Icons.X />
                    ) : exec.status === 'Cancelled' ? (
                      <Icons.Ban />
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
                    {(exec.hookExecutionCount ?? 0) > 0 && (
                      <div className="history-time">{exec.hookExecutionCount} hooks</div>
                    )}
                  </div>
                  <div style={{ fontSize: '12px', color: 'var(--text-muted)' }}>
                    {exec.isActive ? 'In progress' : `${exec.durationSeconds}s`}
                  </div>
                </div>
              ))}
              {hasMore && !searchQuery && statusFilter === 'all' && (
                <button
                  className="btn btn-sm"
                  onClick={loadMore}
                  disabled={loadingMore}
                  style={{ alignSelf: 'center', marginTop: '8px' }}
                >
                  {loadingMore ? 'Loading...' : `Load More (${history.length} of ${totalExecutions})`}
                </button>
              )}
            </div>
          )}
        </div>
        <div className="modal-footer">
          <span style={{ fontSize: '12px', color: 'var(--text-muted)', marginRight: 'auto' }}>
            {searchAll && searchQuery.trim() && serverSearchResults !== null
              ? `Found ${displayResults.length} results across all orchestrations`
              : <>
                  Showing {displayResults.length} of {totalExecutions} executions
                  {history.length < totalExecutions && ` (${history.length} loaded)`}
                </>
            }
          </span>
          <button className="btn" onClick={onClose}>Close</button>
        </div>
      </div>
    </div>
  );
}

export default HistoryModal;
