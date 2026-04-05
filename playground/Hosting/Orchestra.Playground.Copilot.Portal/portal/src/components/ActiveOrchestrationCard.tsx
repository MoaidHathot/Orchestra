import React from 'react';
import { Icons, getTriggerIcon } from '../icons';
import { formatTimeAgo, formatTimeUntil, getMatchingProfiles } from '../utils';
import type { Orchestration, Profile, InputDefinition, RunContext } from '../types';

/** Combined execution shape used by both running and pending cards. */
export interface CardExecution {
  executionId?: string;
  orchestrationId: string;
  orchestrationName: string;
  status?: string;
  startedAt?: string;
  triggeredBy?: string;
  parameters?: Record<string, unknown>;
  webhookUrl?: string;
  stepCount?: number;
  totalSteps?: number;
  completedSteps?: number;
  currentStep?: string;
  nextFireTime?: string;
  lastFireTime?: string;
  runCount?: number;
  /** Run context from SSE stream - available for running orchestrations that have emitted it */
  runContext?: RunContext;
}

interface Props {
  execution: CardExecution;
  type: 'running' | 'pending' | 'manual' | 'disabled';
  onView: (execution: CardExecution, orch: Orchestration | undefined) => void;
  onCancel?: (executionId: string) => void;
  onRun?: (orch: Orchestration) => void;
  orchestrations?: Orchestration[];
  profiles?: Profile[];
}

