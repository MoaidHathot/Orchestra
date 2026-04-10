import React, { useState, useEffect, useCallback, useRef, useMemo } from 'react';
import type { Orchestration, StepEvent, TraceData, Step, StepMcpRef, RunContext } from '../../types';
import { Icons } from '../../icons';
import { renderExecutionDag } from '../../mermaid';
import { formatLogContent } from '../../formatLogContent';
import { useFocusTrap } from '../../hooks/useFocusTrap';
import ZoomableDag from '../ZoomableDag';

/** Extract a display name from a step MCP reference (string or object). */
function mcpDisplayName(ref: StepMcpRef): string {
  return typeof ref === 'string' ? ref : ref.name;
}

/**
 * The trace tool-call objects coming from the server carry more fields than
 * the strict `ToolCallData` type.  We define a local shape that matches
 * what the execution viewer actually renders.
 */
interface TraceToolCall {
  toolName: string;
  mcpServer?: string;
  success?: boolean;
  arguments?: string;
  result?: string;
  error?: string;
  durationMs?: number;
  startedAt?: string;
  completedAt?: string;
}

/**
 * The trace data the modal renders may carry richer tool-call objects and
 * response segments that are plain strings (not `{type,content}` objects).
 * We extend the shared type so the template can access these fields safely.
 */
interface RichTraceData extends Omit<TraceData, 'toolCalls' | 'responseSegments'> {
  toolCalls?: TraceToolCall[];
  responseSegments?: string[];
}

type TraceSectionKey =
  | 'systemPrompt'
  | 'userPromptRaw'
  | 'userPromptProcessed'
  | 'reasoning'
  | 'toolCalls'
  | 'responseSegments'
  | 'finalResponse'
  | 'outputHandlerResult'
  | 'mcpServers'
  | 'warnings';

interface DisplayContent {
  content: string;
  source: 'step' | 'final' | 'streaming';
}

interface Props {
  open: boolean;
  orchestration: Orchestration | null;
  executionId: string | null;
  stepStatuses: Record<string, string>;
  stepEvents: Record<string, StepEvent[]>;
  stepResults: Record<string, string>;
  stepTraces: Record<string, TraceData>;
  streamingContent: string;
  finalResult: string;
  status: string;
  errorMessage: string | null;
  completedByStep: string | null;
  runContext: RunContext | null;
  onClose: () => void;
  onCancel: (executionId: string) => void;
}

