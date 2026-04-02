import React, { useState } from 'react';
import { api } from '../../api';
import { useFocusTrap } from '../../hooks/useFocusTrap';
import { Icons } from '../../icons';

interface BrowseResponse {
  cancelled: boolean;
  path?: string;
}

interface ExportResultItem {
  id: string;
  name: string;
  path?: string;
  exported?: boolean;
  skipReason?: string;
}

interface ExportResponse {
  exported: ExportResultItem[];
  skipped: ExportResultItem[];
  errors?: ExportResultItem[];
}

interface Props {
  open: boolean;
  onClose: () => void;
  title: string;
  /** API endpoint to POST to, e.g. '/api/profiles/export' */
  endpoint: string;
  /** IDs to export. If empty/undefined, exports all. */
  ids?: string[];
  /** The field name for the IDs array in the request body, e.g. 'profileIds' or 'orchestrationIds' */
  idsField: string;
}

function ExportModal({ open, onClose, title, endpoint, ids, idsField }: Props): React.JSX.Element {
  const trapRef = useFocusTrap<HTMLDivElement>(open, onClose);
  const [directory, setDirectory] = useState('');
  const [overwriteExisting, setOverwriteExisting] = useState(false);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState('');
  const [result, setResult] = useState<ExportResponse | null>(null);

  const openFolderDialog = async () => {
    setLoading(true);
    setError('');
    try {
      const res = await api.get<BrowseResponse>('/api/folder/browse');
      if (!res.cancelled && res.path) {
        setDirectory(res.path);
      }
    } catch (err) {
      setError((err as Error).message);
    } finally {
      setLoading(false);
    }
  };

  const handleExport = async () => {
    if (!directory.trim()) return;
    setLoading(true);
    setError('');
    setResult(null);
    try {
      const body: Record<string, unknown> = {
        directory: directory.trim(),
        overwriteExisting,
      };
      if (ids && ids.length > 0) {
        body[idsField] = ids;
      }
      const res = await api.post<ExportResponse>(endpoint, body);
      setResult(res);
    } catch (err) {
      setError((err as Error).message);
    } finally {
      setLoading(false);
    }
  };

  const handleClose = () => {
    setDirectory('');
    setOverwriteExisting(false);
    setError('');
    setResult(null);
    onClose();
  };

  const totalExported = result?.exported?.length ?? 0;
  const totalSkipped = result?.skipped?.length ?? 0;
  const totalErrors = result?.errors?.length ?? 0;

  return (
    <div className={`modal-overlay ${open ? 'visible' : ''}`} ref={trapRef} onClick={(e) => e.target === e.currentTarget && handleClose()}>
      <div className="modal" role="dialog" aria-modal="true" aria-label={title} style={{ maxWidth: '550px' }}>
        <div className="modal-header">
          <div className="modal-title"><Icons.Download /> {title}</div>
          <button className="modal-close" onClick={handleClose} aria-label="Close"><Icons.X /></button>
        </div>
        <div className="modal-body">
          {error && (
            <div style={{ background: 'rgba(248, 81, 73, 0.15)', border: '1px solid var(--error)', borderRadius: '6px', padding: '12px', marginBottom: '16px', color: 'var(--error)' }}>
              {error}
            </div>
          )}

          {!result ? (
            <>
              <div className="form-group">
                <label className="form-label">Export Directory</label>
                <div style={{ display: 'flex', gap: '8px' }}>
                  <input
                    type="text"
                    className="form-input"
                    placeholder="Select a folder..."
                    value={directory}
                    onChange={(e: React.ChangeEvent<HTMLInputElement>) => setDirectory(e.target.value)}
                    style={{ flex: 1 }}
                  />
                  <button className="btn" onClick={openFolderDialog} disabled={loading} title="Browse for folder">
                    <Icons.Folder />
                  </button>
                </div>
              </div>

              <div className="form-group" style={{ marginTop: '12px' }}>
                <label style={{ display: 'flex', alignItems: 'center', gap: '8px', cursor: 'pointer', fontSize: '13px' }}>
                  <input
                    type="checkbox"
                    checked={overwriteExisting}
                    onChange={(e) => setOverwriteExisting(e.target.checked)}
                  />
                  Overwrite existing files
                </label>
              </div>
            </>
          ) : (
            <div>
              {totalExported > 0 && (
                <div style={{ marginBottom: '12px' }}>
                  <div style={{ color: 'var(--success)', fontWeight: 600, fontSize: '13px', marginBottom: '6px' }}>
                    Exported ({totalExported})
                  </div>
                  {result.exported.map((item, i) => (
                    <div key={i} style={{ fontSize: '12px', padding: '4px 0', borderBottom: '1px solid var(--border)' }}>
                      <span>{item.name}</span>
                      {item.path && <span style={{ color: 'var(--text-muted)', marginLeft: '8px', fontSize: '11px' }}>{item.path}</span>}
                    </div>
                  ))}
                </div>
              )}

              {totalSkipped > 0 && (
                <div style={{ marginBottom: '12px' }}>
                  <div style={{ color: 'var(--warning)', fontWeight: 600, fontSize: '13px', marginBottom: '6px' }}>
                    Skipped ({totalSkipped})
                  </div>
                  {result.skipped.map((item, i) => (
                    <div key={i} style={{ fontSize: '12px', padding: '4px 0', borderBottom: '1px solid var(--border)' }}>
                      <span>{item.name}</span>
                      {item.skipReason && <span style={{ color: 'var(--text-muted)', marginLeft: '8px', fontSize: '11px' }}>{item.skipReason}</span>}
                    </div>
                  ))}
                </div>
              )}

              {totalErrors > 0 && (
                <div>
                  <div style={{ color: 'var(--error)', fontWeight: 600, fontSize: '13px', marginBottom: '6px' }}>
                    Errors ({totalErrors})
                  </div>
                  {result.errors!.map((item, i) => (
                    <div key={i} style={{ fontSize: '12px', padding: '4px 0', borderBottom: '1px solid var(--border)', color: 'var(--error)' }}>
                      <span>{item.name}</span>
                      {item.skipReason && <span style={{ marginLeft: '8px', fontSize: '11px' }}>{item.skipReason}</span>}
                    </div>
                  ))}
                </div>
              )}

              {totalExported === 0 && totalSkipped === 0 && totalErrors === 0 && (
                <div style={{ color: 'var(--text-muted)', textAlign: 'center', padding: '20px' }}>
                  Nothing to export.
                </div>
              )}
            </div>
          )}
        </div>
        <div className="modal-footer">
          <button className="btn" onClick={handleClose}>{result ? 'Close' : 'Cancel'}</button>
          {!result && (
            <button
              className="btn btn-primary"
              onClick={handleExport}
              disabled={loading || !directory.trim()}
            >
              {loading ? <div className="spinner"></div> : 'Export'}
            </button>
          )}
        </div>
      </div>
    </div>
  );
}

export default ExportModal;