export default function ActiveOrchestrationCard({
  execution,
  type,
  onView,
  onCancel,
  onRun,
  orchestrations,
  profiles,
}: Props): React.JSX.Element {
  const isRunning = type === 'running';
  const isManual = type === 'manual';
  const isDisabled = type === 'disabled';
  const isCancelling = execution.status === 'Cancelling';
  const orch = orchestrations?.find((o) => o.id === execution.orchestrationId);

  const getDuration = (): string | null => {
    if (!execution.startedAt) return null;
    const start = new Date(execution.startedAt);
    const now = new Date();
    const seconds = Math.floor((now.getTime() - start.getTime()) / 1000);
    if (seconds < 60) return `${seconds}s`;
    const minutes = Math.floor(seconds / 60);
    if (minutes < 60) return `${minutes}m ${seconds % 60}s`;
    const hours = Math.floor(minutes / 60);
    return `${hours}h ${minutes % 60}m`;
  };

  const getStatusColor = (): string => {
    if (isCancelling) return 'var(--warning)';
    if (isRunning) return 'var(--primary)';
    if (isManual) return 'var(--text-dim)';
    if (isDisabled) return 'var(--text-dim)';
    return 'var(--warning)';
  };

  const getStatusBadgeClass = (): string => {
    if (isCancelling) return 'cancelling';
    if (isRunning) return 'running';
    if (isManual) return 'manual';
    if (isDisabled) return 'disabled';
    return 'pending';
  };

  const getStatusLabel = (): React.JSX.Element => {
    if (isCancelling) {
      return (
        <>
          <span style={{ width: '12px', height: '12px', color: 'var(--warning)', display: 'inline-flex' }}>
            <Icons.Spinner />
          </span>
          <span style={{ color: 'var(--warning)' }}>Cancelling</span>
        </>
      );
    }
    if (isRunning) {
      return (
        <>
          <span style={{ width: '12px', height: '12px', display: 'inline-flex' }}>
            <Icons.Spinner />
          </span>
          Running
        </>
      );
    }
    if (isManual) {
      return (
        <>
          <span style={{ width: '12px', height: '12px', display: 'inline-flex' }}>
            <Icons.Play />
          </span>
          Manual
        </>
      );
    }
    if (isDisabled) {
      return (
        <>
          <span style={{ width: '12px', height: '12px', display: 'inline-flex', opacity: 0.5 }}>
            <Icons.Ban />
          </span>
          <span style={{ opacity: 0.5 }}>Disabled</span>
        </>
      );
    }
    return (
      <>
        <span style={{ width: '12px', height: '12px', display: 'inline-flex' }}>
          <Icons.Clock />
        </span>
        Pending
      </>
    );
  };

  return (
    <div
      className={`orch-card ${isDisabled ? 'orch-card-disabled' : ''}`}
      style={{
        borderLeft: `4px solid ${getStatusColor()}`,
        cursor: 'pointer',
        opacity: isDisabled ? 0.6 : 1,
      }}
      onClick={() => onView(execution, orch)}
    >
      <div className="card-header">
        <div className="card-title-area">
          <div style={{ display: 'flex', alignItems: 'center', gap: '10px' }}>
            <div
              className={`step-status-badge ${getStatusBadgeClass()}`}
              style={{
                width: '10px',
                height: '10px',
                borderRadius: '50%',
                flexShrink: 0,
                background: isCancelling ? 'var(--warning)' : isDisabled ? 'var(--text-dim)' : isManual ? 'var(--text-muted)' : undefined,
              }}
            />
            <div className="card-title">{execution.orchestrationName}</div>
          </div>
          <div
            className="card-version"
            style={{ display: 'flex', alignItems: 'center', gap: '6px' }}
          >
            {getStatusLabel()}
          </div>
        </div>
      </div>

      <div className="card-body">
        <div className="card-meta-grid">
          {isRunning ? (
            <>
              <div className="card-meta-item">
                <div className="card-meta-label">Execution ID</div>
                <div
                  className="card-meta-value"
                  style={{ fontFamily: 'monospace', fontSize: '11px' }}
                >
                  {execution.executionId?.slice(0, 8)}...
                </div>
              </div>
              <div className="card-meta-item">
                <div className="card-meta-label">Duration</div>
                <div className="card-meta-value">{getDuration() || '-'}</div>
              </div>
              <div className="card-meta-item">
                <div className="card-meta-label">Started</div>
                <div className="card-meta-value">
                  {formatTimeAgo(execution.startedAt)}
                </div>
              </div>
              <div className="card-meta-item">
                <div className="card-meta-label">Trigger</div>
                <div
                  className="card-meta-value"
                  style={{ display: 'flex', alignItems: 'center', gap: '4px' }}
                >
                  {getTriggerIcon(execution.triggeredBy)}
                  {execution.triggeredBy}
                </div>
              </div>

              {/* Progress bar for running orchestrations */}
              {execution.totalSteps != null && execution.totalSteps > 0 && (
                <div className="card-meta-item" style={{ gridColumn: '1 / -1' }}>
                  <div
                    className="card-meta-label"
                    style={{ display: 'flex', justifyContent: 'space-between' }}
                  >
                    <span>Progress</span>
                    <span>
                      {execution.completedSteps || 0}/{execution.totalSteps} steps
                    </span>
                  </div>
                  <div
                    style={{
                      height: '8px',
                      background: 'var(--bg-secondary)',
                      borderRadius: '4px',
                      marginTop: '4px',
                      overflow: 'hidden',
                      border: '1px solid var(--border)',
                      boxSizing: 'content-box',
                      position: 'relative',
                    }}
                  >
                    {(() => {
                      const completed = execution.completedSteps || 0;
                      const total = execution.totalSteps!;
                      const hasCurrentStep = !!execution.currentStep;
                      const progressPercent = hasCurrentStep
                        ? ((completed + 0.5) / total) * 100
                        : (completed / total) * 100;
                      const finalWidth = Math.max(progressPercent, hasCurrentStep ? 5 : 0);
                      return (
                        <div
                          style={{
                            position: 'absolute',
                            top: 0,
                            left: 0,
                            bottom: 0,
                            width: `${finalWidth}%`,
                            background: hasCurrentStep ? 'var(--warning)' : 'var(--success)',
                            borderRadius: '3px',
                            transition: 'width 0.3s ease',
                          }}
                        />
                      );
                    })()}
                  </div>
                  {execution.currentStep && (
                    <div style={{ fontSize: '8px', color: 'var(--warning)', marginTop: '2px' }}>
                      {execution.currentStep}
                    </div>
                  )}
                </div>
              )}
            </>
          ) : isManual || isDisabled ? (
            <>
              <div className="card-meta-item">
                <div className="card-meta-label">Type</div>
                <div className="card-meta-value">
                  {isManual ? 'Manual (no trigger)' : 'Trigger disabled'}
                </div>
              </div>
              <div className="card-meta-item">
                <div className="card-meta-label">Steps</div>
                <div className="card-meta-value">
                  {orch?.steps?.length || '-'}
                </div>
              </div>
              {orch?.description && (
                <div className="card-meta-item" style={{ gridColumn: '1 / -1' }}>
                  <div className="card-meta-label">Description</div>
                  <div className="card-meta-value" style={{ whiteSpace: 'pre-wrap' }}>
                    {orch.description.length > 120 ? orch.description.slice(0, 120) + '...' : orch.description}
                  </div>
                </div>
              )}
            </>
          ) : (
            <>
              <div className="card-meta-item">
                <div className="card-meta-label">Trigger</div>
                <div
                  className="card-meta-value"
                  style={{ display: 'flex', alignItems: 'center', gap: '4px' }}
                >
                  {getTriggerIcon(execution.triggeredBy)}
                  {execution.triggeredBy}
                </div>
              </div>
              <div className="card-meta-item">
                <div className="card-meta-label">Status</div>
                <div className="card-meta-value">{execution.status || 'Scheduled'}</div>
              </div>
              <div className="card-meta-item">
                <div className="card-meta-label">Next Fire</div>
                <div className="card-meta-value">
                  {execution.nextFireTime
                    ? formatTimeUntil(execution.nextFireTime)
                    : 'Unknown'}
                </div>
              </div>
              <div className="card-meta-item">
                <div className="card-meta-label">Last Fired</div>
                <div className="card-meta-value">
                  {execution.lastFireTime
                    ? formatTimeAgo(execution.lastFireTime)
                    : 'Never'}
                </div>
              </div>
              <div className="card-meta-item">
                <div className="card-meta-label">Run Count</div>
                <div className="card-meta-value">{execution.runCount ?? 0}</div>
              </div>
              <div className="card-meta-item">
                <div className="card-meta-label">Steps</div>
                <div className="card-meta-value">
                  {execution.stepCount || orch?.steps?.length || '-'}
                </div>
              </div>

              {/* Webhook URL for webhook triggers */}
              {execution.triggeredBy === 'webhook' && execution.webhookUrl && (
                <div className="card-meta-item" style={{ gridColumn: '1 / -1' }}>
                  <div className="card-meta-label">Webhook URL</div>
                  <div
                    className="card-meta-value"
                    style={{
                      fontFamily: 'monospace',
                      fontSize: '11px',
                      display: 'flex',
                      alignItems: 'center',
                      gap: '8px',
                    }}
                  >
                    <code
                      style={{
                        flex: 1,
                        overflow: 'hidden',
                        textOverflow: 'ellipsis',
                        whiteSpace: 'nowrap',
                        background: 'var(--bg)',
                        padding: '4px 8px',
                        borderRadius: '4px',
                      }}
                    >
                      {execution.webhookUrl}
                    </code>
                    <button
                      className="btn-icon"
                      onClick={(e: React.MouseEvent) => {
                        e.stopPropagation();
                        navigator.clipboard.writeText(
                          window.location.origin + execution.webhookUrl,
                        );
                      }}
                      title="Copy full URL"
                      style={{ flexShrink: 0 }}
                    >
                      <Icons.Copy />
                    </button>
                  </div>
                </div>
              )}
            </>
          )}
        </div>

        {/* MCPs list */}
        {orch?.mcps && orch.mcps.length > 0 && (
          <div style={{ marginBottom: '8px' }}>
            <div className="card-meta-label">MCPs</div>
            <div style={{ display: 'flex', flexWrap: 'wrap', gap: '4px', marginTop: '2px' }}>
              {orch.mcps.map((mcp) => {
                const mcpName = typeof mcp === 'string' ? mcp : mcp.name;
                return (
                  <span
                    key={mcpName}
                    style={{
                      display: 'inline-flex',
                      alignItems: 'center',
                      padding: '2px 6px',
                      fontSize: '10px',
                      background: 'rgba(139, 92, 246, 0.15)',
                      border: '1px solid rgba(139, 92, 246, 0.3)',
                      borderRadius: '4px',
                      color: '#a78bfa',
                    }}
                  >
                    {mcpName}
                  </span>
                );
              })}
            </div>
          </div>
        )}

        {/* Tags */}
        {orch?.tags && orch.tags.length > 0 && (
          <div className="orch-tags" style={{ marginBottom: '4px' }}>
            {orch.tags.map(tag => (
              <span key={tag} className={`tag-chip ${tag === '*' ? 'tag-wildcard' : ''}`}>
                <Icons.Tag />{tag}
              </span>
            ))}
          </div>
        )}

        {/* Profiles */}
        {(() => {
          const matchedProfiles = profiles && orch
            ? getMatchingProfiles(profiles, orch.id, orch.tags)
            : [];
          return matchedProfiles.length > 0 ? (
            <div className="orch-profiles">
              {matchedProfiles.map(p => (
                <span key={p.id} className={`profile-badge ${p.isActive ? 'active' : ''}`}>
                  <Icons.Shield />{p.name}
                </span>
              ))}
            </div>
          ) : null;
        })()}

        {/* Parameters preview for running */}
        {isRunning &&
          execution.parameters &&
          Object.keys(execution.parameters).length > 0 && (
            <div
              style={{
                marginTop: '8px',
                padding: '6px 8px',
                background: 'var(--bg)',
                borderRadius: '6px',
              }}
            >
              <div className="card-meta-label" style={{ marginBottom: '4px' }}>
                Parameters
              </div>
              <div
                style={{
                  fontSize: '11px',
                  fontFamily: 'monospace',
                  color: 'var(--text-muted)',
                }}
              >
                {Object.entries(execution.parameters)
                  .slice(0, 3)
                  .map(([k, v]) => (
                    <div
                      key={k}
                      style={{
                        overflow: 'hidden',
                        textOverflow: 'ellipsis',
                        whiteSpace: 'nowrap',
                      }}
                    >
                      <span style={{ color: '#ff7b72' }}>{k}:</span>{' '}
                      {typeof v === 'string'
                        ? v.slice(0, 50)
                        : JSON.stringify(v).slice(0, 50)}
                    </div>
                  ))}
                {Object.keys(execution.parameters).length > 3 && (
                  <div className="text-muted">
                    +{Object.keys(execution.parameters).length - 3} more...
                  </div>
                )}
              </div>
            </div>
          )}

        {/* Resolved context for running orchestrations (from SSE run-context event) */}
        {isRunning && execution.runContext && (
          <OrchestrationContextSection
            runContext={execution.runContext}
            orch={orch}
          />
        )}

        {/* Orchestration context for non-running cards (definition view) */}
        {!isRunning && orch && (
          <OrchestrationContextSection
            orch={orch}
          />
        )}

        <div className="card-actions" style={{ marginTop: '6px' }}>
          <button
            className="btn btn-sm"
            onClick={(e: React.MouseEvent) => {
              e.stopPropagation();
              onView(execution, orch);
            }}
          >
            <Icons.Eye /> View
          </button>
          {!isRunning && onRun && orch && (
            <button
              className="btn btn-success btn-sm"
              onClick={(e: React.MouseEvent) => {
                e.stopPropagation();
                onRun(orch);
              }}
            >
              <Icons.Play /> Run
            </button>
          )}
          {isRunning && onCancel && (
            <button
              className="btn btn-danger btn-sm"
              onClick={(e: React.MouseEvent) => {
                e.stopPropagation();
                if (execution.executionId) {
                  onCancel(execution.executionId);
                }
              }}
            >
              <Icons.X /> Cancel
            </button>
          )}
        </div>
      </div>
    </div>
  );
}

