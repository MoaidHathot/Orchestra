import React, { useState } from 'react';
import { api } from '../../api';
import { useFocusTrap } from '../../hooks/useFocusTrap';
import { Icons } from '../../icons';

interface BrowseResponse {
  cancelled: boolean;
  path?: string;
}

interface ScannedProfile {
  path: string;
  fileName: string;
  name: string;
  description?: string;
  tags?: string[];
  hasSchedule: boolean;
  valid: boolean;
  error?: string;
}

interface ScanResponse {
  profiles: ScannedProfile[];
}

interface ImportResultItem {
  id: string;
  name: string;
  imported: boolean;
  skipReason?: string;
}

interface ImportResponse {
  imported: ImportResultItem[];
  skipped: ImportResultItem[];
  errors: string[];
}

interface ImportJsonResponse {
  id: string;
  name: string;
}

interface Props {
  open: boolean;
  onClose: () => void;
  onImported: () => void;
}

function ImportProfilesModal({ open, onClose, onImported }: Props): React.JSX.Element {
  const trapRef = useFocusTrap<HTMLDivElement>(open, onClose);
  const [activeTab, setActiveTab] = useState<'browse' | 'paste'>('browse');
  const [path, setPath] = useState('');
  const [files, setFiles] = useState<ScannedProfile[]>([]);
  const [selectedFiles, setSelectedFiles] = useState<string[]>([]);
  const [jsonContent, setJsonContent] = useState('');
  const [overwriteExisting, setOverwriteExisting] = useState(false);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState('');
  const [previewFile, setPreviewFile] = useState<ScannedProfile | null>(null);
  const [previewJson, setPreviewJson] = useState('');

  const openFolderDialog = async () => {
    setLoading(true);
    setError('');
    try {
      const result = await api.get<BrowseResponse>('/api/folder/browse');
      if (!result.cancelled && result.path) {
        setPath(result.path);
        const data = await api.post<ScanResponse>('/api/profiles/scan', { directory: result.path });
        setFiles(data.profiles || []);
        setPreviewFile(null);
        setSelectedFiles([]);
      }
    } catch (err) {
      setError((err as Error).message);
    } finally {
      setLoading(false);
    }
  };

  const scanFolder = async () => {
    if (!path) return;
    setLoading(true);
    setError('');
    try {
      const data = await api.post<ScanResponse>('/api/profiles/scan', { directory: path });
      setFiles(data.profiles || []);
      setPreviewFile(null);
      setSelectedFiles([]);
    } catch (err) {
      setError((err as Error).message);
    } finally {
      setLoading(false);
    }
  };

  const toggleFile = (filePath: string) => {
    setSelectedFiles(prev =>
      prev.includes(filePath) ? prev.filter(f => f !== filePath) : [...prev, filePath]
    );
  };

  const loadPreview = async (file: ScannedProfile) => {
    if (!file.valid) return;
    setPreviewFile(file);
    try {
      const response = await fetch(`/api/file/read?path=${encodeURIComponent(file.path)}`);
      if (response.ok) {
        const text = await response.text();
        setPreviewJson(text);
      } else {
        setPreviewJson(JSON.stringify(file, null, 2));
      }
    } catch {
      setPreviewJson(JSON.stringify(file, null, 2));
    }
  };

  const handleImport = async () => {
    setLoading(true);
    setError('');
    try {
      if (activeTab === 'browse') {
        const res = await api.post<ImportResponse>('/api/profiles/import', {
          paths: selectedFiles,
          overwriteExisting,
        });
        const importedCount = res.imported?.length ?? 0;
        const skippedCount = res.skipped?.length ?? 0;
        const errorCount = res.errors?.length ?? 0;

        if (errorCount > 0) {
          setError(`Imported ${importedCount}, skipped ${skippedCount}, errors: ${res.errors.join('; ')}`);
        }

        if (importedCount > 0) {
          onImported();
        }
        if (errorCount === 0) {
          onClose();
        }
      } else {
        await api.post<ImportJsonResponse>('/api/profiles/import-json', {
          json: jsonContent,
          overwriteExisting,
        });
        onImported();
        onClose();
      }
    } catch (err) {
      setError((err as Error).message);
    } finally {
      setLoading(false);
    }
  };

  return (
    <div className={`modal-overlay ${open ? 'visible' : ''}`} ref={trapRef} onClick={(e) => e.target === e.currentTarget && onClose()}>
      <div className="modal" role="dialog" aria-modal="true" aria-label="Import Profiles" style={{ maxWidth: previewFile ? '900px' : '600px', transition: 'max-width 0.2s ease' }}>
        <div className="modal-header">
          <div className="modal-title"><Icons.Upload /> Import Profiles</div>
          <button className="modal-close" onClick={onClose} aria-label="Close"><Icons.X /></button>
        </div>
        <div className="tabs">
          <div className={`tab ${activeTab === 'browse' ? 'active' : ''}`} onClick={() => setActiveTab('browse')}>Browse Files</div>
          <div className={`tab ${activeTab === 'paste' ? 'active' : ''}`} onClick={() => setActiveTab('paste')}>Paste JSON</div>
        </div>
        <div className="modal-body">
          {error && (
            <div style={{ background: 'rgba(248, 81, 73, 0.15)', border: '1px solid var(--error)', borderRadius: '6px', padding: '12px', marginBottom: '16px', color: 'var(--error)' }}>
              {error}
            </div>
          )}

          {activeTab === 'browse' && (
            <div style={{ display: 'flex', gap: '16px' }}>
              {/* Left side: file browser */}
              <div style={{ flex: previewFile ? '0 0 300px' : '1', minWidth: 0 }}>
                <div className="form-group">
                  <label className="form-label">Folder Path</label>
                  <div style={{ display: 'flex', gap: '8px' }}>
                    <input
                      type="text"
                      className="form-input"
                      placeholder="Enter folder path..."
                      value={path}
                      onChange={(e: React.ChangeEvent<HTMLInputElement>) => setPath(e.target.value)}
                      onKeyDown={(e: React.KeyboardEvent<HTMLInputElement>) => e.key === 'Enter' && scanFolder()}
                      style={{ flex: 1 }}
                    />
                    <button className="btn" onClick={openFolderDialog} disabled={loading} title="Open folder dialog">
                      <Icons.Folder />
                    </button>
                    <button className="btn btn-primary" onClick={scanFolder} disabled={loading || !path}>
                      {loading ? <div className="spinner"></div> : 'Scan'}
                    </button>
                  </div>
                </div>

                {files.length > 0 && (
                  <div className="form-group">
                    <label className="form-label">Select Profile Files ({selectedFiles.length} selected)</label>
                    <div className="file-browser" style={{ maxHeight: '300px' }}>
                      {files.map((file, i) => (
                        <div
                          key={i}
                          className={`file-item ${selectedFiles.includes(file.path) ? 'selected' : ''} ${!file.valid ? 'invalid' : ''} ${previewFile?.path === file.path ? 'previewing' : ''}`}
                          onClick={() => {
                            if (file.valid) {
                              toggleFile(file.path);
                              loadPreview(file);
                            }
                          }}
                          title={file.valid ? file.name : file.error}
                          style={{
                            borderLeft: previewFile?.path === file.path ? '3px solid var(--primary)' : 'none',
                            paddingLeft: previewFile?.path === file.path ? '9px' : '12px'
                          }}
                        >
                          <span className="file-icon"><Icons.File /></span>
                          <span className="file-name" style={{ flex: 1, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{file.fileName}</span>
                          {file.valid && <span className="file-orch-name" style={{ color: 'var(--text-muted)', fontSize: '10px' }}>{file.name}</span>}
                          {!file.valid && <span style={{ color: 'var(--error)', fontSize: '10px' }}>Invalid</span>}
                        </div>
                      ))}
                    </div>
                  </div>
                )}

                {/* Overwrite toggle */}
                <div className="form-group" style={{ marginTop: '12px' }}>
                  <label style={{ display: 'flex', alignItems: 'center', gap: '8px', cursor: 'pointer', fontSize: '13px' }}>
                    <input
                      type="checkbox"
                      checked={overwriteExisting}
                      onChange={(e) => setOverwriteExisting(e.target.checked)}
                    />
                    Overwrite existing profiles
                  </label>
                </div>
              </div>

              {/* Right side: preview pane */}
              {previewFile && (
                <div style={{ flex: 1, minWidth: 0, borderLeft: '1px solid var(--border)', paddingLeft: '16px' }}>
                  <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', marginBottom: '8px' }}>
                    <div style={{ fontWeight: 600, fontSize: '14px' }}>{previewFile.name}</div>
                    <button className="btn-icon" onClick={() => setPreviewFile(null)} title="Close preview">
                      <Icons.X />
                    </button>
                  </div>
                  {previewFile.description && (
                    <div style={{ fontSize: '12px', color: 'var(--text-muted)', marginBottom: '12px' }}>{previewFile.description}</div>
                  )}
                  <div style={{
                    background: 'var(--bg)',
                    borderRadius: '8px',
                    padding: '12px',
                    maxHeight: '300px',
                    overflow: 'auto',
                    fontSize: '12px'
                  }}>
                    {/* Profile details */}
                    <div style={{ marginBottom: '12px' }}>
                      <div style={{ color: 'var(--text-muted)', fontSize: '10px', textTransform: 'uppercase', marginBottom: '4px' }}>Details</div>
                      <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: '4px 12px' }}>
                        <div><span style={{ color: 'var(--text-dim)' }}>Name:</span> {previewFile.name}</div>
                        <div><span style={{ color: 'var(--text-dim)' }}>Schedule:</span> {previewFile.hasSchedule ? 'Yes' : 'No'}</div>
                      </div>
                    </div>

                    {/* Tags */}
                    {previewFile.tags && previewFile.tags.length > 0 && (
                      <div style={{ marginBottom: '12px' }}>
                        <div style={{ color: 'var(--text-muted)', fontSize: '10px', textTransform: 'uppercase', marginBottom: '4px' }}>Tags</div>
                        <div style={{ display: 'flex', flexWrap: 'wrap', gap: '4px' }}>
                          {previewFile.tags.map((tag, i) => (
                            <span key={i} style={{ background: 'var(--surface)', padding: '2px 6px', borderRadius: '4px', fontSize: '11px' }}>
                              {tag === '*' ? 'all (*)' : tag}
                            </span>
                          ))}
                        </div>
                      </div>
                    )}

                    {/* Raw JSON */}
                    <div>
                      <div style={{ color: 'var(--text-muted)', fontSize: '10px', textTransform: 'uppercase', marginBottom: '4px' }}>JSON</div>
                      <pre style={{
                        background: 'var(--surface)',
                        borderRadius: '6px',
                        padding: '8px',
                        fontSize: '11px',
                        maxHeight: '150px',
                        overflow: 'auto',
                        margin: 0
                      }}>
                        {previewJson}
                      </pre>
                    </div>
                  </div>
                </div>
              )}
            </div>
          )}

          {activeTab === 'paste' && (
            <>
              <div className="form-group">
                <label className="form-label">Profile JSON</label>
                <textarea
                  className="form-textarea"
                  placeholder="Paste your profile JSON here..."
                  value={jsonContent}
                  onChange={(e: React.ChangeEvent<HTMLTextAreaElement>) => setJsonContent(e.target.value)}
                  style={{ minHeight: '300px' }}
                />
              </div>
              <div className="form-group" style={{ marginTop: '12px' }}>
                <label style={{ display: 'flex', alignItems: 'center', gap: '8px', cursor: 'pointer', fontSize: '13px' }}>
                  <input
                    type="checkbox"
                    checked={overwriteExisting}
                    onChange={(e) => setOverwriteExisting(e.target.checked)}
                  />
                  Overwrite existing profiles
                </label>
              </div>
            </>
          )}

          <div style={{ marginTop: '12px', padding: '8px 12px', background: 'rgba(139, 148, 158, 0.1)', borderRadius: '6px', fontSize: '12px', color: 'var(--text-muted)' }}>
            Imported profiles are always set to <strong>inactive</strong>. You can activate them after import.
          </div>
        </div>
        <div className="modal-footer">
          <button className="btn" onClick={onClose}>Cancel</button>
          <button
            className="btn btn-primary"
            onClick={handleImport}
            disabled={loading || (activeTab === 'browse' ? selectedFiles.length === 0 : !jsonContent.trim())}
          >
            {loading ? <div className="spinner"></div> : 'Import Profiles'}
          </button>
        </div>
      </div>
    </div>
  );
}

export default ImportProfilesModal;
