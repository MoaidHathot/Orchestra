import React, { useState, useEffect, useCallback, useRef } from 'react';
import type { Orchestration, Step, McpConfig, TagCount } from '../../types';
import { api } from '../../api';
import { Icons } from '../../icons';
import StepDetailsPanel from '../StepDetailsPanel';
import { renderMermaidDag } from '../../mermaid';
import { useFocusTrap } from '../../hooks/useFocusTrap';

/** Loose step shape – the server JSON may include fields not in the strict Step type. */
type LooseStep = Step & Record<string, unknown>;

/** Extended trigger shape for webhook details returned by the API. */
interface WebhookTrigger {
  type: string;
  webhookUrl?: string;
  hasSecret?: boolean;
  expectedParameters?: string[];
  hasInputHandler?: boolean;
  inputHandlerPrompt?: string;
  [key: string]: unknown;
}

interface Props {
  open: boolean;
  orchestration: Orchestration | null;
  onClose: () => void;
  onRun?: () => void;
  onTagsChanged?: () => void;
}

function ViewerModal({ open, orchestration, onClose, onRun, onTagsChanged }: Props): React.JSX.Element | null {
  const trapRef = useFocusTrap<HTMLDivElement>(open, onClose);
  const dagRef = useRef<HTMLDivElement>(null);
  const [activeTab, setActiveTab] = useState<'dag' | 'details' | 'json'>('dag');
  const [fullOrchestration, setFullOrchestration] = useState<Orchestration | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [selectedStep, setSelectedStep] = useState<LooseStep | null>(null);

  // ── Tag editing state ──
  const [tagInput, setTagInput] = useState('');
  const [tagSaving, setTagSaving] = useState(false);
  const [knownTags, setKnownTags] = useState<TagCount[]>([]);
  const [tagDetails, setTagDetails] = useState<{ effectiveTags: string[]; authorTags: string[]; hostTags: string[] } | null>(null);

  // Fetch full orchestration details when modal opens
  useEffect(() => {
    if (open && orchestration?.id) {
      setLoading(true);
      setFullOrchestration(null);
      setSelectedStep(null);
      setError(null);
      setTagDetails(null);
      setTagInput('');

      Promise.all([
        api.get<Orchestration>(`/api/orchestrations/${orchestration.id}`),
        api.get<{ orchestrationId: string; effectiveTags: string[]; authorTags: string[]; hostTags: string[] }>(`/api/orchestrations/${orchestration.id}/tags`),
        api.get<{ count: number; tags: TagCount[] }>('/api/tags'),
      ])
        .then(([data, tagData, tagsResp]) => {
          setFullOrchestration(data);
          setTagDetails(tagData);
          setKnownTags(tagsResp.tags || []);
        })
        .catch((err: Error) => {
          console.error('Failed to load orchestration details:', err);
          setError(err.message || 'Failed to load orchestration details');
        })
        .finally(() => setLoading(false));
    }
  }, [open, orchestration?.id]);

  // ── Tag editing actions ──

  const addTag = async () => {
    const tag = tagInput.trim().toLowerCase();
    if (!tag || !orchestration?.id) return;
    setTagSaving(true);
    try {
      const result = await api.post<{ orchestrationId: string; effectiveTags: string[] }>(
        `/api/orchestrations/${orchestration.id}/tags`,
        { tags: [tag] }
      );
      setTagDetails(prev => prev ? { ...prev, effectiveTags: result.effectiveTags, hostTags: [...prev.hostTags, tag] } : prev);
      setTagInput('');
      // Reload known tags
      const tagsResp = await api.get<{ count: number; tags: TagCount[] }>('/api/tags');
      setKnownTags(tagsResp.tags || []);
      onTagsChanged?.();
    } catch (err) {
      console.error('Failed to add tag:', err);
    } finally {
      setTagSaving(false);
    }
  };

  const removeTag = async (tag: string) => {
    if (!orchestration?.id) return;
    setTagSaving(true);
    try {
      const result = await api.delete<{ orchestrationId: string; effectiveTags: string[] }>(
        `/api/orchestrations/${orchestration.id}/tags/${encodeURIComponent(tag)}`
      );
      setTagDetails(prev => prev ? { ...prev, effectiveTags: result.effectiveTags, hostTags: prev.hostTags.filter(t => t !== tag) } : prev);
      // Reload known tags
      const tagsResp = await api.get<{ count: number; tags: TagCount[] }>('/api/tags');
      setKnownTags(tagsResp.tags || []);
      onTagsChanged?.();
    } catch (err) {
      console.error('Failed to remove tag:', err);
    } finally {
      setTagSaving(false);
    }
  };

  // Handle step click from DAG
  const handleStepClick = useCallback((stepName: string) => {
    const step = fullOrchestration?.steps?.find(s =>
      (typeof s === 'string' ? s : s.name) === stepName
    );
    setSelectedStep((step as LooseStep) || { name: stepName } as LooseStep);
  }, [fullOrchestration]);

  // Render Mermaid DAG when data is ready
  useEffect(() => {
    if (open && fullOrchestration && dagRef.current && activeTab === 'dag') {
      // Clear and create a fresh container for Mermaid to avoid React DOM conflicts
      const container = dagRef.current;
      container.innerHTML = '<div class="mermaid-inner"></div>';
      const innerContainer = container.querySelector('.mermaid-inner') as HTMLElement;
      renderMermaidDag(fullOrchestration, innerContainer, handleStepClick);
    }
  }, [open, fullOrchestration, activeTab, handleStepClick]);

  if (!orchestration) return null;

  const displayOrch = fullOrchestration || orchestration;
  const steps = displayOrch.steps ?? [];
  const parameters = displayOrch.parameters;
  const mcps: McpConfig[] = displayOrch.mcps ?? [];
  const trigger = displayOrch.trigger as WebhookTrigger | undefined;
  const triggerType = (displayOrch as unknown as Record<string, unknown>).triggerType as string | undefined;

  /** Build the cURL body payload from expected parameters. */
  const buildCurlBody = (): string => {
    const expectedParams = trigger?.expectedParameters ?? parameters ?? [];
    const payload = expectedParams.reduce<Record<string, string>>((acc, p) => {
      acc[p] = `<${p} value>`;
      return acc;
    }, {});
    return JSON.stringify(payload, null, 2);
  };

  return (
    <div ref={trapRef} className={`modal-overlay ${open ? 'visible' : ''}`} onClick={(e) => e.target === e.currentTarget && onClose()}>
      <div className="modal modal-lg" role="dialog" aria-modal="true" aria-label={`${displayOrch.name} orchestration viewer`}>
        <div className="modal-header">
          <div>
            <div className="modal-title">{displayOrch.name}</div>
            <div className="text-muted" style={{ fontSize: '13px' }}>v{displayOrch.version || '1.0.0'}</div>
          </div>
          <button className="modal-close" onClick={onClose} aria-label="Close"><Icons.X /></button>
        </div>
        <div className="tabs">
          <div className={`tab ${activeTab === 'dag' ? 'active' : ''}`} onClick={() => setActiveTab('dag')}>DAG Visualization</div>
          <div className={`tab ${activeTab === 'details' ? 'active' : ''}`} onClick={() => setActiveTab('details')}>Details</div>
          <div className={`tab ${activeTab === 'json' ? 'active' : ''}`} onClick={() => setActiveTab('json')}>JSON</div>
        </div>
        <div className="modal-body">
          {loading ? (
            <div className="empty-state">
              <div className="spinner"></div>
            </div>
          ) : error ? (
            <div className="empty-state">
              <div style={{ color: 'var(--error)', marginBottom: '12px' }}>
                <Icons.X />
              </div>
              <div className="empty-text" style={{ color: 'var(--error)' }}>Failed to load orchestration</div>
              <div className="text-muted" style={{ marginTop: '8px', fontSize: '13px' }}>{error}</div>
              <button
                className="btn"
                style={{ marginTop: '16px' }}
                onClick={() => {
                  setError(null);
                  setLoading(true);
                  Promise.all([
                    api.get<Orchestration>(`/api/orchestrations/${orchestration.id}`),
                    api.get<{ orchestrationId: string; effectiveTags: string[]; authorTags: string[]; hostTags: string[] }>(`/api/orchestrations/${orchestration.id}/tags`),
                  ])
                    .then(([data, tagData]) => { setFullOrchestration(data); setTagDetails(tagData); })
                    .catch((err: Error) => setError(err.message || 'Failed to load'))
                    .finally(() => setLoading(false));
                }}
              >
                Retry
              </button>
            </div>
          ) : (
            <>
              {activeTab === 'dag' && (
                <div style={{ display: 'flex', flexDirection: 'column', height: '100%' }}>
                  {/* Legend */}
                  <div style={{
                    display: 'flex',
                    gap: '16px',
                    padding: '8px 12px',
                    background: 'var(--surface)',
                    borderRadius: '6px',
                    marginBottom: '12px',
                    fontSize: '11px',
                    flexWrap: 'wrap'
                  }}>
                    <span className="text-muted">Legend:</span>
                    <span><span style={{ color: '#3fb950' }}>⇢</span> Input Handler</span>
                    <span><span style={{ color: '#58a6ff' }}>⇠</span> Output Handler</span>
                    <span><span style={{ color: '#d29922' }}>⇄</span> Both Handlers</span>
                  </div>
                  <div style={{ display: 'flex', gap: '16px', flex: 1 }}>
                    <div style={{ flex: selectedStep ? '1 1 60%' : '1 1 100%', position: 'relative', display: 'flex', flexDirection: 'column' }}>
                      <div className="dag-container" ref={dagRef} style={{ flex: 1 }}>
                        {/* Content managed by Mermaid - do not add React children here */}
                      </div>
                      {!selectedStep && steps.length > 0 && (
                        <div className="dag-hint">Click a node to view step details</div>
                      )}
                    </div>
                    {selectedStep && (
                      <div className="step-details-panel" style={{
                        flex: '0 0 350px',
                        background: 'var(--surface)',
                        borderRadius: '8px',
                        padding: '16px',
                        overflowY: 'auto',
                        maxHeight: '500px',
                        border: '1px solid var(--border)'
                      }}>
                        <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: '12px' }}>
                          <h4 style={{ margin: 0, color: 'var(--text)' }}>Step Details</h4>
                          <button
                            className="modal-close"
                            onClick={() => setSelectedStep(null)}
                            style={{ position: 'static', padding: '4px' }}
                          >
                            <Icons.X />
                          </button>
                        </div>
                        <StepDetailsPanel step={selectedStep} />
                      </div>
                    )}
                  </div>
                </div>
              )}
              {activeTab === 'details' && (
                <div>
                  <div className="form-group">
                    <label className="form-label">Description</label>
                    <p>{displayOrch.description || 'No description'}</p>
                  </div>

                  {/* Tags (editable) */}
                  <div className="form-group">
                    <label className="form-label">Tags</label>

                    {/* Author-defined tags (read-only) */}
                    {tagDetails?.authorTags && tagDetails.authorTags.length > 0 && (
                      <div style={{ marginBottom: '8px' }}>
                        <span className="text-muted" style={{ fontSize: '11px', display: 'block', marginBottom: '4px' }}>Author-defined (from orchestration JSON):</span>
                        <div className="profile-tags">
                          {tagDetails.authorTags.map(tag => (
                            <span key={tag} className={`tag-chip ${tag === '*' ? 'tag-wildcard' : ''}`}>
                              <Icons.Tag />{tag}
                              <Icons.Lock />
                            </span>
                          ))}
                        </div>
                      </div>
                    )}

                    {/* Host-managed tags (editable) */}
                    <div style={{ marginBottom: '8px' }}>
                      <span className="text-muted" style={{ fontSize: '11px', display: 'block', marginBottom: '4px' }}>Host-managed tags (editable):</span>
                      <div className="profile-tags">
                        {tagDetails?.hostTags && tagDetails.hostTags.length > 0 ? (
                          tagDetails.hostTags.map(tag => (
                            <span key={tag} className="tag-chip">
                              <Icons.Tag />{tag}
                              <button
                                className="tag-chip-remove"
                                onClick={() => removeTag(tag)}
                                disabled={tagSaving}
                                aria-label={`Remove tag ${tag}`}
                              >
                                <Icons.X />
                              </button>
                            </span>
                          ))
                        ) : (
                          <span className="text-muted" style={{ fontSize: '12px' }}>No host-managed tags</span>
                        )}
                      </div>
                    </div>

                    {/* Add tag input */}
                    <div className="tag-editor-input-row">
                      <input
                        type="text"
                        className="tag-editor-input"
                        placeholder="Add a tag..."
                        value={tagInput}
                        onChange={(e) => setTagInput(e.target.value)}
                        onKeyDown={(e) => { if (e.key === 'Enter') { e.preventDefault(); addTag(); } }}
                        disabled={tagSaving}
                      />
                      <button
                        className="btn btn-sm"
                        onClick={addTag}
                        disabled={!tagInput.trim() || tagSaving}
                      >
                        {tagSaving ? '...' : 'Add'}
                      </button>
                    </div>

                    {/* Quick-add from known tags */}
                    {knownTags.length > 0 && (
                      <div className="tag-editor-suggestions">
                        <span className="text-muted" style={{ fontSize: '11px' }}>Known tags: </span>
                        {knownTags
                          .filter(t => !tagDetails?.effectiveTags?.includes(t.tag))
                          .map(t => (
                            <button
                              key={t.tag}
                              className="tag-chip tag-chip-small tag-chip-clickable"
                              onClick={() => { setTagInput(t.tag); }}
                              disabled={tagSaving}
                            >
                              <Icons.Tag />{t.tag}
                            </button>
                          ))}
                      </div>
                    )}
                  </div>

                  {/* Parameters */}
                  {parameters && parameters.length > 0 && (
                    <div className="form-group">
                      <label className="form-label">Required Parameters</label>
                      <div style={{ display: 'flex', flexWrap: 'wrap', gap: '8px' }}>
                        {parameters.map((paramName, i) => (
                          <span key={i} style={{
                            background: '#3fb9504d',
                            border: '1px solid #3fb950',
                            padding: '4px 12px',
                            borderRadius: '4px',
                            fontSize: '13px',
                            color: '#7ee787'
                          }}>
                            {paramName}
                          </span>
                        ))}
                      </div>
                    </div>
                  )}

                  {/* MCPs */}
                  {mcps.length > 0 && (
                    <div className="form-group">
                      <label className="form-label">MCPs ({mcps.length})</label>
                      <div style={{ display: 'flex', flexDirection: 'column', gap: '8px' }}>
                        {mcps.map((mcp, i) => (
                          <div key={i} style={{
                            padding: '8px 12px',
                            background: 'var(--surface)',
                            borderRadius: '6px',
                            display: 'flex',
                            justifyContent: 'space-between',
                            alignItems: 'center'
                          }}>
                            <div>
                              <span style={{ color: '#a371f7', fontWeight: 500 }}>{mcp.name}</span>
                              <span className="text-muted" style={{ marginLeft: '8px' }}>({mcp.type})</span>
                            </div>
                            {mcp.url && (
                              <span className="text-muted" style={{ fontSize: '12px' }}>{mcp.url}</span>
                            )}
                            {mcp.command && (
                              <span className="text-muted" style={{ fontSize: '12px' }}>{mcp.command}</span>
                            )}
                          </div>
                        ))}
                      </div>
                    </div>
                  )}

                  {/* Webhook Trigger Details */}
                  {trigger?.type === 'webhook' && (
                    <div className="form-group">
                      <label className="form-label">Webhook Trigger</label>
                      <div style={{
                        padding: '16px',
                        background: 'var(--surface)',
                        borderRadius: '8px',
                        border: '1px solid var(--border)'
                      }}>
                        {/* Webhook URL */}
                        <div style={{ marginBottom: '16px' }}>
                          <div className="form-label" style={{ fontSize: '12px', marginBottom: '6px' }}>Webhook URL</div>
                          <div style={{
                            display: 'flex',
                            alignItems: 'center',
                            gap: '8px',
                            padding: '10px 12px',
                            background: 'var(--bg)',
                            borderRadius: '6px',
                            fontFamily: 'monospace',
                            fontSize: '13px'
                          }}>
                            <code style={{ flex: 1, color: '#58a6ff', overflow: 'hidden', textOverflow: 'ellipsis' }}>
                              {trigger.webhookUrl || `/api/webhook/${displayOrch.id}`}
                            </code>
                            <button
                              className="btn btn-sm"
                              onClick={() => navigator.clipboard.writeText(
                                window.location.origin + (trigger.webhookUrl || `/api/webhook/${displayOrch.id}`)
                              )}
                              title="Copy full URL"
                            >
                              <Icons.Copy /> Copy
                            </button>
                          </div>
                        </div>

                        {/* Method and Content-Type */}
                        <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: '12px', marginBottom: '16px' }}>
                          <div>
                            <div className="form-label" style={{ fontSize: '12px', marginBottom: '4px' }}>Method</div>
                            <span style={{
                              background: '#238636',
                              color: 'white',
                              padding: '4px 10px',
                              borderRadius: '4px',
                              fontSize: '12px',
                              fontWeight: 600
                            }}>POST</span>
                          </div>
                          <div>
                            <div className="form-label" style={{ fontSize: '12px', marginBottom: '4px' }}>Content-Type</div>
                            <code style={{ fontSize: '12px', color: 'var(--text-muted)' }}>application/json</code>
                          </div>
                        </div>

                        {/* Secret requirement */}
                        {trigger.hasSecret && (
                          <div style={{ marginBottom: '16px', padding: '10px', background: '#da36334d', borderRadius: '6px', border: '1px solid #da3633' }}>
                            <div style={{ display: 'flex', alignItems: 'center', gap: '6px', color: '#ff7b72', fontSize: '13px' }}>
                              <Icons.Lock />
                              <span>Requires <code>X-Webhook-Secret</code> header</span>
                            </div>
                          </div>
                        )}

                        {/* Expected Parameters */}
                        {trigger.expectedParameters && trigger.expectedParameters.length > 0 && (
                          <div style={{ marginBottom: '16px' }}>
                            <div className="form-label" style={{ fontSize: '12px', marginBottom: '8px' }}>Expected Parameters</div>
                            <div style={{ display: 'flex', flexWrap: 'wrap', gap: '6px' }}>
                              {trigger.expectedParameters.map((param, i) => (
                                <span key={i} style={{
                                  background: '#3fb9504d',
                                  border: '1px solid #3fb950',
                                  padding: '3px 10px',
                                  borderRadius: '4px',
                                  fontSize: '12px',
                                  color: '#7ee787',
                                  fontFamily: 'monospace'
                                }}>
                                  {param}
                                </span>
                              ))}
                            </div>
                          </div>
                        )}

                        {/* Example cURL */}
                        <div>
                          <div className="form-label" style={{ fontSize: '12px', marginBottom: '8px' }}>Example Request</div>
                          <pre style={{
                            background: 'var(--bg)',
                            padding: '12px',
                            borderRadius: '6px',
                            fontSize: '11px',
                            overflow: 'auto',
                            margin: 0
                          }}>
{`curl -X POST ${window.location.origin}${trigger.webhookUrl || `/api/webhook/${displayOrch.id}`} \\
  -H "Content-Type: application/json"${trigger.hasSecret ? ` \\
  -H "X-Webhook-Secret: <your-secret>"` : ''} \\
  -d '${buildCurlBody().replace(/\n/g, '\n  ')}'`}
                          </pre>
                          <button
                            className="btn btn-sm"
                            style={{ marginTop: '8px' }}
                            onClick={() => {
                              const expectedParams = trigger.expectedParameters ?? Object.keys(parameters ?? {});
                              const payload = expectedParams.reduce<Record<string, string>>((acc, p) => {
                                acc[p] = `<${p} value>`;
                                return acc;
                              }, {});
                              const curlCmd = `curl -X POST ${window.location.origin}${trigger.webhookUrl || `/api/webhook/${displayOrch.id}`} -H "Content-Type: application/json"${trigger.hasSecret ? ` -H "X-Webhook-Secret: <your-secret>"` : ''} -d '${JSON.stringify(payload)}'`;
                              navigator.clipboard.writeText(curlCmd);
                            }}
                          >
                            <Icons.Copy /> Copy cURL
                          </button>
                        </div>

                        {/* Input Handler note */}
                        {trigger.hasInputHandler && (
                          <div style={{ marginTop: '16px', padding: '10px', background: '#1f6feb33', borderRadius: '6px', border: '1px solid #1f6feb' }}>
                            <div style={{ display: 'flex', alignItems: 'flex-start', gap: '8px', color: '#58a6ff', fontSize: '12px' }}>
                              <span style={{ flexShrink: 0, marginTop: '2px' }}><Icons.Sparkles /></span>
                              <span>This webhook has an <strong>input handler</strong> that uses an LLM to parse the raw payload. You can send any JSON structure and it will be intelligently extracted.</span>
                            </div>
                          </div>
                        )}
                      </div>
                    </div>
                  )}

                  <div className="form-group">
                    <label className="form-label">Steps ({steps.length})</label>
                    {steps.map((step, i) => {
                      const s = step as unknown as LooseStep;
                      return (
                        <div key={i} style={{ padding: '8px 12px', background: 'var(--surface)', borderRadius: '6px', marginBottom: '8px' }}>
                          <div style={{ display: 'flex', alignItems: 'center', gap: '8px' }}>
                            <strong>{typeof step === 'string' ? step : step.name}</strong>
                            {!!s.inputHandlerPrompt && (
                              <span style={{ color: '#3fb950', fontSize: '11px' }} title="Has Input Handler">⇢</span>
                            )}
                            {!!s.outputHandlerPrompt && (
                              <span style={{ color: '#58a6ff', fontSize: '11px' }} title="Has Output Handler">⇠</span>
                            )}
                          </div>
                          {step.dependsOn && step.dependsOn.length > 0 && (
                            <span className="text-muted" style={{ fontSize: '12px' }}>
                              depends on: {step.dependsOn.join(', ')}
                            </span>
                          )}
                          {step.parameters && step.parameters.length > 0 && (
                            <div style={{ marginTop: '4px' }}>
                              <span className="text-muted" style={{ fontSize: '11px' }}>params: </span>
                              {step.parameters.map((paramName, j) => (
                                <span key={j} style={{
                                  background: '#3fb9503d',
                                  padding: '1px 6px',
                                  borderRadius: '3px',
                                  fontSize: '11px',
                                  marginRight: '4px',
                                  color: '#7ee787'
                                }}>
                                  {paramName}
                                </span>
                              ))}
                            </div>
                          )}
                        </div>
                      );
                    })}
                  </div>
                </div>
              )}
              {activeTab === 'json' && (
                <pre style={{ background: 'var(--surface)', padding: '16px', borderRadius: '8px', overflow: 'auto', fontSize: '12px' }}>
                  {JSON.stringify(displayOrch, null, 2)}
                </pre>
              )}
            </>
          )}
        </div>
        <div className="modal-footer">
          {(!triggerType || triggerType === 'Manual') && onRun && (
            <button className="btn btn-success" onClick={onRun} style={{ marginRight: 'auto' }}>
              <Icons.Play /> Run Now
            </button>
          )}
          <button className="btn" onClick={onClose}>Close</button>
        </div>
      </div>
    </div>
  );
}

export default ViewerModal;
