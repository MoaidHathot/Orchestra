import React, { useState, useEffect, useMemo } from 'react';
import { Icons } from '../../icons';
import { api } from '../../api';
import { useFocusTrap } from '../../hooks/useFocusTrap';

interface McpEntry {
  name: string;
  type?: string;
  endpoint?: string;
  command?: string;
  arguments?: string[];
  usedByCount: number;
  usedBy?: string[];
}

interface McpsResponse {
  mcps: McpEntry[];
}

interface Props {
  open: boolean;
  onClose: () => void;
}

export default function McpsModal({ open, onClose }: Props): React.JSX.Element {
  const trapRef = useFocusTrap<HTMLDivElement>(open, onClose);
  const [mcps, setMcps] = useState<McpEntry[]>([]);
  const [loading, setLoading] = useState(true);
  const [searchQuery, setSearchQuery] = useState('');
  const [expandedMcp, setExpandedMcp] = useState<string | null>(null);

  useEffect(() => {
    if (open) {
      setLoading(true);
      api
        .get<McpsResponse>('/api/mcps')
        .then((data) => setMcps(data.mcps || []))
        .catch((err: unknown) => console.error('Failed to load MCPs:', err))
        .finally(() => setLoading(false));
    }
  }, [open]);

  const filteredMcps = useMemo(() => {
    if (!searchQuery) return mcps;
    const q = searchQuery.toLowerCase();
    return mcps.filter(
      (m) =>
        m.name?.toLowerCase().includes(q) ||
        m.type?.toLowerCase().includes(q) ||
        m.endpoint?.toLowerCase().includes(q) ||
        m.command?.toLowerCase().includes(q),
    );
  }, [mcps, searchQuery]);

  return (
    <div
      className={`modal-overlay ${open ? 'visible' : ''}`}
      ref={trapRef}
      onClick={(e: React.MouseEvent<HTMLDivElement>) => {
        if (e.target === e.currentTarget) onClose();
      }}
    >
      <div className="modal modal-lg" role="dialog" aria-modal="true" aria-label="MCP tools">
        <div className="modal-header">
          <div className="modal-title">MCP Tools ({mcps.length})</div>
          <button className="modal-close" aria-label="Close" onClick={onClose}>
            <Icons.X />
          </button>
        </div>
        <div className="modal-body">
          {/* Search */}
          <div className="search-box" style={{ marginBottom: '16px' }}>
            <span className="search-icon">
              <Icons.Search />
            </span>
            <input
              type="text"
              placeholder="Search MCPs..."
              value={searchQuery}
              onChange={(e: React.ChangeEvent<HTMLInputElement>) =>
                setSearchQuery(e.target.value)
              }
            />
          </div>

          {loading ? (
            <div className="empty-state">
              <div className="spinner"></div>
            </div>
          ) : filteredMcps.length === 0 ? (
            <div className="empty-state">
              <div className="empty-title">No MCPs Found</div>
              <div className="empty-text">
                {mcps.length === 0
                  ? 'No MCP tools are registered across any orchestration.'
                  : 'No MCPs match your search query.'}
              </div>
            </div>
          ) : (
            <div style={{ display: 'flex', flexDirection: 'column', gap: '8px' }}>
              {filteredMcps.map((mcp, i) => (
                <div
                  key={i}
                  style={{
                    background: 'var(--surface)',
                    borderRadius: '8px',
                    border: '1px solid var(--border)',
                    overflow: 'hidden',
                  }}
                >
                  <div
                    style={{
                      padding: '12px 16px',
                      cursor: 'pointer',
                      display: 'flex',
                      justifyContent: 'space-between',
                      alignItems: 'center',
                    }}
                    onClick={() =>
                      setExpandedMcp(expandedMcp === mcp.name ? null : mcp.name)
                    }
                  >
                    <div style={{ display: 'flex', alignItems: 'center', gap: '12px' }}>
                      <div
                        style={{
                          width: '8px',
                          height: '8px',
                          borderRadius: '50%',
                          background: mcp.type === 'Remote' ? '#58a6ff' : '#3fb950',
                        }}
                      />
                      <div>
                        <div style={{ fontWeight: 500, color: '#a371f7' }}>{mcp.name}</div>
                        <div className="text-muted" style={{ fontSize: '12px' }}>
                          {mcp.type === 'Remote' ? mcp.endpoint : mcp.command}
                        </div>
                      </div>
                    </div>
                    <div style={{ display: 'flex', alignItems: 'center', gap: '12px' }}>
                      <span
                        className={`badge badge-${mcp.type?.toLowerCase() || 'local'}`}
                      >
                        {mcp.type}
                      </span>
                      <span className="text-muted" style={{ fontSize: '12px' }}>
                        Used by {mcp.usedByCount} orchestration
                        {mcp.usedByCount !== 1 ? 's' : ''}
                      </span>
                      <span
                        style={{
                          color: 'var(--text-muted)',
                          transform:
                            expandedMcp === mcp.name
                              ? 'rotate(90deg)'
                              : 'rotate(0)',
                          transition: 'transform 0.2s',
                        }}
                      >
                        &#9654;
                      </span>
                    </div>
                  </div>

                  {expandedMcp === mcp.name && (
                    <div
                      style={{
                        padding: '12px 16px',
                        borderTop: '1px solid var(--border)',
                        background: 'var(--bg-secondary)',
                      }}
                    >
                      <div
                        style={{
                          display: 'grid',
                          gridTemplateColumns: '1fr 1fr',
                          gap: '12px',
                          marginBottom: '12px',
                        }}
                      >
                        <div>
                          <div
                            className="text-muted"
                            style={{ fontSize: '11px', marginBottom: '4px' }}
                          >
                            Type
                          </div>
                          <div>{mcp.type}</div>
                        </div>
                        {mcp.type === 'Remote' && mcp.endpoint && (
                          <div>
                            <div
                              className="text-muted"
                              style={{ fontSize: '11px', marginBottom: '4px' }}
                            >
                              Endpoint
                            </div>
                            <div style={{ fontSize: '13px', wordBreak: 'break-all' }}>
                              {mcp.endpoint}
                            </div>
                          </div>
                        )}
                        {mcp.type === 'Local' && (
                          <>
                            <div>
                              <div
                                className="text-muted"
                                style={{ fontSize: '11px', marginBottom: '4px' }}
                              >
                                Command
                              </div>
                              <div style={{ fontFamily: 'monospace', fontSize: '13px' }}>
                                {mcp.command}
                              </div>
                            </div>
                            {mcp.arguments && mcp.arguments.length > 0 && (
                              <div style={{ gridColumn: '1 / -1' }}>
                                <div
                                  className="text-muted"
                                  style={{ fontSize: '11px', marginBottom: '4px' }}
                                >
                                  Arguments
                                </div>
                                <div
                                  style={{
                                    fontFamily: 'monospace',
                                    fontSize: '12px',
                                    background: 'var(--surface)',
                                    padding: '8px',
                                    borderRadius: '4px',
                                  }}
                                >
                                  {mcp.arguments.join(' ')}
                                </div>
                              </div>
                            )}
                          </>
                        )}
                      </div>

                      <div>
                        <div
                          className="text-muted"
                          style={{ fontSize: '11px', marginBottom: '8px' }}
                        >
                          Used By
                        </div>
                        <div style={{ display: 'flex', flexWrap: 'wrap', gap: '6px' }}>
                          {mcp.usedBy?.map((orchId, j) => (
                            <span
                              key={j}
                              style={{
                                background: 'var(--surface)',
                                padding: '4px 10px',
                                borderRadius: '4px',
                                fontSize: '12px',
                                border: '1px solid var(--border)',
                              }}
                            >
                              {orchId}
                            </span>
                          ))}
                        </div>
                      </div>
                    </div>
                  )}
                </div>
              ))}
            </div>
          )}
        </div>
        <div className="modal-footer">
          <button className="btn" onClick={onClose}>
            Close
          </button>
        </div>
      </div>
    </div>
  );
}