/* ── Key-value row for context sections ─────────────────────────────────── */

function ContextRow({ label, value, color }: { label: string; value: string; color?: string }) {
  return (
    <div
      style={{
        overflow: 'hidden',
        textOverflow: 'ellipsis',
        whiteSpace: 'nowrap',
      }}
    >
      <span style={{ color: color || '#ff7b72' }}>{label}:</span>{' '}
      <span style={{ color: 'var(--text-muted)' }}>{value.length > 60 ? value.slice(0, 60) + '...' : value}</span>
    </div>
  );
}

/* ── Context section showing variables, inputs, env vars, models ──────── */

interface OrchestrationContextSectionProps {
  /** Orchestration definition (for definition-time data) */
  orch?: Orchestration;
  /** Run context from SSE stream (for runtime-resolved data) */
  runContext?: RunContext;
}

function OrchestrationContextSection({ orch, runContext }: OrchestrationContextSectionProps) {
  const hasRunContext = !!runContext;

  // Collect data sections
  const sections: { title: string; entries: { key: string; value: string; color?: string }[] }[] = [];

  // Variables
  if (hasRunContext) {
    // Show resolved variables (prefer resolvedVariables, fall back to raw variables)
    const resolved = runContext.resolvedVariables;
    const raw = runContext.variables;
    const vars = resolved && Object.keys(resolved).length > 0
      ? { ...raw, ...resolved }
      : raw;
    if (vars && Object.keys(vars).length > 0) {
      sections.push({
        title: 'Variables',
        entries: Object.entries(vars).map(([k, v]) => ({
          key: k,
          value: v ?? '',
          color: resolved?.[k] !== undefined ? '#7ee787' : '#ff7b72',
        })),
      });
    }
  } else if (orch?.variables && Object.keys(orch.variables).length > 0) {
    sections.push({
      title: 'Variables',
      entries: Object.entries(orch.variables).map(([k, v]) => ({
        key: k,
        value: v,
      })),
    });
  }

  // Inputs / Parameters
  if (hasRunContext && runContext.parameters && Object.keys(runContext.parameters).length > 0) {
    // Already shown in the parameters section above for running cards;
    // skip to avoid duplication
  } else if (!hasRunContext && orch?.inputs && Object.keys(orch.inputs).length > 0) {
    const inputEntries = Object.entries(orch.inputs).map(([k, def]: [string, InputDefinition]) => {
      const parts: string[] = [];
      parts.push(def.type || 'string');
      if (def.required === false) parts.push('optional');
      if (def.default) parts.push(`default: ${def.default}`);
      if (def.enum && def.enum.length > 0) parts.push(`[${def.enum.join(', ')}]`);
      if (def.description) parts.push(`- ${def.description}`);
      return { key: k, value: parts.join(', '), color: '#79c0ff' as string | undefined };
    });
    sections.push({ title: 'Inputs', entries: inputEntries });
  } else if (!hasRunContext && orch?.parameters && orch.parameters.length > 0 && !orch.inputs) {
    // Legacy parameter names without input definitions
    sections.push({
      title: 'Parameters',
      entries: orch.parameters.map(p => ({ key: p, value: '{{param.' + p + '}}', color: '#79c0ff' })),
    });
  }

  // Environment variables
  if (hasRunContext && runContext.accessedEnvironmentVariables) {
    const envEntries = Object.entries(runContext.accessedEnvironmentVariables)
      .map(([k, v]) => ({
        key: k,
        value: v !== null ? v : '(not set)',
        color: v !== null ? '#7ee787' : '#f85149',
      }));
    if (envEntries.length > 0) {
      sections.push({ title: 'Environment', entries: envEntries });
    }
  } else if (!hasRunContext && orch?.referencedEnvVars && orch.referencedEnvVars.length > 0) {
    sections.push({
      title: 'Environment',
      entries: orch.referencedEnvVars.map(v => ({
        key: v,
        value: '{{env.' + v + '}}',
        color: '#d2a8ff',
      })),
    });
  }

  // Models (definition cards only, running cards show model in step details)
  if (!hasRunContext && orch?.models && orch.models.length > 0) {
    sections.push({
      title: 'Models',
      entries: orch.models.map(m => ({ key: m, value: '', color: '#ffa657' })),
    });
  }

  if (sections.length === 0) return null;

  return (
    <div
      style={{
        marginTop: '8px',
        padding: '6px 8px',
        background: 'var(--bg)',
        borderRadius: '6px',
        fontSize: '11px',
        fontFamily: 'monospace',
      }}
    >
      {sections.map((section, si) => (
        <div key={section.title} style={{ marginTop: si > 0 ? '6px' : 0 }}>
          <div className="card-meta-label" style={{ marginBottom: '2px', fontSize: '10px' }}>
            {section.title}
            {hasRunContext && section.title === 'Variables' && (
              <span style={{ marginLeft: '6px', color: 'var(--text-dim)', fontWeight: 'normal' }}>
                (resolved)
              </span>
            )}
            {hasRunContext && section.title === 'Environment' && (
              <span style={{ marginLeft: '6px', color: 'var(--text-dim)', fontWeight: 'normal' }}>
                (runtime)
              </span>
            )}
          </div>
          {section.title === 'Models' ? (
            <div style={{ display: 'flex', flexWrap: 'wrap', gap: '4px' }}>
              {section.entries.map(e => (
                <span
                  key={e.key}
                  style={{
                    display: 'inline-flex',
                    padding: '1px 6px',
                    fontSize: '10px',
                    background: 'rgba(255, 166, 87, 0.12)',
                    border: '1px solid rgba(255, 166, 87, 0.3)',
                    borderRadius: '4px',
                    color: '#ffa657',
                  }}
                >
                  {e.key}
                </span>
              ))}
            </div>
          ) : (
            section.entries.slice(0, 5).map(e => (
              <ContextRow key={e.key} label={e.key} value={e.value} color={e.color} />
            ))
          )}
          {section.entries.length > 5 && (
            <div style={{ color: 'var(--text-dim)', fontSize: '10px' }}>
              +{section.entries.length - 5} more...
            </div>
          )}
        </div>
      ))}
    </div>
  );
}
