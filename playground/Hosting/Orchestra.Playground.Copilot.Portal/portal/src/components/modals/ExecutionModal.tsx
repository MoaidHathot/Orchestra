import React, { useState, useEffect, useCallback, useRef, useMemo } from 'react';
import type { Orchestration, StepEvent, TraceData, Step, StepMcpRef } from '../../types';
import { Icons } from '../../icons';
import { renderExecutionDag } from '../../mermaid';
import { formatLogContent } from '../../formatLogContent';
import { useFocusTrap } from '../../hooks/useFocusTrap';

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
  | 'outputHandlerResult';

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
  onClose,
  onCancel,
}: Props): React.JSX.Element {
  const trapRef = useFocusTrap<HTMLDivElement>(open, onClose);
  const dagRef = useRef<HTMLDivElement | null>(null);
  const streamingEndRef = useRef<HTMLDivElement | null>(null);
  const [selectedStep, setSelectedStep] = useState<string | null>(null);
  const [showTracePanel, setShowTracePanel] = useState(true);
  const [expandedTraceSections, setExpandedTraceSections] = useState<
    Record<TraceSectionKey, boolean>
  >({} as Record<TraceSectionKey, boolean>);

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
      navigator.clipboard.writeText(displayContent.content);
    }
  }, [displayContent.content]);

  // Download content as file
  const downloadContent = useCallback(() => {
    if (displayContent.content) {
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
    }
  }, [displayContent.content, selectedStep, orchestration?.name]);

  // Render DAG when orchestration or step statuses change.
  // NOTE: `selectedStep` is intentionally excluded from deps — including it
  // caused a full SVG re-render on every click, which destroyed event
  // listeners mid-propagation and could trigger unwanted navigation.
  // Selected-step highlighting is handled via direct DOM manipulation below.
  useEffect(() => {
    if (open && dagRef.current && orchestration) {
      renderExecutionDag(
        orchestration,
        stepStatuses || {},
        dagRef.current,
        setSelectedStep,
      );
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open, orchestration, stepStatuses]);

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
              <span className="text-completed-early">Completed Early</span>
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
          <div className="execution-modal-content">
            {/* DAG Section */}
            <div className="execution-dag-section" ref={dagRef}>
              {!orchestration && (
                <div className="empty-state">
                  <div className="spinner"></div>
                  <div className="empty-text" style={{ marginTop: '8px' }}>
                    Loading orchestration...
                  </div>
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
                      </div>
                      {/* DEBUG: Show trace availability */}
                      <span
                        style={{ fontSize: '10px', color: 'var(--text-dim)' }}
                      >
                        [trace: {selectedStepTrace ? 'YES' : 'NO'}, keys:{' '}
                        {stepTraces
                          ? Object.keys(stepTraces).join(',')
                          : 'null'}
                        ]
                      </span>
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
                      </div>
                    ) : (
                      /* Events Panel (default view) */
                      <>
                        {selectedStepEvents.length === 0 ? (
                          <div className="no-step-selected">
                            No events yet for this step
                          </div>
                        ) : (
                          selectedStepEvents.map((event, i) => (
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
