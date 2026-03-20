import React, { useState, useEffect, useRef } from 'react';
import { api } from '../../api';
import { useFocusTrap } from '../../hooks/useFocusTrap';
import { Icons } from '../../icons';
import { renderMermaidDag } from '../../mermaid';
import type { Orchestration } from '../../types';

/** Shape of a scanned file entry returned by /api/folder/scan. */
interface ScannedFile {
  path: string;
  fileName: string;
  name: string;
  description?: string;
  valid: boolean;
  error?: string;
}

interface ScanResponse {
  orchestrations: ScannedFile[];
}

interface BrowseResponse {
  cancelled: boolean;
  path?: string;
}

/**
 * Loose orchestration shape used for preview – the JSON may include fields
 * with PascalCase or camelCase keys depending on the serializer.
 */
interface PreviewOrchestration {
  name?: string;
  Name?: string;
  version?: string;
  Version?: string;
  steps?: PreviewStep[];
  Steps?: PreviewStep[];
  trigger?: PreviewTrigger;
  Trigger?: PreviewTrigger;
  mcps?: PreviewMcp[];
  Mcps?: PreviewMcp[];
  parameters?: Record<string, unknown>;
  Parameters?: Record<string, unknown>;
}

interface PreviewStep {
  name?: string;
  Name?: string;
  model?: string;
  Model?: string;
  loopConfig?: PreviewLoopConfig;
  LoopConfig?: PreviewLoopConfig;
  loop?: PreviewLoopConfig;
  Loop?: PreviewLoopConfig;
  inputHandlerPrompt?: string;
  InputHandlerPrompt?: string;
  outputHandlerPrompt?: string;
  OutputHandlerPrompt?: string;
  mcps?: PreviewMcp[] | string[];
  Mcps?: PreviewMcp[] | string[];
  mcp?: PreviewMcp[] | string[] | string;
  Mcp?: PreviewMcp[] | string[] | string;
  parameters?: string[];
  Parameters?: string[];
  dependsOn?: string[];
}

interface PreviewLoopConfig {
  target?: string;
  Target?: string;
  maxIterations?: number;
  MaxIterations?: number;
}

interface PreviewMcp {
  name?: string;
  Name?: string;
  [key: string]: unknown;
}

interface PreviewTrigger {
  type?: string;
  Type?: string;
  schedule?: string;
  Schedule?: string;
  inputHandlerPrompt?: string;
}

interface Props {
  open: boolean;
  onClose: () => void;
  onAdded: () => void;
}