export default function ExecutionModal({
  open,
  orchestration,
  executionId,
  stepStatuses,
  stepEvents,
  stepResults,
  stepTraces,
  streamingContent,
  finalResult,
  status,
  errorMessage,
  completedByStep,
  runContext,
  onClose,
  onCancel,
}: Props): React.JSX.Element {
  const trapRef = useFocusTrap<HTMLDivElement>(open, onClose);
  const dagRef = useRef<HTMLDivElement | null>(null);
  const streamingEndRef = useRef<HTMLDivElement | null>(null);
  const [selectedStep, setSelectedStep] = useState<string | null>(null);
  const [showTracePanel, setShowTracePanel] = useState(true);
  const [showRunContext, setShowRunContext] = useState(false);
  const [expandedTraceSections, setExpandedTraceSections] = useState<
    Record<TraceSectionKey, boolean>
  >({} as Record<TraceSectionKey, boolean>);
  const [eventSearchQuery, setEventSearchQuery] = useState('');
  const [inlineError, setInlineError] = useState<string | null>(null);
  const [showGantt, setShowGantt] = useState(false);
  const [expandedLoopSteps, setExpandedLoopSteps] = useState<Record<string, boolean>>({});

  // Reset local state when switching to a different execution
  useEffect(() => {
    setSelectedStep(null);
    setShowRunContext(false);
    setExpandedTraceSections({} as Record<TraceSectionKey, boolean>);
    setEventSearchQuery('');
    setInlineError(null);
    setShowGantt(false);
    setExpandedLoopSteps({});
  }, [executionId]);

  // Toggle trace section
  const toggleTraceSection = useCallback((section: TraceSectionKey) => {
    setExpandedTraceSections((prev) => ({
      ...prev,
      [section]: !prev[section],
    }));
  }, []);

  // Determine if execution is completed
  const isCompleted = ['success', 'failed', 'cancelled', 'completed_early'].includes(status);

  // Get trace for selected step (cast to the richer shape the template expects)
  const selectedStepTrace: RichTraceData | null =
    selectedStep && stepTraces
      ? ((stepTraces[selectedStep] as unknown as RichTraceData) ?? null)
      : null;

  // Determine what content to show in the output panel
  const displayContent = useMemo<DisplayContent>(() => {
    // If a step is selected and we have its result, show it
    if (selectedStep && stepResults && stepResults[selectedStep]) {
      return { content: stepResults[selectedStep], source: 'step' };
    }
    // Otherwise show streaming content or final result
    if (isCompleted && finalResult) {
      return { content: finalResult, source: 'final' };
    }
    return { content: streamingContent || '', source: 'streaming' };
  }, [selectedStep, stepResults, streamingContent, finalResult, isCompleted]);

  // Copy content to clipboard
  const copyContent = useCallback(() => {
    if (displayContent.content) {
      navigator.clipboard.writeText(displayContent.content).catch((err) => {
        setInlineError(`Failed to copy to clipboard: ${err instanceof Error ? err.message : String(err)}`);
      });
    }
  }, [displayContent.content]);

  // Download content as file
  const downloadContent = useCallback(() => {
    if (displayContent.content) {
      try {
        const blob = new Blob([displayContent.content], { type: 'text/plain' });
        const url = URL.createObjectURL(blob);
        const a = document.createElement('a');
        a.href = url;
        const filename = selectedStep
          ? `${orchestration?.name || 'orchestration'}_${selectedStep}_output.txt`
          : `${orchestration?.name || 'orchestration'}_result.txt`;
        a.download = filename;
        document.body.appendChild(a);
        a.click();
        document.body.removeChild(a);
        URL.revokeObjectURL(url);
      } catch (err) {
        setInlineError(`Failed to download: ${err instanceof Error ? err.message : String(err)}`);
      }
    }
  }, [displayContent.content, selectedStep, orchestration?.name]);

  // Render DAG when orchestration or step statuses change.
  // NOTE: `selectedStep` is intentionally excluded from deps — including it
  // caused a full SVG re-render on every click, which destroyed event
  // listeners mid-propagation and could trigger unwanted navigation.
  // Selected-step highlighting is handled via direct DOM manipulation below.
  // `showGantt` IS included: when the user switches from Timeline back to DAG,
  // the ZoomableDag remounts and the ref becomes a fresh empty div.
  useEffect(() => {
    if (open && dagRef.current && orchestration && !showGantt) {
      renderExecutionDag(
        orchestration,
        stepStatuses || {},
        dagRef.current,
        setSelectedStep,
      );
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open, orchestration, stepStatuses, showGantt]);

  // Highlight the selected step via direct DOM manipulation instead of
  // re-rendering the entire DAG (which would destroy click handlers).
  useEffect(() => {
    if (!dagRef.current) return;
    const svg = dagRef.current.querySelector('svg');
    if (!svg) return;

    // Reset all node highlights first
    svg.querySelectorAll('.node rect, .node polygon, .node circle, .node ellipse').forEach((el) => {
      (el as HTMLElement).style.strokeWidth = '';
    });

    // Apply highlight to selected step
    if (selectedStep) {
      const safeId = selectedStep.replace(/[^a-zA-Z0-9]/g, '_');
      // Mermaid node IDs follow the pattern "flowchart-<safeId>-<number>"
      const nodes = svg.querySelectorAll('.node');
      nodes.forEach((node) => {
        const nodeId = node.id?.replace('flowchart-', '').replace(/-\d+$/, '');
        if (nodeId === safeId) {
          const rect = node.querySelector('rect, polygon, circle, ellipse') as HTMLElement | null;
          if (rect) {
            rect.style.strokeWidth = '3px';
          }
        }
      });
    }
  }, [selectedStep]);

  // Auto-scroll streaming content
  useEffect(() => {
    streamingEndRef.current?.scrollIntoView({ behavior: 'smooth' });
  }, [streamingContent]);

  // Get events for selected step
  const selectedStepEvents: StepEvent[] =
    selectedStep && stepEvents ? stepEvents[selectedStep] || [] : [];
  const selectedStepStatus: string | null =
    selectedStep && stepStatuses ? stepStatuses[selectedStep] || 'pending' : null;

  // Get selected step data (including MCPs)
  const selectedStepData = useMemo<Step | null>(() => {
    if (!selectedStep || !orchestration?.steps) return null;
    const step = orchestration.steps.find(
      (s) => (typeof s === 'string' ? s : s?.name) === selectedStep,
    );
    return typeof step === 'object' ? step : null;
  }, [selectedStep, orchestration]);

  const handleCancel = useCallback(() => {
    if (executionId) {
      onCancel(executionId);
    }
  }, [executionId, onCancel]);

  /** Format a duration in ms to a human-readable string like "1.2s" or "350ms". */
  const formatDuration = useCallback((ms: number): string => {
    if (ms < 1000) return `${Math.round(ms)}ms`;
    if (ms < 60000) return `${(ms / 1000).toFixed(1)}s`;
    return `${(ms / 60000).toFixed(1)}m`;
  }, []);

  /** Compute tool call duration from durationMs or startedAt/completedAt. */
  const getToolCallDuration = useCallback(
    (tc: TraceToolCall): string | null => {
      if (tc.durationMs != null && tc.durationMs > 0) {
        return formatDuration(tc.durationMs);
      }
      if (tc.startedAt && tc.completedAt) {
        const ms =
          new Date(tc.completedAt).getTime() - new Date(tc.startedAt).getTime();
        if (ms >= 0) return formatDuration(ms);
      }
      return null;
    },
    [formatDuration],
  );

  /** Filtered step events based on search query. */
  const filteredStepEvents = useMemo<StepEvent[]>(() => {
    if (!eventSearchQuery.trim()) return selectedStepEvents;
    const q = eventSearchQuery.toLowerCase();
    return selectedStepEvents.filter((event) => {
      const text = [
        event.type,
        event.content,
        event.toolName,
        event.error,
        event.timestamp,
        formatLogContent(event),
      ]
        .filter(Boolean)
        .join(' ')
        .toLowerCase();
      return text.includes(q);
    });
  }, [selectedStepEvents, eventSearchQuery]);

  /** Detect loop iterations from stepStatuses keys (pattern: "stepName:iteration-N"). */
  const loopIterations = useMemo<Record<string, { iteration: number; status: string; key: string }[]>>(() => {
    const result: Record<string, { iteration: number; status: string; key: string }[]> = {};
    if (!stepStatuses) return result;
    for (const key of Object.keys(stepStatuses)) {
      const match = key.match(/^(.+):iteration-(\d+)$/);
      if (match) {
        const baseName = match[1];
        const iterNum = parseInt(match[2], 10);
        if (!result[baseName]) result[baseName] = [];
        result[baseName].push({
          iteration: iterNum,
          status: stepStatuses[key],
          key,
        });
      }
    }
    // Sort iterations by number
    for (const baseName of Object.keys(result)) {
      result[baseName].sort((a, b) => a.iteration - b.iteration);
    }
    return result;
  }, [stepStatuses]);

  /** Gantt chart data: steps with startedAt/completedAt from events. */
  const ganttData = useMemo(() => {
    if (!orchestration?.steps) return [];
    const items: {
      name: string;
      startMs: number;
      endMs: number;
      status: string;
    }[] = [];
    for (const step of orchestration.steps) {
      const stepName = typeof step === 'string' ? step : step?.name;
      if (!stepName) continue;

      let startTime: number | null = null;
      let endTime: number | null = null;
      const stepStatus = stepStatuses[stepName] || 'pending';

      // Source 1: SSE events with ISO timestamp
      const events = stepEvents?.[stepName];
      if (events && events.length > 0) {
        for (const ev of events) {
          const ts = ev.timestamp;
          if (!ts) continue;
          const parsed = new Date(ts).getTime();
          if (isNaN(parsed)) continue;
          if (ev.type === 'step-started') {
            startTime = parsed;
          }
          if (
            ev.type === 'step-completed' ||
            ev.type === 'step-error' ||
            ev.type === 'step-cancelled' ||
            ev.type === 'step-skipped'
          ) {
            endTime = parsed;
          }
        }
      }

      // Source 2: Step trace data (available from API for historical runs)
      // The trace may not carry exact timestamps but the step result data
      // from the execution context does (provided via runContext or step metadata).
      // We skip steps with no timing data entirely.

      if (startTime && !isNaN(startTime)) {
        items.push({
          name: stepName,
          startMs: startTime,
          endMs: endTime && !isNaN(endTime) ? endTime : Date.now(),
          status: stepStatus,
        });
      }
    }
    return items;
  }, [stepEvents, orchestration, stepStatuses]);

  /** Inline error dismiss handler */
  const dismissInlineError = useCallback(() => setInlineError(null), []);

  return (
    <div
      ref={trapRef}
      className={`modal-overlay ${open ? 'visible' : ''}`}
      onClick={(e: React.MouseEvent<HTMLDivElement>) => {
        if (e.target === e.currentTarget) onClose();
      }}
    >
      <div className="modal modal-lg" role="dialog" aria-modal="true" aria-label="Execution details">
        <div className="modal-header">
          <div className="flex items-center gap-2">
            <div className="modal-title">
              {orchestration
                ? `Execution: ${orchestration.name}`
                : 'Running Orchestration'}
            </div>
            {status === 'loading' && <div className="spinner"></div>}
            {status === 'running' && <div className="spinner"></div>}
            {status === 'cancelling' && (
              <>
                <div
                  className="spinner"
                  style={{ borderTopColor: 'var(--warning)' }}
                ></div>
                <span className="text-warning">Cancelling...</span>
              </>
            )}
            {status === 'success' && (
              <span className="text-success">Completed</span>
            )}
            {status === 'completed_early' && (
              <span className="text-completed-early">
                Completed Early{completedByStep ? ` (by ${completedByStep})` : ''}
              </span>
            )}
            {status === 'failed' && <span className="text-error">Failed</span>}
            {status === 'cancelled' && (
              <span className="text-warning">Cancelled</span>
            )}
            {status === 'error' && <span className="text-error">Error</span>}
          </div>
          <button className="modal-close" onClick={onClose} aria-label="Close">
            <Icons.X />
          </button>
        </div>
        <div className="modal-body" style={{ padding: 0 }}>
          {/* Error Message Display */}
          {status === 'error' && errorMessage && (
            <div
              style={{
                padding: '12px 16px',
                background: 'rgba(248, 81, 73, 0.1)',
                borderBottom: '1px solid var(--error)',
                display: 'flex',
                alignItems: 'center',
                gap: '12px',
              }}
            >
              <Icons.X />
              <div style={{ flex: 1 }}>
                <div
                  style={{
                    color: 'var(--error)',
                    fontWeight: 500,
                    marginBottom: '2px',
                  }}
                >
                  Error
                </div>
                <div style={{ color: 'var(--text-muted)', fontSize: '13px' }}>
                  {errorMessage}
                </div>
              </div>
            </div>
          )}
          {/* Inline Error Banner (replacement for window.alert) */}
          {inlineError && (
            <div
              style={{
                padding: '10px 16px',
                background: 'rgba(248, 81, 73, 0.1)',
                borderBottom: '1px solid var(--error)',
                display: 'flex',
                alignItems: 'center',
                gap: '10px',
              }}
            >
              <Icons.X />
              <div style={{ flex: 1, color: 'var(--error)', fontSize: '13px' }}>
                {inlineError}
              </div>
              <button
                onClick={dismissInlineError}
                style={{
                  background: 'none',
                  border: '1px solid var(--error)',
                  borderRadius: '4px',
                  color: 'var(--error)',
                  cursor: 'pointer',
                  padding: '2px 8px',
                  fontSize: '11px',
                }}
              >
                Dismiss
              </button>
            </div>
          )}
          {/* Run Context Panel */}
          {runContext && (
            <div className="run-context-panel">
              <div
                className="run-context-header"
                onClick={() => setShowRunContext(!showRunContext)}
              >
                <span
                  style={{
                    transform: showRunContext ? 'rotate(90deg)' : 'rotate(0deg)',
                    transition: 'transform 0.15s',
                    display: 'inline-block',
                  }}
                >
                  &#9654;
                </span>
                <span style={{ fontWeight: 600, color: 'var(--accent)' }}>
                  Run Context
                </span>
                <span style={{ fontSize: '11px', color: 'var(--text-dim)', marginLeft: '8px' }}>
                  {runContext.orchestrationName} v{runContext.orchestrationVersion}
                </span>
              </div>
              {showRunContext && (
                <div className="run-context-body">
                  {/* Summary row */}
                  <div className="run-context-summary">
                    <div className="run-context-field">
                      <span className="run-context-label">Run ID</span>
                      <span className="run-context-value run-context-mono">{runContext.runId}</span>
                    </div>
                    <div className="run-context-field">
                      <span className="run-context-label">Orchestration</span>
                      <span className="run-context-value">{runContext.orchestrationName} v{runContext.orchestrationVersion}</span>
                    </div>
                    <div className="run-context-field">
                      <span className="run-context-label">Started At</span>
                      <span className="run-context-value">{runContext.startedAt ? new Date(runContext.startedAt).toLocaleString() : 'N/A'}</span>
                    </div>
                    <div className="run-context-field">
                      <span className="run-context-label">Triggered By</span>
                      <span className="run-context-value">{runContext.triggeredBy || 'N/A'}</span>
                    </div>
                    {runContext.triggerId && (
                      <div className="run-context-field">
                        <span className="run-context-label">Trigger ID</span>
                        <span className="run-context-value run-context-mono">{runContext.triggerId}</span>
                      </div>
                    )}
                    {runContext.dataDirectory && (
                      <div className="run-context-field" style={{ gridColumn: '1 / -1' }}>
                        <span className="run-context-label">Data Directory</span>
                        <span className="run-context-value run-context-mono" style={{ fontSize: '11px', wordBreak: 'break-all' }}>{runContext.dataDirectory}</span>
                      </div>
                    )}
                  </div>

                  {/* Parameters */}
                  {runContext.parameters && Object.keys(runContext.parameters).length > 0 && (
                    <div className="run-context-table-section">
                      <div className="run-context-table-title">Parameters</div>
                      <table className="run-context-table">
                        <thead>
                          <tr><th>Name</th><th>Value</th></tr>
                        </thead>
                        <tbody>
                          {Object.entries(runContext.parameters).map(([key, value]) => (
                            <tr key={key}>
                              <td className="run-context-mono">{key}</td>
                              <td>{value}</td>
                            </tr>
                          ))}
                        </tbody>
                      </table>
                    </div>
                  )}

                  {/* Variables (raw) */}
                  {runContext.variables && Object.keys(runContext.variables).length > 0 && (
                    <div className="run-context-table-section">
                      <div className="run-context-table-title">Variables (Raw)</div>
                      <table className="run-context-table">
                        <thead>
                          <tr><th>Name</th><th>Value</th></tr>
                        </thead>
                        <tbody>
                          {Object.entries(runContext.variables).map(([key, value]) => (
                            <tr key={key}>
                              <td className="run-context-mono">{key}</td>
                              <td>{value}</td>
                            </tr>
                          ))}
                        </tbody>
                      </table>
                    </div>
                  )}

                  {/* Resolved Variables */}
                  {runContext.resolvedVariables && Object.keys(runContext.resolvedVariables).length > 0 && (
                    <div className="run-context-table-section">
                      <div className="run-context-table-title">Resolved Variables</div>
                      <table className="run-context-table">
                        <thead>
                          <tr><th>Name</th><th>Resolved Value</th></tr>
                        </thead>
                        <tbody>
                          {Object.entries(runContext.resolvedVariables).map(([key, value]) => (
                            <tr key={key}>
                              <td className="run-context-mono">{key}</td>
                              <td>{value}</td>
                            </tr>
                          ))}
                        </tbody>
                      </table>
                    </div>
                  )}

                  {/* Accessed Environment Variables */}
                  {runContext.accessedEnvironmentVariables && Object.keys(runContext.accessedEnvironmentVariables).length > 0 && (
                    <div className="run-context-table-section">
                      <div className="run-context-table-title">Accessed Environment Variables</div>
                      <table className="run-context-table">
                        <thead>
                          <tr><th>Name</th><th>Value</th></tr>
                        </thead>
                        <tbody>
                          {Object.entries(runContext.accessedEnvironmentVariables).map(([key, value]) => (
                            <tr key={key}>
                              <td className="run-context-mono">{key}</td>
                              <td>{value !== null ? value : <span style={{ color: 'var(--text-dim)', fontStyle: 'italic' }}>not set</span>}</td>
                            </tr>
                          ))}
                        </tbody>
                      </table>
                    </div>
                  )}
                </div>
              )}
            </div>
          )}
          <div className="execution-modal-content">
            {/* DAG Section */}
            <div className="execution-dag-section">
              {!orchestration ? (
                <div className="empty-state">
                  <div className="spinner"></div>
                  <div className="empty-text" style={{ marginTop: '8px' }}>
                    Loading orchestration...
                  </div>
                </div>
              ) : (
                <div style={{ display: 'flex', flexDirection: 'column', height: '100%' }}>
                  {/* DAG / Gantt toggle */}
                  <div
                    style={{
                      display: 'flex',
                      gap: '4px',
                      padding: '4px 8px',
                      borderBottom: '1px solid var(--border-subtle)',
                      flexShrink: 0,
                    }}
                  >
                    <button
                      className={`btn btn-sm${!showGantt ? ' btn-primary' : ''}`}
                      onClick={() => setShowGantt(false)}
                      style={{ padding: '3px 10px', fontSize: '11px' }}
                    >
                      DAG
                    </button>
                    <button
                      className={`btn btn-sm${showGantt ? ' btn-primary' : ''}`}
                      onClick={() => setShowGantt(true)}
                      style={{ padding: '3px 10px', fontSize: '11px' }}
                    >
                      Timeline
                    </button>
                  </div>
                  {showGantt ? (
                    /* Gantt Chart Timeline View */
                    <div
                      style={{
                        flex: 1,
                        overflow: 'auto',
                        padding: '12px',
                      }}
                    >
                      {ganttData.length === 0 ? (
                        <div className="empty-state">
                          <div className="empty-text">
                            No timing data available.
                            {status === 'running' || status === 'loading'
                              ? ' Timeline will populate as steps start executing.'
                              : ' Timeline data is only available for live or recently viewed executions.'}
                          </div>
                        </div>
                      ) : (
                        (() => {
                          const minStart = Math.min(...ganttData.map((d) => d.startMs));
                          const maxEnd = Math.max(...ganttData.map((d) => d.endMs));
                          const totalSpan = maxEnd - minStart || 1;
                          const barHeight = 24;
                          const labelWidth = 120;
                          const chartWidth = 400;
                          const svgWidth = labelWidth + chartWidth + 80;
                          const svgHeight = ganttData.length * (barHeight + 8) + 30;
                          const statusColor: Record<string, string> = {
                            completed: '#3fb950',
                            success: '#3fb950',
                            completed_early: '#3fb950',
                            failed: '#f85149',
                            running: '#d29922',
                            pending: '#8b949e',
                            skipped: '#8b949e',
                            cancelled: '#8b949e',
                            noaction: '#8b949e',
                          };

                          return (
                            <svg
                              width={svgWidth}
                              height={svgHeight}
                              style={{ fontFamily: 'monospace', fontSize: '11px' }}
                            >
                              {ganttData.map((item, idx) => {
                                const y = idx * (barHeight + 8) + 4;
                                const barStart =
                                  labelWidth +
                                  ((item.startMs - minStart) / totalSpan) * chartWidth;
                                const barW = Math.max(
                                  2,
                                  ((item.endMs - item.startMs) / totalSpan) * chartWidth,
                                );
                                const color =
                                  statusColor[item.status] || statusColor.pending;
                                const durationMs = item.endMs - item.startMs;
                                const durLabel =
                                  durationMs < 1000
                                    ? `${Math.round(durationMs)}ms`
                                    : durationMs < 60000
                                      ? `${(durationMs / 1000).toFixed(1)}s`
                                      : `${(durationMs / 60000).toFixed(1)}m`;
                                return (
                                  <g key={item.name}>
                                    {/* Step name label */}
                                    <text
                                      x={labelWidth - 6}
                                      y={y + barHeight / 2 + 4}
                                      textAnchor="end"
                                      fill="var(--text-muted)"
                                    >
                                      {item.name.length > 16
                                        ? item.name.substring(0, 15) + '\u2026'
                                        : item.name}
                                    </text>
                                    {/* Bar */}
                                    <rect
                                      x={barStart}
                                      y={y}
                                      width={barW}
                                      height={barHeight}
                                      rx={3}
                                      fill={color}
                                      opacity={0.85}
                                    >
                                      <title>
                                        {item.name}: {durLabel} ({item.status})
                                      </title>
                                    </rect>
                                    {/* Duration label */}
                                    <text
                                      x={barStart + barW + 4}
                                      y={y + barHeight / 2 + 4}
                                      fill="var(--text-dim)"
                                      style={{ fontSize: '10px' }}
                                    >
                                      {durLabel}
                                    </text>
                                  </g>
                                );
                              })}
                            </svg>
                          );
                        })()
                      )}
                    </div>
                  ) : (
                    <ZoomableDag dagRef={dagRef} style={{ flex: 1 }} />
                  )}
                </div>
              )}
            </div>

            {/* Details Section: Step Events + Streaming Output */}
            <div className="execution-details-section">
              {/* Step Events Panel */}
              <div className="execution-step-events">
                {selectedStep ? (
                  <>
                    <div
                      className="selected-step-header"
                      style={{
                        display: 'flex',
                        alignItems: 'center',
                        justifyContent: 'space-between',
                      }}
                    >
                      <div style={{ display: 'flex', alignItems: 'center' }}>
                        <span>{selectedStep}</span>
                        {selectedStepStatus && (
                          <span
                            className={`step-status-badge ${selectedStepStatus}`}
                            style={{ marginLeft: '8px' }}
                          >
                            {selectedStepStatus}
                          </span>
                        )}
                        {/* Loop Iteration Pill */}
                        {selectedStep && loopIterations[selectedStep] && (
                          <span
                            style={{
                              marginLeft: '8px',
                              position: 'relative',
                              display: 'inline-block',
                            }}
                          >
                            <button
                              onClick={() =>
                                setExpandedLoopSteps((prev) => ({
                                  ...prev,
                                  [selectedStep]: !prev[selectedStep],
                                }))
                              }
                              style={{
                                background: 'var(--surface)',
                                border: '1px solid var(--border-subtle)',
                                borderRadius: '12px',
                                padding: '2px 10px',
                                fontSize: '11px',
                                color: 'var(--accent)',
                                cursor: 'pointer',
                                fontWeight: 500,
                              }}
                            >
                              {loopIterations[selectedStep].length} iteration
                              {loopIterations[selectedStep].length !== 1 ? 's' : ''}
                            </button>
                            {expandedLoopSteps[selectedStep] && (
                              <div
                                style={{
                                  position: 'absolute',
                                  top: '100%',
                                  left: 0,
                                  zIndex: 50,
                                  marginTop: '4px',
                                  background: 'var(--bg-secondary)',
                                  border: '1px solid var(--border-subtle)',
                                  borderRadius: '6px',
                                  boxShadow: '0 4px 12px rgba(0,0,0,0.3)',
                                  minWidth: '200px',
                                  maxHeight: '200px',
                                  overflow: 'auto',
                                }}
                              >
                                {loopIterations[selectedStep].map((iter) => {
                                  const iterStatus = iter.status;
                                  const statusColor =
                                    iterStatus === 'completed'
                                      ? 'var(--success)'
                                      : iterStatus === 'failed'
                                        ? 'var(--error)'
                                        : iterStatus === 'running'
                                          ? 'var(--warning)'
                                          : 'var(--text-dim)';
                                  return (
                                    <div
                                      key={iter.key}
                                      onClick={() => {
                                        setSelectedStep(iter.key);
                                        setExpandedLoopSteps((prev) => ({
                                          ...prev,
                                          [selectedStep]: false,
                                        }));
                                      }}
                                      style={{
                                        padding: '6px 12px',
                                        cursor: 'pointer',
                                        display: 'flex',
                                        alignItems: 'center',
                                        gap: '8px',
                                        borderBottom:
                                          '1px solid var(--border-subtle)',
                                        fontSize: '12px',
                                      }}
                                      onMouseEnter={(e) =>
                                        (e.currentTarget.style.background =
                                          'var(--surface)')
                                      }
                                      onMouseLeave={(e) =>
                                        (e.currentTarget.style.background =
                                          'transparent')
                                      }
                                    >
                                      <span
                                        style={{
                                          width: '8px',
                                          height: '8px',
                                          borderRadius: '50%',
                                          background: statusColor,
                                          flexShrink: 0,
                                        }}
                                      />
                                      <span style={{ color: 'var(--text)' }}>
                                        Iteration {iter.iteration}
                                      </span>
                                      <span
                                        style={{
                                          marginLeft: 'auto',
                                          fontSize: '10px',
                                          color: statusColor,
                                        }}
                                      >
                                        {iterStatus}
                                      </span>
                                    </div>
                                  );
                                })}
                              </div>
                            )}
                          </span>
                        )}
                      </div>
                      {selectedStepTrace && (
                        <button
                          className="btn btn-sm"
                          onClick={() => setShowTracePanel(!showTracePanel)}
                          style={{ padding: '4px 8px', fontSize: '11px' }}
                        >
                          {showTracePanel ? 'Events' : 'Trace'}
                        </button>
                      )}
                    </div>

                    {/* Step Info: Type, Model, MCPs */}
                    {selectedStepData && (
                      <div
                        className="step-info-section"
                        style={{
                          padding: '8px 12px',
                          background: 'var(--bg-tertiary)',
                          borderRadius: '6px',
                          marginBottom: '8px',
                          fontSize: '12px',
                        }}
                      >
                        <div
                          style={{
                            display: 'flex',
                            gap: '16px',
                            flexWrap: 'wrap',
                            marginBottom:
                              selectedStepData.mcps &&
                              selectedStepData.mcps.length > 0
                                ? '8px'
                                : '0',
                          }}
                        >
                          {selectedStepData.type && (
                            <div>
                              <span style={{ color: 'var(--text-dim)' }}>
                                Type:
                              </span>{' '}
                              <span style={{ color: 'var(--text-secondary)' }}>
                                {selectedStepData.type}
                              </span>
                            </div>
                          )}
                          {selectedStepData.model && (
                            <div>
                              <span style={{ color: 'var(--text-dim)' }}>
                                Model:
                              </span>{' '}
                              <span style={{ color: 'var(--text-secondary)' }}>
                                {selectedStepData.model}
                              </span>
                            </div>
                          )}
                        </div>
                        {selectedStepData.mcps &&
                          selectedStepData.mcps.length > 0 && (
                            <div>
                              <span style={{ color: 'var(--text-dim)' }}>
                                MCPs:
                              </span>
                              <div
                                style={{
                                  display: 'flex',
                                  gap: '6px',
                                  flexWrap: 'wrap',
                                  marginTop: '4px',
                                }}
                              >
                                {selectedStepData.mcps.map((mcp, i) => (
                                  <span
                                    key={i}
                                    style={{
                                      background: 'var(--bg-secondary)',
                                      padding: '2px 8px',
                                      borderRadius: '4px',
                                      color: 'var(--accent)',
                                      border:
                                        '1px solid var(--border-subtle)',
                                    }}
                                  >
                                    {mcpDisplayName(mcp)}
                                  </span>
                                ))}
                              </div>
                            </div>
                          )}
                      </div>
                    )}

                    {/* Trace Panel (detailed debug view) */}
                    {showTracePanel && selectedStepTrace ? (
                      <div
                        className="trace-panel"
                        style={{ fontSize: '12px', overflow: 'auto', flex: 1 }}
                      >
                        {/* System Prompt */}
                        {selectedStepTrace.systemPrompt && (
                          <div
                            className="trace-section"
                            style={{ marginBottom: '12px' }}
                          >
                            <div
                              className="trace-section-header"
                              onClick={() =>
                                toggleTraceSection('systemPrompt')
                              }
                              style={{
                                display: 'flex',
                                alignItems: 'center',
                                gap: '6px',
                                cursor: 'pointer',
                                padding: '6px 8px',
                                background: 'var(--surface)',
                                borderRadius: '4px',
                                marginBottom:
                                  expandedTraceSections.systemPrompt
                                    ? '4px'
                                    : '0',
                              }}
                            >
                              <span
                                style={{
                                  transform:
                                    expandedTraceSections.systemPrompt
                                      ? 'rotate(90deg)'
                                      : 'rotate(0deg)',
                                  transition: 'transform 0.15s',
                                }}
                              >
                                &#9654;
                              </span>
                              <span
                                style={{
                                  fontWeight: 500,
                                  color: 'var(--purple)',
                                }}
                              >
                                System Prompt
                              </span>
                            </div>
                            {expandedTraceSections.systemPrompt && (
                              <pre
                                style={{
                                  background: 'var(--bg)',
                                  padding: '8px',
                                  borderRadius: '4px',
                                  whiteSpace: 'pre-wrap',
                                  fontSize: '11px',
                                  color: 'var(--text-muted)',
                                  maxHeight: '150px',
                                  overflow: 'auto',
                                }}
                              >
                                {selectedStepTrace.systemPrompt}
                              </pre>
                            )}
                          </div>
                        )}

                        {/* User Prompt Raw */}
                        {selectedStepTrace.userPromptRaw && (
                          <div
                            className="trace-section"
                            style={{ marginBottom: '12px' }}
                          >
                            <div
                              className="trace-section-header"
                              onClick={() =>
                                toggleTraceSection('userPromptRaw')
                              }
                              style={{
                                display: 'flex',
                                alignItems: 'center',
                                gap: '6px',
                                cursor: 'pointer',
                                padding: '6px 8px',
                                background: 'var(--surface)',
                                borderRadius: '4px',
                                marginBottom:
                                  expandedTraceSections.userPromptRaw
                                    ? '4px'
                                    : '0',
                              }}
                            >
                              <span
                                style={{
                                  transform:
                                    expandedTraceSections.userPromptRaw
                                      ? 'rotate(90deg)'
                                      : 'rotate(0deg)',
                                  transition: 'transform 0.15s',
                                }}
                              >
                                &#9654;
                              </span>
                              <span
                                style={{
                                  fontWeight: 500,
                                  color: 'var(--cyan)',
                                }}
                              >
                                User Prompt (Raw)
                              </span>
                            </div>
                            {expandedTraceSections.userPromptRaw && (
                              <pre
                                style={{
                                  background: 'var(--bg)',
                                  padding: '8px',
                                  borderRadius: '4px',
                                  whiteSpace: 'pre-wrap',
                                  fontSize: '11px',
                                  color: 'var(--text-muted)',
                                  maxHeight: '150px',
                                  overflow: 'auto',
                                }}
                              >
                                {selectedStepTrace.userPromptRaw}
                              </pre>
                            )}
                          </div>
                        )}

                        {/* User Prompt Processed (after input handler) */}
                        {selectedStepTrace.userPromptProcessed &&
                          selectedStepTrace.userPromptProcessed !==
                            selectedStepTrace.userPromptRaw && (
                            <div
                              className="trace-section"
                              style={{ marginBottom: '12px' }}
                            >
                              <div
                                className="trace-section-header"
                                onClick={() =>
                                  toggleTraceSection('userPromptProcessed')
                                }
                                style={{
                                  display: 'flex',
                                  alignItems: 'center',
                                  gap: '6px',
                                  cursor: 'pointer',
                                  padding: '6px 8px',
                                  background: 'var(--surface)',
                                  borderRadius: '4px',
                                  marginBottom:
                                    expandedTraceSections.userPromptProcessed
                                      ? '4px'
                                      : '0',
                                }}
                              >
                                <span
                                  style={{
                                    transform:
                                      expandedTraceSections.userPromptProcessed
                                        ? 'rotate(90deg)'
                                        : 'rotate(0deg)',
                                    transition: 'transform 0.15s',
                                  }}
                                >
                                  &#9654;
                                </span>
                                <span
                                  style={{
                                    fontWeight: 500,
                                    color: 'var(--cyan)',
                                  }}
                                >
                                  User Prompt (After Input Handler)
                                </span>
                              </div>
                              {expandedTraceSections.userPromptProcessed && (
                                <pre
                                  style={{
                                    background: 'var(--bg)',
                                    padding: '8px',
                                    borderRadius: '4px',
                                    whiteSpace: 'pre-wrap',
                                    fontSize: '11px',
                                    color: 'var(--text-muted)',
                                    maxHeight: '150px',
                                    overflow: 'auto',
                                  }}
                                >
                                  {selectedStepTrace.userPromptProcessed}
                                </pre>
                              )}
                            </div>
                          )}

                        {/* Reasoning */}
                        {selectedStepTrace.reasoning && (
                          <div
                            className="trace-section"
                            style={{ marginBottom: '12px' }}
                          >
                            <div
                              className="trace-section-header"
                              onClick={() => toggleTraceSection('reasoning')}
                              style={{
                                display: 'flex',
                                alignItems: 'center',
                                gap: '6px',
                                cursor: 'pointer',
                                padding: '6px 8px',
                                background: 'var(--surface)',
                                borderRadius: '4px',
                                marginBottom:
                                  expandedTraceSections.reasoning
                                    ? '4px'
                                    : '0',
                              }}
                            >
                              <span
                                style={{
                                  transform: expandedTraceSections.reasoning
                                    ? 'rotate(90deg)'
                                    : 'rotate(0deg)',
                                  transition: 'transform 0.15s',
                                }}
                              >
                                &#9654;
                              </span>
                              <span
                                style={{
                                  fontWeight: 500,
                                  color: 'var(--warning)',
                                }}
                              >
                                Reasoning
                              </span>
                            </div>
                            {expandedTraceSections.reasoning && (
                              <pre
                                style={{
                                  background: 'var(--bg)',
                                  padding: '8px',
                                  borderRadius: '4px',
                                  whiteSpace: 'pre-wrap',
                                  fontSize: '11px',
                                  color: 'var(--text-muted)',
                                  maxHeight: '200px',
                                  overflow: 'auto',
                                }}
                              >
                                {selectedStepTrace.reasoning}
                              </pre>
                            )}
                          </div>
                        )}

                        {/* Tool Calls */}
                        {selectedStepTrace.toolCalls &&
                          selectedStepTrace.toolCalls.length > 0 && (
                            <div
                              className="trace-section"
                              style={{ marginBottom: '12px' }}
                            >
                              <div
                                className="trace-section-header"
                                onClick={() =>
                                  toggleTraceSection('toolCalls')
                                }
                                style={{
                                  display: 'flex',
                                  alignItems: 'center',
                                  gap: '6px',
                                  cursor: 'pointer',
                                  padding: '6px 8px',
                                  background: 'var(--surface)',
                                  borderRadius: '4px',
                                  marginBottom:
                                    expandedTraceSections.toolCalls
                                      ? '4px'
                                      : '0',
                                }}
                              >
                                <span
                                  style={{
                                    transform:
                                      expandedTraceSections.toolCalls
                                        ? 'rotate(90deg)'
                                        : 'rotate(0deg)',
                                    transition: 'transform 0.15s',
                                  }}
                                >
                                  &#9654;
                                </span>
                                <span
                                  style={{
                                    fontWeight: 500,
                                    color: 'var(--accent)',
                                  }}
                                >
                                  Tool Calls (
                                  {selectedStepTrace.toolCalls.length})
                                </span>
                              </div>
                              {expandedTraceSections.toolCalls && (
                                <div
                                  style={{
                                    display: 'flex',
                                    flexDirection: 'column',
                                    gap: '8px',
                                  }}
                                >
                                  {selectedStepTrace.toolCalls.map((tc, i) => (
                                    <div
                                      key={i}
                                      style={{
                                        background: 'var(--bg)',
                                        padding: '8px',
                                        borderRadius: '4px',
                                        borderLeft: `3px solid ${
                                          tc.success
                                            ? 'var(--success)'
                                            : 'var(--error)'
                                        }`,
                                      }}
                                    >
                                      <div
                                        style={{
                                          display: 'flex',
                                          alignItems: 'center',
                                          gap: '8px',
                                          marginBottom: '4px',
                                        }}
                                      >
                                        <span
                                          style={{
                                            fontWeight: 500,
                                            color: 'var(--text)',
                                          }}
                                        >
                                          {tc.toolName}
                                        </span>
                                        {(() => {
                                          const dur = getToolCallDuration(tc);
                                          return dur ? (
                                            <span
                                              style={{
                                                fontSize: '10px',
                                                color: 'var(--text-dim)',
                                                fontWeight: 400,
                                              }}
                                            >
                                              ({dur})
                                            </span>
                                          ) : null;
                                        })()}
                                        {tc.mcpServer && (
                                          <span
                                            style={{
                                              fontSize: '10px',
                                              color: 'var(--text-dim)',
                                              background: 'var(--surface)',
                                              padding: '1px 4px',
                                              borderRadius: '3px',
                                            }}
                                          >
                                            {tc.mcpServer}
                                          </span>
                                        )}
                                        <span
                                          style={{
                                            fontSize: '10px',
                                            color: tc.success
                                              ? 'var(--success)'
                                              : 'var(--error)',
                                          }}
                                        >
                                          {tc.success ? 'OK' : 'FAILED'}
                                        </span>
                                      </div>
                                      {tc.arguments && (
                                        <details
                                          style={{ marginBottom: '4px' }}
                                        >
                                          <summary
                                            style={{
                                              cursor: 'pointer',
                                              fontSize: '11px',
                                              color: 'var(--text-dim)',
                                            }}
                                          >
                                            Arguments
                                          </summary>
                                          <pre
                                            style={{
                                              whiteSpace: 'pre-wrap',
                                              fontSize: '10px',
                                              color: 'var(--text-muted)',
                                              marginTop: '4px',
                                              background: 'var(--surface)',
                                              padding: '6px',
                                              borderRadius: '3px',
                                              maxHeight: '100px',
                                              overflow: 'auto',
                                            }}
                                          >
                                            {tc.arguments}
                                          </pre>
                                        </details>
                                      )}
                                      {tc.result && (
                                        <details>
                                          <summary
                                            style={{
                                              cursor: 'pointer',
                                              fontSize: '11px',
                                              color: 'var(--text-dim)',
                                            }}
                                          >
                                            Result
                                          </summary>
                                          <pre
                                            style={{
                                              whiteSpace: 'pre-wrap',
                                              fontSize: '10px',
                                              color: 'var(--text-muted)',
                                              marginTop: '4px',
                                              background: 'var(--surface)',
                                              padding: '6px',
                                              borderRadius: '3px',
                                              maxHeight: '100px',
                                              overflow: 'auto',
                                            }}
                                          >
                                            {tc.result}
                                          </pre>
                                        </details>
                                      )}
                                      {tc.error && (
                                        <div
                                          style={{
                                            fontSize: '11px',
                                            color: 'var(--error)',
                                            marginTop: '4px',
                                          }}
                                        >
                                          Error: {tc.error}
                                        </div>
                                      )}
                                    </div>
                                  ))}
                                </div>
                              )}
                            </div>
                          )}

                        {/* Response Segments */}
                        {selectedStepTrace.responseSegments &&
                          selectedStepTrace.responseSegments.length > 0 && (
                            <div
                              className="trace-section"
                              style={{ marginBottom: '12px' }}
                            >
                              <div
                                className="trace-section-header"
                                onClick={() =>
                                  toggleTraceSection('responseSegments')
                                }
                                style={{
                                  display: 'flex',
                                  alignItems: 'center',
                                  gap: '6px',
                                  cursor: 'pointer',
                                  padding: '6px 8px',
                                  background: 'var(--surface)',
                                  borderRadius: '4px',
                                  marginBottom:
                                    expandedTraceSections.responseSegments
                                      ? '4px'
                                      : '0',
                                }}
                              >
                                <span
                                  style={{
                                    transform:
                                      expandedTraceSections.responseSegments
                                        ? 'rotate(90deg)'
                                        : 'rotate(0deg)',
                                    transition: 'transform 0.15s',
                                  }}
                                >
                                  &#9654;
                                </span>
                                <span
                                  style={{
                                    fontWeight: 500,
                                    color: 'var(--success)',
                                  }}
                                >
                                  Response Segments (
                                  {selectedStepTrace.responseSegments.length})
                                </span>
                              </div>
                              {expandedTraceSections.responseSegments && (
                                <div
                                  style={{
                                    display: 'flex',
                                    flexDirection: 'column',
                                    gap: '6px',
                                  }}
                                >
                                  {selectedStepTrace.responseSegments.map(
                                    (seg, i) => (
                                      <pre
                                        key={i}
                                        style={{
                                          background: 'var(--bg)',
                                          padding: '8px',
                                          borderRadius: '4px',
                                          whiteSpace: 'pre-wrap',
                                          fontSize: '11px',
                                          color: 'var(--text-muted)',
                                          maxHeight: '150px',
                                          overflow: 'auto',
                                        }}
                                      >
                                        {seg}
                                      </pre>
                                    ),
                                  )}
                                </div>
                              )}
                            </div>
                          )}

                        {/* Final Response (before output handler) */}
                        {selectedStepTrace.finalResponse && (
                          <div
                            className="trace-section"
                            style={{ marginBottom: '12px' }}
                          >
                            <div
                              className="trace-section-header"
                              onClick={() =>
                                toggleTraceSection('finalResponse')
                              }
                              style={{
                                display: 'flex',
                                alignItems: 'center',
                                gap: '6px',
                                cursor: 'pointer',
                                padding: '6px 8px',
                                background: 'var(--surface)',
                                borderRadius: '4px',
                                marginBottom:
                                  expandedTraceSections.finalResponse
                                    ? '4px'
                                    : '0',
                              }}
                            >
                              <span
                                style={{
                                  transform:
                                    expandedTraceSections.finalResponse
                                      ? 'rotate(90deg)'
                                      : 'rotate(0deg)',
                                  transition: 'transform 0.15s',
                                }}
                              >
                                &#9654;
                              </span>
                              <span
                                style={{
                                  fontWeight: 500,
                                  color: 'var(--text)',
                                }}
                              >
                                Final Response (Before Output Handler)
                              </span>
                            </div>
                            {expandedTraceSections.finalResponse && (
                              <pre
                                style={{
                                  background: 'var(--bg)',
                                  padding: '8px',
                                  borderRadius: '4px',
                                  whiteSpace: 'pre-wrap',
                                  fontSize: '11px',
                                  color: 'var(--text-muted)',
                                  maxHeight: '150px',
                                  overflow: 'auto',
                                }}
                              >
                                {selectedStepTrace.finalResponse}
                              </pre>
                            )}
                          </div>
                        )}

                        {/* Output Handler Result */}
                        {selectedStepTrace.outputHandlerResult &&
                          selectedStepTrace.outputHandlerResult !==
                            selectedStepTrace.finalResponse && (
                            <div
                              className="trace-section"
                              style={{ marginBottom: '12px' }}
                            >
                              <div
                                className="trace-section-header"
                                onClick={() =>
                                  toggleTraceSection('outputHandlerResult')
                                }
                                style={{
                                  display: 'flex',
                                  alignItems: 'center',
                                  gap: '6px',
                                  cursor: 'pointer',
                                  padding: '6px 8px',
                                  background: 'var(--surface)',
                                  borderRadius: '4px',
                                  marginBottom:
                                    expandedTraceSections.outputHandlerResult
                                      ? '4px'
                                      : '0',
                                }}
                              >
                                <span
                                  style={{
                                    transform:
                                      expandedTraceSections.outputHandlerResult
                                        ? 'rotate(90deg)'
                                        : 'rotate(0deg)',
                                    transition: 'transform 0.15s',
                                  }}
                                >
                                  &#9654;
                                </span>
                                <span
                                  style={{
                                    fontWeight: 500,
                                    color: 'var(--success)',
                                  }}
                                >
                                  Output (After Output Handler)
                                </span>
                              </div>
                              {expandedTraceSections.outputHandlerResult && (
                                <pre
                                  style={{
                                    background: 'var(--bg)',
                                    padding: '8px',
                                    borderRadius: '4px',
                                    whiteSpace: 'pre-wrap',
                                    fontSize: '11px',
                                    color: 'var(--text-muted)',
                                    maxHeight: '150px',
                                    overflow: 'auto',
                                  }}
                                >
                                  {selectedStepTrace.outputHandlerResult}
                                </pre>
                               )}
                            </div>
                          )}

                        {/* MCP Servers */}
                        {selectedStepTrace.mcpServers &&
                          selectedStepTrace.mcpServers.length > 0 && (
                            <div
                              className="trace-section"
                              style={{ marginBottom: '12px' }}
                            >
                              <div
                                className="trace-section-header"
                                onClick={() =>
                                  toggleTraceSection('mcpServers')
                                }
                                style={{
                                  display: 'flex',
                                  alignItems: 'center',
                                  gap: '6px',
                                  cursor: 'pointer',
                                  padding: '6px 8px',
                                  background: 'var(--surface)',
                                  borderRadius: '4px',
                                  marginBottom:
                                    expandedTraceSections.mcpServers
                                      ? '4px'
                                      : '0',
                                }}
                              >
                                <span
                                  style={{
                                    transform:
                                      expandedTraceSections.mcpServers
                                        ? 'rotate(90deg)'
                                        : 'rotate(0deg)',
                                    transition: 'transform 0.15s',
                                  }}
                                >
                                  &#9654;
                                </span>
                                <span
                                  style={{
                                    fontWeight: 500,
                                    color: 'var(--cyan)',
                                  }}
                                >
                                  MCP Servers (
                                  {selectedStepTrace.mcpServers.length})
                                </span>
                              </div>
                              {expandedTraceSections.mcpServers && (
                                <pre
                                  style={{
                                    background: 'var(--bg)',
                                    padding: '8px',
                                    borderRadius: '4px',
                                    whiteSpace: 'pre-wrap',
                                    fontSize: '11px',
                                    color: 'var(--text-muted)',
                                    maxHeight: '150px',
                                    overflow: 'auto',
                                  }}
                                >
                                  {selectedStepTrace.mcpServers.join('\n')}
                                </pre>
                              )}
                            </div>
                          )}

                        {/* Warnings */}
                        {selectedStepTrace.warnings &&
                          selectedStepTrace.warnings.length > 0 && (
                            <div
                              className="trace-section"
                              style={{ marginBottom: '12px' }}
                            >
                              <div
                                className="trace-section-header"
                                onClick={() =>
                                  toggleTraceSection('warnings')
                                }
                                style={{
                                  display: 'flex',
                                  alignItems: 'center',
                                  gap: '6px',
                                  cursor: 'pointer',
                                  padding: '6px 8px',
                                  background: 'var(--surface)',
                                  borderRadius: '4px',
                                  marginBottom:
                                    expandedTraceSections.warnings
                                      ? '4px'
                                      : '0',
                                }}
                              >
                                <span
                                  style={{
                                    transform:
                                      expandedTraceSections.warnings
                                        ? 'rotate(90deg)'
                                        : 'rotate(0deg)',
                                    transition: 'transform 0.15s',
                                  }}
                                >
                                  &#9654;
                                </span>
                                <span
                                  style={{
                                    fontWeight: 500,
                                    color: 'var(--error)',
                                  }}
                                >
                                  Warnings (
                                  {selectedStepTrace.warnings.length})
                                </span>
                              </div>
                              {expandedTraceSections.warnings && (
                                <pre
                                  style={{
                                    background: 'var(--bg)',
                                    padding: '8px',
                                    borderRadius: '4px',
                                    whiteSpace: 'pre-wrap',
                                    fontSize: '11px',
                                    color: 'var(--warning)',
                                    maxHeight: '150px',
                                    overflow: 'auto',
                                  }}
                                >
                                  {selectedStepTrace.warnings.join('\n')}
                                </pre>
                              )}
                            </div>
                          )}
                      </div>
                    ) : (
                      /* Events Panel (default view) */
                      <>
                        {/* Search input for events */}
                        <div
                          style={{
                            display: 'flex',
                            alignItems: 'center',
                            gap: '6px',
                            padding: '6px 8px',
                            marginBottom: '8px',
                            background: 'var(--surface)',
                            borderRadius: '4px',
                          }}
                        >
                          <Icons.Search />
                          <input
                            type="text"
                            value={eventSearchQuery}
                            onChange={(e) => setEventSearchQuery(e.target.value)}
                            placeholder="Filter events..."
                            style={{
                              flex: 1,
                              background: 'transparent',
                              border: 'none',
                              outline: 'none',
                              color: 'var(--text)',
                              fontSize: '12px',
                            }}
                          />
                          {eventSearchQuery && (
                            <button
                              onClick={() => setEventSearchQuery('')}
                              style={{
                                background: 'none',
                                border: 'none',
                                cursor: 'pointer',
                                color: 'var(--text-dim)',
                                padding: '2px',
                                display: 'flex',
                                alignItems: 'center',
                              }}
                              title="Clear search"
                            >
                              <Icons.X />
                            </button>
                          )}
                        </div>
                        {filteredStepEvents.length === 0 ? (
                          <div className="no-step-selected">
                            {eventSearchQuery
                              ? 'No events match the search'
                              : 'No events yet for this step'}
                          </div>
                        ) : (
                          filteredStepEvents.map((event, i) => (
                            <div
                              className={`step-event-item ${event.type}`}
                              key={i}
                            >
                              <span className="event-type">{event.type}</span>
                              <span
                                className="event-time"
                                style={{
                                  color: 'var(--text-dim)',
                                  marginRight: '8px',
                                }}
                              >
                                {event.timestamp}
                              </span>
                              <span
                                className="event-content"
                                style={{ color: 'var(--text-muted)' }}
                              >
                                {formatLogContent(event)}
                              </span>
                            </div>
                          ))
                        )}
                      </>
                    )}
                  </>
                ) : (
                  <div className="no-step-selected">
                    Click a step in the DAG to view its events
                  </div>
                )}
              </div>

              {/* Output/Result Panel */}
              <div className="execution-streaming" aria-live="polite">
                <div className="streaming-header">
                  {isCompleted ? <Icons.Check /> : <Icons.Terminal />}
                  <span>
                    {selectedStep
                      ? `Step Output: ${selectedStep}`
                      : isCompleted
                        ? 'Result'
                        : 'Agent Output'}
                  </span>
                  {status === 'running' && !selectedStep && (
                    <div
                      className="spinner"
                      style={{ width: '14px', height: '14px' }}
                    ></div>
                  )}
                  {/* Copy and Download buttons */}
                  {displayContent.content && (
                    <div
                      style={{
                        marginLeft: 'auto',
                        display: 'flex',
                        gap: '8px',
                      }}
                    >
                      <button
                        className="btn-icon"
                        onClick={copyContent}
                        title="Copy to clipboard"
                        style={{ padding: '4px 8px', fontSize: '12px' }}
                      >
                        <Icons.Copy />
                      </button>
                      <button
                        className="btn-icon"
                        onClick={downloadContent}
                        title="Download as file"
                        style={{ padding: '4px 8px', fontSize: '12px' }}
                      >
                        <Icons.Download />
                      </button>
                    </div>
                  )}
                </div>
                <div className="streaming-content">
                  {displayContent.content ? (
                    displayContent.content
                  ) : (
                    <span style={{ color: 'var(--text-dim)' }}>
                      {status === 'running'
                        ? selectedStep
                          ? 'No output yet for this step...'
                          : 'Running...'
                        : 'No output captured'}
                    </span>
                  )}
                  <div ref={streamingEndRef} />
                </div>
              </div>
            </div>
          </div>
        </div>
        <div className="modal-footer">
          {(status === 'running' || status === 'cancelling') && (
            <button
              className="btn btn-warning"
              onClick={handleCancel}
              disabled={status === 'cancelling'}
              style={{ marginRight: 'auto' }}
            >
              {status === 'cancelling'
                ? 'Cancelling...'
                : 'Cancel Execution'}
            </button>
          )}
          <button className="btn" onClick={onClose}>
            Close
          </button>
        </div>
      </div>
    </div>
  );
}
