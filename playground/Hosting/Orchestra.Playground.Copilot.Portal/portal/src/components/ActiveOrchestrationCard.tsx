import React from 'react';
import { Icons, getTriggerIcon } from '../icons';
import { formatTimeAgo, formatTimeUntil, getMatchingProfiles } from '../utils';
import type { Orchestration, Profile } from '../types';

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
}

interface Props {
  execution: CardExecution;
  type: 'running' | 'pending';
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
    return 'var(--warning)';
  };

  return (
    <div
      className="orch-card"
      style={{
        borderLeft: `4px solid ${getStatusColor()}`,
        cursor: 'pointer',
      }}
      onClick={() => onView(execution, orch)}
    >
      <div className="card-header">
        <div className="card-title-area">
          <div style={{ display: 'flex', alignItems: 'center', gap: '10px' }}>
            <div
              className={`step-status-badge ${isCancelling ? 'cancelling' : isRunning ? 'running' : 'pending'}`}
              style={{
                width: '10px',
                height: '10px',
                borderRadius: '50%',
                flexShrink: 0,
                background: isCancelling ? 'var(--warning)' : undefined,
              }}
            />
            <div className="card-title">{execution.orchestrationName}</div>
          </div>
          <div
            className="card-version"
            style={{ display: 'flex', alignItems: 'center', gap: '6px' }}
          >
            {isCancelling ? (
              <>
                <span style={{ width: '12px', height: '12px', color: 'var(--warning)', display: 'inline-flex' }}>
                  <Icons.Spinner />
                </span>
                <span style={{ color: 'var(--warning)' }}>Cancelling</span>
              </>
            ) : isRunning ? (
              <>
                <span style={{ width: '12px', height: '12px', display: 'inline-flex' }}>
                  <Icons.Spinner />
                </span>
                Running
              </>
            ) : (
              <>
                <span style={{ width: '12px', height: '12px', display: 'inline-flex' }}>
                  <Icons.Clock />
                </span>
                Pending
              </>
            )}
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
                        ? v.slice(0, 30)
                        : JSON.stringify(v).slice(0, 30)}
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