function AddModal({ open, onClose, onAdded }: Props): React.JSX.Element {
  const trapRef = useFocusTrap<HTMLDivElement>(open, onClose);
  const [activeTab, setActiveTab] = useState<'browse' | 'paste'>('browse');
  const [path, setPath] = useState('');
  const [files, setFiles] = useState<ScannedFile[]>([]);
  const [selectedFiles, setSelectedFiles] = useState<string[]>([]);
  const [jsonContent, setJsonContent] = useState('');
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState('');
  const [previewFile, setPreviewFile] = useState<ScannedFile | null>(null);
  const [previewJson, setPreviewJson] = useState('');
  const [previewOrch, setPreviewOrch] = useState<PreviewOrchestration | null>(null);
  const [previewTab, setPreviewTab] = useState<'details' | 'graph' | 'json'>('details');
  const previewDagRef = useRef<HTMLDivElement>(null);

  // Open folder browser dialog
  const openFolderDialog = async () => {
    setLoading(true);
    setError('');
    try {
      const result = await api.get<BrowseResponse>('/api/folder/browse');
      if (!result.cancelled && result.path) {
        setPath(result.path);
        // Auto-scan the selected folder
        const data = await api.post<ScanResponse>('/api/folder/scan', { directory: result.path });
        setFiles(data.orchestrations || []);
        setPreviewFile(null);
        setPreviewOrch(null);
        setSelectedFiles([]);
      }
    } catch (err) {
      setError((err as Error).message);
    } finally {
      setLoading(false);
    }
  };

  const browseFolder = async () => {
    if (!path) return;
    setLoading(true);
    setError('');
    try {
      const data = await api.post<ScanResponse>('/api/folder/scan', { directory: path });
      setFiles(data.orchestrations || []);
      setPreviewFile(null);
      setPreviewOrch(null);
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

  // Load preview for a file
  const loadPreview = async (file: ScannedFile) => {
    if (!file.valid) return;
    setPreviewFile(file);
    setPreviewOrch(null);
    try {
      const response = await fetch(
        file.path.startsWith('http') ? file.path : `/api/file/read?path=${encodeURIComponent(file.path)}`
      );
      if (response.ok) {
        const text = await response.text();
        setPreviewJson(text);
        try {
          const parsed = JSON.parse(text) as PreviewOrchestration;
          setPreviewOrch(parsed);
        } catch (parseErr) {
          console.error('Failed to parse orchestration JSON:', parseErr);
        }
      } else {
        setPreviewJson(JSON.stringify(file, null, 2));
      }
    } catch {
      setPreviewJson(JSON.stringify(file, null, 2));
    }
  };

  // Render preview DAG when orchestration or tab changes
  useEffect(() => {
    if (previewOrch && previewTab === 'graph' && previewDagRef.current) {
      const steps = previewOrch.steps || previewOrch.Steps || [];
      if (steps.length > 0) {
        renderMermaidDag(previewOrch as unknown as Orchestration, previewDagRef.current);
      } else {
        previewDagRef.current.innerHTML = '<div style="color: var(--text-muted); text-align: center; padding: 40px;">No steps defined in this orchestration</div>';
      }
    } else if (previewFile && previewTab === 'graph' && previewDagRef.current && !previewOrch) {
      previewDagRef.current.innerHTML = '<div style="color: var(--text-muted); text-align: center; padding: 40px;">Loading...</div>';
    }
  }, [previewOrch, previewTab, previewFile]);

  const addOrchestrations = async () => {
    setLoading(true);
    setError('');
    try {
      if (activeTab === 'browse') {
        await api.post('/api/orchestrations/add', { paths: selectedFiles });
      } else {
        await api.post('/api/orchestrations/add-json', { json: jsonContent });
      }
      onAdded();
      onClose();
    } catch (err) {
      setError((err as Error).message);
    } finally {
      setLoading(false);
    }
  };

  /** Resolve steps from the preview orchestration handling PascalCase/camelCase. */
  const getPreviewSteps = (): PreviewStep[] => previewOrch?.steps || previewOrch?.Steps || [];

  /** Collect unique MCP names from the preview orchestration and its steps. */
  const getUniqueMcpNames = (): string[] => {
    if (!previewOrch) return [];
    const mcps = previewOrch.mcps || previewOrch.Mcps || [];
    const stepMcps = getPreviewSteps().flatMap(s => {
      const stepMcpList = s.mcps || s.Mcps || s.mcp || s.Mcp || [];
      const list = Array.isArray(stepMcpList) ? stepMcpList : [stepMcpList];
      return list.map(item => typeof item === 'string' ? item : (item as PreviewMcp).name || (item as PreviewMcp).Name || '');
    });
    const topLevelMcpNames = mcps.map(m => (typeof m === 'string' ? m : (m as PreviewMcp).name || (m as PreviewMcp).Name || '')).filter(Boolean);
    const allNames = [...topLevelMcpNames, ...stepMcps].filter(Boolean);
    return [...new Set(allNames)];
  };

  /** Get steps that have loop configurations. */
  const getLoopSteps = (): PreviewStep[] =>
    getPreviewSteps().filter(s => s.loopConfig || s.LoopConfig || s.loop || s.Loop);

  /** Collect unique parameter names from all steps. */
  const getUniqueParams = (): string[] => {
    const steps = getPreviewSteps();
    return [...new Set(steps.flatMap(s => s.parameters || s.Parameters || []))];
  };

  return (
    <div className={`modal-overlay ${open ? 'visible' : ''}`} ref={trapRef} onClick={(e) => e.target === e.currentTarget && onClose()}>
      <div className="modal" role="dialog" aria-modal="true" aria-label="Add Orchestration" style={{ maxWidth: previewFile ? '1000px' : '600px', transition: 'max-width 0.2s ease' }}>
        <div className="modal-header">
          <div className="modal-title">Add Orchestrations</div>
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
                      onKeyDown={(e: React.KeyboardEvent<HTMLInputElement>) => e.key === 'Enter' && browseFolder()}
                      style={{ flex: 1 }}
                    />
                    <button className="btn" onClick={openFolderDialog} disabled={loading} title="Open folder dialog">
                      <Icons.Folder />
                    </button>
                    <button className="btn btn-primary" onClick={browseFolder} disabled={loading || !path}>
                      {loading ? <div className="spinner"></div> : 'Scan'}
                    </button>
                  </div>
                </div>

                {files.length > 0 && (
                  <div className="form-group">
                    <label className="form-label">Select Orchestration Files ({selectedFiles.length} selected)</label>
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
              </div>

              {/* Right side: preview pane */}
              {previewFile && (
                <div style={{ flex: 1, minWidth: 0, borderLeft: '1px solid var(--border)', paddingLeft: '16px' }}>
                  <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', marginBottom: '8px' }}>
                    <div style={{ fontWeight: 600, fontSize: '14px' }}>{previewFile.name}</div>
                    <button className="btn-icon" onClick={() => { setPreviewFile(null); setPreviewOrch(null); }} title="Close preview">
                      <Icons.X />
                    </button>
                  </div>
                  {previewFile.description && (
                    <div style={{ fontSize: '12px', color: 'var(--text-muted)', marginBottom: '12px' }}>{previewFile.description}</div>
                  )}
                  <div className="tabs" style={{ marginBottom: '8px' }}>
                    <div className={`tab ${previewTab === 'details' ? 'active' : ''}`} onClick={() => setPreviewTab('details')} style={{ padding: '6px 12px', fontSize: '12px' }}>Details</div>
                    <div className={`tab ${previewTab === 'graph' ? 'active' : ''}`} onClick={() => setPreviewTab('graph')} style={{ padding: '6px 12px', fontSize: '12px' }}>Graph</div>
                    <div className={`tab ${previewTab === 'json' ? 'active' : ''}`} onClick={() => setPreviewTab('json')} style={{ padding: '6px 12px', fontSize: '12px' }}>JSON</div>
                  </div>
                  {previewTab === 'details' ? (
                    <div style={{
                      background: 'var(--bg)',
                      borderRadius: '8px',
                      padding: '12px',
                      maxHeight: '300px',
                      overflow: 'auto',
                      fontSize: '12px'
                    }}>
                      {previewOrch ? (
                        <>
                          {/* Basic Info */}
                          <div style={{ marginBottom: '12px' }}>
                            <div style={{ color: 'var(--text-muted)', fontSize: '10px', textTransform: 'uppercase', marginBottom: '4px' }}>Basic Info</div>
                            <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: '4px 12px' }}>
                              <div><span style={{ color: 'var(--text-dim)' }}>Steps:</span> {getPreviewSteps().length}</div>
                              <div><span style={{ color: 'var(--text-dim)' }}>Version:</span> {previewOrch.version || previewOrch.Version || '-'}</div>
                            </div>
                          </div>

                          {/* Trigger */}
                          {(previewOrch.trigger || previewOrch.Trigger) && (() => {
                            const trigger = previewOrch.trigger || previewOrch.Trigger;
                            if (!trigger) return null;
                            const triggerType = trigger.type || trigger.Type || 'Manual';
                            return (
                              <div style={{ marginBottom: '12px' }}>
                                <div style={{ color: 'var(--text-muted)', fontSize: '10px', textTransform: 'uppercase', marginBottom: '4px' }}>Trigger</div>
                                <div style={{ background: 'var(--surface)', padding: '8px', borderRadius: '4px' }}>
                                  <div><span style={{ color: 'var(--text-dim)' }}>Type:</span> {triggerType}</div>
                                  {trigger.schedule && <div><span style={{ color: 'var(--text-dim)' }}>Schedule:</span> {trigger.schedule}</div>}
                                  {trigger.Schedule && <div><span style={{ color: 'var(--text-dim)' }}>Schedule:</span> {trigger.Schedule}</div>}
                                  {trigger.inputHandlerPrompt && <div style={{ marginTop: '4px', color: 'var(--primary)' }}>Has Input Handler</div>}
                                </div>
                              </div>
                            );
                          })()}

                          {/* MCPs */}
                          {(() => {
                            const uniqueMcps = getUniqueMcpNames();
                            if (uniqueMcps.length > 0) {
                              return (
                                <div style={{ marginBottom: '12px' }}>
                                  <div style={{ color: 'var(--text-muted)', fontSize: '10px', textTransform: 'uppercase', marginBottom: '4px' }}>MCPs ({uniqueMcps.length})</div>
                                  <div style={{ display: 'flex', flexWrap: 'wrap', gap: '4px' }}>
                                    {uniqueMcps.map((mcp, i) => (
                                      <span key={i} style={{ background: 'var(--surface)', padding: '2px 6px', borderRadius: '4px', fontSize: '11px' }}>{mcp}</span>
                                    ))}
                                  </div>
                                </div>
                              );
                            }
                            return null;
                          })()}

                          {/* Steps with Loops */}
                          {(() => {
                            const loopSteps = getLoopSteps();
                            if (loopSteps.length > 0) {
                              return (
                                <div style={{ marginBottom: '12px' }}>
                                  <div style={{ color: 'var(--text-muted)', fontSize: '10px', textTransform: 'uppercase', marginBottom: '4px' }}>Loops ({loopSteps.length})</div>
                                  {loopSteps.map((step, i) => {
                                    const loop = step.loopConfig || step.LoopConfig || step.loop || step.Loop;
                                    const target = loop?.target || loop?.Target;
                                    const maxIter = loop?.maxIterations || loop?.MaxIterations;
                                    return (
                                      <div key={i} style={{ background: 'var(--surface)', padding: '6px', borderRadius: '4px', marginBottom: '4px' }}>
                                        <div><span style={{ color: 'var(--primary)' }}>{step.name || step.Name}</span> loops to <span style={{ color: 'var(--success)' }}>{target}</span></div>
                                        {maxIter && <div style={{ fontSize: '10px', color: 'var(--text-dim)' }}>Max iterations: {maxIter}</div>}
                                      </div>
                                    );
                                  })}
                                </div>
                              );
                            }
                            return null;
                          })()}

                          {/* Steps List */}
                          <div>
                            <div style={{ color: 'var(--text-muted)', fontSize: '10px', textTransform: 'uppercase', marginBottom: '4px' }}>Steps</div>
                            <div style={{ display: 'flex', flexDirection: 'column', gap: '4px' }}>
                              {getPreviewSteps().map((step, i) => {
                                const stepName = step.name || step.Name || '';
                                const model = step.model || step.Model;
                                const hasLoop = step.loopConfig || step.LoopConfig || step.loop || step.Loop;
                                const hasInputHandler = step.inputHandlerPrompt || step.InputHandlerPrompt;
                                const hasOutputHandler = step.outputHandlerPrompt || step.OutputHandlerPrompt;

                                return (
                                  <div key={i} style={{ background: 'var(--surface)', padding: '4px 8px', borderRadius: '4px', display: 'flex', alignItems: 'center', gap: '8px' }}>
                                    <span style={{ color: 'var(--text-dim)', fontSize: '10px', width: '16px' }}>{i + 1}</span>
                                    <span style={{ flex: 1 }}>{stepName}</span>
                                    {model && <span style={{ fontSize: '10px', color: 'var(--text-dim)' }}>{model.split('/').pop()}</span>}
                                    {hasLoop && <span style={{ fontSize: '10px', color: 'var(--primary)' }}>&#x21bb;</span>}
                                    {hasInputHandler && <span style={{ fontSize: '10px', color: 'var(--success)' }}>IN</span>}
                                    {hasOutputHandler && <span style={{ fontSize: '10px', color: 'var(--info)' }}>OUT</span>}
                                  </div>
                                );
                              })}
                            </div>
                          </div>

                          {/* Parameters */}
                          {(() => {
                            const params = getUniqueParams();
                            if (params.length > 0) {
                              return (
                                <div style={{ marginTop: '12px' }}>
                                  <div style={{ color: 'var(--text-muted)', fontSize: '10px', textTransform: 'uppercase', marginBottom: '4px' }}>Parameters ({params.length})</div>
                                  <div style={{ display: 'flex', flexWrap: 'wrap', gap: '4px' }}>
                                    {params.map((p, i) => (
                                      <span key={i} style={{ background: 'var(--surface)', padding: '2px 6px', borderRadius: '4px', fontSize: '11px', color: 'var(--warning)' }}>{p}</span>
                                    ))}
                                  </div>
                                </div>
                              );
                            }
                            return null;
                          })()}
                        </>
                      ) : (
                        <div style={{ color: 'var(--text-muted)', textAlign: 'center', padding: '20px' }}>Loading details...</div>
                      )}
                    </div>
                  ) : previewTab === 'graph' ? (
                    <div
                      ref={previewDagRef}
                      style={{
                        background: 'var(--bg)',
                        borderRadius: '8px',
                        padding: '12px',
                        minHeight: '200px',
                        maxHeight: '300px',
                        overflow: 'auto'
                      }}
                    ></div>
                  ) : (
                    <pre style={{
                      background: 'var(--bg)',
                      borderRadius: '8px',
                      padding: '12px',
                      fontSize: '11px',
                      maxHeight: '300px',
                      overflow: 'auto',
                      margin: 0
                    }}>
                      {previewJson}
                    </pre>
                  )}
                </div>
              )}
            </div>
          )}

          {activeTab === 'paste' && (
            <div className="form-group">
              <label className="form-label">Orchestration JSON</label>
              <textarea
                className="form-textarea"
                placeholder="Paste your orchestration JSON here..."
                value={jsonContent}
                onChange={(e: React.ChangeEvent<HTMLTextAreaElement>) => setJsonContent(e.target.value)}
                style={{ minHeight: '300px' }}
              />
            </div>
          )}
        </div>
        <div className="modal-footer">
          <button className="btn" onClick={onClose}>Cancel</button>
          <button
            className="btn btn-primary"
            onClick={addOrchestrations}
            disabled={loading || (activeTab === 'browse' ? selectedFiles.length === 0 : !jsonContent.trim())}
          >
            {loading ? <div className="spinner"></div> : 'Add Orchestrations'}
          </button>
        </div>
      </div>
    </div>
  );
}

export default AddModal;
