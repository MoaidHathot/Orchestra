import React, { useState, useEffect } from 'react';
import type { ActorStream } from '../types';
import { actorColor, actorBackgroundColor } from '../actorColors';

interface Props {
  stream: ActorStream;
  /**
   * When provided, renders an indentation rail to the left so nested sub-agents
   * (depth 2+) visually stair-step inside their parent's card.
   */
  depth?: number;
}

function StatusBadge({ status }: { status: ActorStream['status'] }): React.JSX.Element {
  const label = status ?? 'running';
  const className = `subagent-status subagent-status--${label}`;
  return <span className={className}>{label}</span>;
}

function formatElapsed(startedAt: string, completedAt?: string): string {
  const start = new Date(startedAt).getTime();
  const end = completedAt ? new Date(completedAt).getTime() : Date.now();
  const ms = Math.max(0, end - start);
  if (ms < 1000) return `${ms}ms`;
  if (ms < 60_000) return `${(ms / 1000).toFixed(1)}s`;
  const m = Math.floor(ms / 60_000);
  const s = Math.floor((ms % 60_000) / 1000);
  return `${m}m ${s}s`;
}

/**
 * Inline card rendering a single actor's (sub-agent or main) live activity:
 * response chunks, optional reasoning subsection (dim, collapsed by default),
 * and lifecycle/tool events. Auto-collapses on completion, but the user can
 * always re-expand. Color is derived deterministically from the agent name.
 */
export default function SubagentCard({ stream, depth }: Props): React.JSX.Element {
  const isRunning = stream.status === 'running' || stream.status === undefined;
  const isMain = stream.actor === null;

  // Default expansion: expanded while running; auto-collapse on completion.
  const [expanded, setExpanded] = useState(isRunning);
  const [reasoningExpanded, setReasoningExpanded] = useState(false);

  // Auto-collapse on completion, but only if the user hasn't manually toggled.
  // We track the previous status so transitions trigger the collapse exactly once.
  const [prevStatus, setPrevStatus] = useState(stream.status);
  useEffect(() => {
    if (prevStatus !== stream.status) {
      if (!isRunning && expanded) {
        setExpanded(false);
      }
      setPrevStatus(stream.status);
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [stream.status]);

  // Live elapsed counter — re-render every second while running.
  const [, force] = useState(0);
  useEffect(() => {
    if (!isRunning) return;
    const id = window.setInterval(() => force(n => n + 1), 1000);
    return () => window.clearInterval(id);
  }, [isRunning]);

  const agentName = isMain ? 'main' : stream.actor!.agentName;
  const displayName = isMain
    ? 'Main agent'
    : (stream.actor!.displayName ?? stream.actor!.agentName);
  const color = isMain ? 'var(--text-dim)' : actorColor(agentName);
  const bg = isMain ? 'transparent' : actorBackgroundColor(agentName);
  const indentDepth = depth ?? stream.actor?.depth ?? 0;

  const headerStyle: React.CSSProperties = {
    borderLeft: isMain ? 'none' : `3px solid ${color}`,
    background: bg,
  };

  return (
    <div
      className={`subagent-card subagent-card--depth-${indentDepth}`}
      style={{ marginLeft: `${indentDepth * 16}px` }}
      data-actor={agentName}
      data-status={stream.status ?? 'running'}
    >
      <button
        type="button"
        className="subagent-card-header"
        style={headerStyle}
        onClick={() => setExpanded(e => !e)}
        aria-expanded={expanded}
      >
        <span className="subagent-dot" style={{ background: color }} aria-hidden="true" />
        <span className="subagent-name">{displayName}</span>
        {!isMain && stream.actor!.depth > 1 && (
          <span className="subagent-depth" title="Nesting depth">
            depth {stream.actor!.depth}
          </span>
        )}
        <StatusBadge status={stream.status} />
        <span className="subagent-elapsed" title="Elapsed">
          {formatElapsed(stream.startedAt, stream.completedAt)}
        </span>
        <span className="subagent-toggle" aria-hidden="true">
          {expanded ? '▾' : '▸'}
        </span>
      </button>

      {expanded && (
        <div className="subagent-card-body">
          {stream.errorMessage && (
            <div className="subagent-error">{stream.errorMessage}</div>
          )}

          {stream.content && (
            <div className="subagent-section subagent-response">
              <div className="subagent-section-label">Response</div>
              <pre className="subagent-content">{stream.content}</pre>
            </div>
          )}

          {stream.reasoning && (
            <div className="subagent-section subagent-reasoning-wrap">
              <button
                type="button"
                className="subagent-reasoning-toggle"
                onClick={() => setReasoningExpanded(v => !v)}
                aria-expanded={reasoningExpanded}
              >
                <span aria-hidden="true">
                  {reasoningExpanded ? '▾' : '▸'}
                </span>
                Reasoning ({stream.reasoning.length} chars)
              </button>
              {reasoningExpanded && (
                <pre className="subagent-reasoning">{stream.reasoning}</pre>
              )}
            </div>
          )}

          {stream.events.length > 0 && (
            <div className="subagent-section subagent-events">
              <div className="subagent-section-label">
                Tool calls &amp; events ({stream.events.length})
              </div>
              <ul className="subagent-event-list">
                {stream.events.map((evt, i) => (
                  <li key={`${evt.type}-${i}`} className={`subagent-event subagent-event--${evt.type}`}>
                    <span className="subagent-event-type">{evt.type}</span>
                    {evt.toolName && (
                      <span className="subagent-event-tool">{evt.toolName}</span>
                    )}
                    {evt.error && (
                      <span className="subagent-event-error">{evt.error}</span>
                    )}
                  </li>
                ))}
              </ul>
            </div>
          )}

          {!stream.content && !stream.reasoning && stream.events.length === 0 && (
            <div className="subagent-empty">
              {isRunning ? 'Waiting for output…' : 'No output produced'}
            </div>
          )}
        </div>
      )}
    </div>
  );
}
