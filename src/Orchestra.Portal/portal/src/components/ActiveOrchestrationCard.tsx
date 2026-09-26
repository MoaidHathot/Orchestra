import React, { useEffect, useMemo, useRef, useState } from 'react';
import { Icons, getTriggerIcon } from '../icons';
import { formatTimeAgo, formatTimeUntil, getMatchingProfiles } from '../utils';
import type { Orchestration, Profile, RunContext } from '../types';

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
  onToggleTrigger?: (orchestrationId: string, currentlyEnabled: boolean) => void;
  orchestrations?: Orchestration[];
  profiles?: Profile[];
  /** When set, the card shows a "Waiting" chip next to the status to surface
   *  that this run is paused on a HITL prompt. The Waiting Inputs modal is the
   *  canonical place to respond. */
  awaitingInput?: boolean;
}

export default function ActiveOrchestrationCard({
  execution,
  type,
  onView,
  onCancel,
  onRun,
  onToggleTrigger,
  orchestrations,
  profiles,
  awaitingInput,
}: Props): React.JSX.Element {
  const isRunning = type === 'running';
  const isManual = type === 'manual';
  const isDisabled = type === 'disabled';
  const isCancelling = execution.status === 'Cancelling';
  const orch = orchestrations?.find((o) => o.id === execution.orchestrationId);

  // Collect all unique MCPs from orchestration-level AND step-level (which includes resolved global MCPs)
  const allMcps = useMemo(() => {
    if (!orch) return [];
    const seen = new Set<string>();
    const result: { name: string; source: 'inline' | 'step' }[] = [];

    // Orchestration-level inline MCPs
    if (orch.mcps) {
      for (const mcp of orch.mcps) {
        const mcpName = typeof mcp === 'string' ? mcp : mcp.name;
        if (!seen.has(mcpName)) {
          seen.add(mcpName);
          result.push({ name: mcpName, source: 'inline' });
        }
      }
    }

    // Step-level MCPs (includes resolved global/shared MCPs)
    if (orch.steps) {
      for (const step of orch.steps) {
        if (typeof step === 'string' || !step?.mcps) continue;
        for (const mcp of step.mcps) {
          const mcpName = typeof mcp === 'string' ? mcp : mcp.name;
          if (!seen.has(mcpName)) {
            seen.add(mcpName);
            result.push({ name: mcpName, source: 'step' });
          }
        }
      }
    }

    return result;
  }, [orch]);

  // Collect all unique skill directories from steps
  const allSkillDirs = useMemo(() => {
    if (!orch?.steps) return [];
    const seen = new Set<string>();
    const result: string[] = [];
    for (const step of orch.steps) {
      if (typeof step === 'string' || !step?.skillDirectories) continue;
      for (const dir of step.skillDirectories) {
        if (!seen.has(dir)) {
          seen.add(dir);
          result.push(dir);
        }
      }
    }
    return result;
  }, [orch]);

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

  // Returns the icon + text payload for the status chip. The chip itself (the
  // `.step-status-badge` pill wrapper) provides the colored background and the
  // text color via CSS; this function only owns icon sizing.
  const getStatusLabel = (): React.JSX.Element => {
    if (isCancelling) {
      return (
        <>
          <span style={{ width: '12px', height: '12px', display: 'inline-flex' }}>
            <Icons.Spinner />
          </span>
          Cancelling
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
          <span style={{ width: '12px', height: '12px', display: 'inline-flex' }}>
            <Icons.Ban />
          </span>
          Disabled
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

  const triggerType = orch?.trigger?.type
    || (orch as unknown as { triggerType?: string })?.triggerType;
  const hasTrigger = !!triggerType && triggerType.toLowerCase() !== 'manual';

  // Tertiary actions that live in the header kebab. The kebab is the
  // touch/keyboard-friendly fallback for Run (since the primary affordance is
  // a hover-revealed chip in the bottom-right corner of the card, which
  // isn't reachable on touch without hover). Webhook URL copy is webhook-only.
  // We deliberately do NOT add a redundant "Open" item — the entire card is
  // already clickable to open the modal.
  const kebabItems: CardKebabMenuItem[] = [];
  if (!isRunning && onRun && orch) {
    const orchToRun = orch;
    kebabItems.push({
      label: 'Run',
      icon: <Icons.Play />,
      onClick: () => onRun(orchToRun),
    });
  }
  if (!isRunning && execution.triggeredBy === 'webhook' && execution.webhookUrl) {
    const webhookUrl = execution.webhookUrl;
    kebabItems.push({
      label: 'Copy webhook URL',
      icon: <Icons.Copy />,
      onClick: () => {
        navigator.clipboard.writeText(window.location.origin + webhookUrl);
      },
    });
  }

  // Context sections (Environment, Models) used to render in their own vertical
  // column; they now live as direct siblings in the resources flex-wrap row
  // alongside MCPs and Skills. buildContextSections returns the bare data so
  // the card owns the row layout.
  const contextSections = buildContextSections(orch, isRunning ? execution.runContext : undefined);

  // Profiles list is computed once per render so we can decide whether to emit
  // the labels row at all and keep the same value for the inline chip overflow.
  const matchedProfiles = profiles && orch
    ? getMatchingProfiles(profiles, orch.id, orch.tags)
    : [];

  const hasResources =
    allMcps.length > 0 ||
    allSkillDirs.length > 0 ||
    contextSections.length > 0;
  const hasLabels = (orch?.tags?.length ?? 0) > 0 || matchedProfiles.length > 0;

  // Run is no longer in the bottom action row — it's a hover-revealed chip
  // overlaid at the bottom-right of the card, plus a kebab fallback. The
  // action row is reserved for the always-visible Cancel safety affordance
  // on running cards.
  const showRun = !isRunning && onRun != null && orch != null;
  const showCancel = isRunning && onCancel != null;
  const hasActionButtons = showCancel;

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
        {/*
         * Single-row header packs four affordances into one line:
         *   [status chip] [power-icon?] [title flex:1] [waiting chip?] [kebab?]
         * - status chip   : at-a-glance state (Pending / Running / Manual / ...)
         * - power-icon    : direct toggle for triggered orchestrations
         * - title         : flex:1 + ellipsis; absorbs free space and truncates
         * - waiting chip  : HITL prompt surfaced when awaitingInput
         * - kebab         : tertiary actions (e.g., copy webhook URL); hidden
         *                   entirely when there is nothing to put inside it
         *
         * Visual distinction: status chip and power icon are small colored
         * affordances, title is bold flat text, waiting + kebab live at the
         * right edge. Side-by-side they read as "[metadata] Title [actions]".
         */}
        <div
          className="card-title-area"
          style={{ display: 'flex', alignItems: 'center', gap: '6px', minWidth: 0 }}
        >
          <span
            className={`step-status-badge ${getStatusBadgeClass()}`}
            style={{ flexShrink: 0 }}
          >
            {getStatusLabel()}
          </span>
          {!isRunning && hasTrigger && onToggleTrigger && orch && (
            <PowerIconToggle
              enabled={!isDisabled}
              onClick={(e) => {
                e.stopPropagation();
                onToggleTrigger(orch.id, !isDisabled);
              }}
            />
          )}
          <div className="card-title" style={{ flex: 1, minWidth: 0 }}>
            {execution.orchestrationName}
          </div>
          {awaitingInput && (
            <span
              className="waiting-inputs-chip"
              style={{ flexShrink: 0 }}
              title="Waiting for human input — open the Waiting Inputs panel to respond"
            >
              <Icons.Hand /> Waiting
            </span>
          )}
          <CardKebabMenu items={kebabItems} />
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
                    // Surface the current step on hover instead of as a separate row;
                    // saves ~14 px of vertical space and keeps the bar self-contained.
                    title={execution.currentStep
                      ? `Current step: ${execution.currentStep}`
                      : undefined}
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
                </div>
              )}
            </>
          ) : isManual || isDisabled ? (
            // Inline pipe-separated summary instead of a labelled 2x2 grid (~50 px saved).
            // Description (when present) renders below on a single clamped line with a
            // tooltip carrying the full text. The card-meta-item wrapper keeps the same
            // grid-row affordance for any future siblings that opt back into a grid cell.
            <div className="card-meta-item" style={{ gridColumn: '1 / -1' }}>
              <InlineMetaRow
                segments={[
                  { value: isManual ? 'Manual (no trigger)' : 'Trigger disabled', title: 'Trigger type' },
                  orch?.steps?.length
                    ? { value: `${orch.steps.length} ${orch.steps.length === 1 ? 'step' : 'steps'}`, title: 'Number of steps' }
                    : null,
                ]}
              />
              {orch?.description && (
                <div
                  className="card-description"
                  style={{ marginTop: '2px' }}
                  title={orch.description}
                >
                  {orch.description}
                </div>
              )}
            </div>
          ) : (
            // Pending (scheduler / loop / webhook) — single inline summary line.
            // Empty/zero-state fields (e.g. Never Fired, Run Count 0) are simply not pushed
            // into the segments array, so light cards stay short and heavy cards stay readable.
            <div className="card-meta-item" style={{ gridColumn: '1 / -1' }}>
              <InlineMetaRow
                segments={[
                  execution.triggeredBy
                    ? {
                        value: execution.triggeredBy,
                        title: `Trigger: ${execution.triggeredBy}`,
                        prefix: getTriggerIcon(execution.triggeredBy),
                      }
                    : null,
                  execution.status
                    ? { value: execution.status, title: 'Status' }
                    : { value: 'Scheduled', title: 'Status' },
                  (execution.stepCount ?? orch?.steps?.length)
                    ? {
                        value: `${execution.stepCount ?? orch!.steps!.length} ${(execution.stepCount ?? orch!.steps!.length) === 1 ? 'step' : 'steps'}`,
                        title: 'Number of steps',
                      }
                    : null,
                  execution.runCount && execution.runCount > 0
                    ? {
                        value: `${execution.runCount} ${execution.runCount === 1 ? 'run' : 'runs'}`,
                        title: `Run count: ${execution.runCount}`,
                      }
                    : null,
                  execution.lastFireTime
                    ? {
                        value: `last ${formatTimeAgo(execution.lastFireTime)}`,
                        title: `Last fired: ${execution.lastFireTime}`,
                      }
                    : null,
                  execution.nextFireTime
                    ? {
                        value: `next ${formatTimeUntil(execution.nextFireTime)}`,
                        title: `Next fire: ${execution.nextFireTime}`,
                      }
                    : null,
                ]}
              />
            </div>
          )}
        </div>

        {/*
         * Resources row — MCPs, Skills, Environment, Models all live on one
         * flex-wrap line as collapsible badges. Expanding any one of them
         * unfolds the detail panel inline below the badge (each component
         * owns its own expanded state). This collapses what used to be up
         * to four rows into a single visual band.
         */}
        {hasResources && (
          <div className="card-resources-row">
            {allMcps.length > 0 && <CollapsibleMcpsBadge mcps={allMcps} />}
            {allSkillDirs.length > 0 && <SkillBadge skillDirs={allSkillDirs} />}
            {contextSections.map((section) => (
              <CollapsibleContextSection
                key={section.title}
                title={section.title}
                entries={section.entries}
                isRuntime={section.isRuntime}
              />
            ))}
          </div>
        )}

        {/*
         * Labels row — tags and profiles share a single flex-wrap line; each
         * has its own overflow control ("+N more"). Visually distinct via
         * their existing .tag-chip and .profile-badge styling.
         */}
        {hasLabels && (
          <div className="card-labels-row">
            {orch?.tags && orch.tags.length > 0 && (
              <OverflowChipRow
                className="orch-tags"
                style={{ margin: 0 }}
                items={orch.tags}
                cap={3}
                renderItem={(tag) => (
                  <span key={tag} className={`tag-chip ${tag === '*' ? 'tag-wildcard' : ''}`}>
                    <Icons.Tag />{tag}
                  </span>
                )}
                moreLabel={(count) => `+${count} more`}
                moreClassName="tag-chip"
              />
            )}
            {matchedProfiles.length > 0 && (
              <OverflowChipRow
                className="orch-profiles"
                style={{ margin: 0 }}
                items={matchedProfiles}
                cap={3}
                renderItem={(p) => (
                  <span key={p.id} className={`profile-badge ${p.isActive ? 'active' : ''}`}>
                    <Icons.Shield />{p.name}
                  </span>
                )}
                moreLabel={(count) => `+${count} more`}
                moreClassName="profile-badge"
              />
            )}
          </div>
        )}

        {/*
         * Action row — reserved for the always-visible Cancel safety affordance
         * on running cards. Run is no longer rendered here; it's a hover-only
         * chip overlaid at the bottom-right (see .orch-card-run-chip below) plus
         * a kebab-menu fallback for touch/keyboard users. The wrapper is
         * skipped entirely when there is no button to render so non-running
         * cards lose the whole row at rest (no margin, no padding).
         *
         * The .card-actions class pins this row to the bottom of the card via
         * `margin-top: auto` so the Cancel button sits consistently regardless
         * of how much content lives above it.
         */}
        {hasActionButtons && (
          <div className="card-actions">
            {showCancel && (
              <button
                className="btn btn-danger btn-sm btn-card-action"
                onClick={(e: React.MouseEvent) => {
                  e.stopPropagation();
                  if (execution.executionId) {
                    onCancel!(execution.executionId);
                  }
                }}
              >
                <Icons.X /> Cancel
              </button>
            )}
          </div>
        )}
      </div>

      {/*
       * Hover-revealed Run chip — icon-only pill in the bottom-right corner,
       * absolutely positioned so it does not affect card height. Hidden at
       * rest, revealed on `.orch-card:hover`, `.orch-card:focus-within`, or
       * `.orch-card-run-chip:focus-visible`. On touch devices (no hover) the
       * chip is permanently visible via `@media (hover: none)` in App.css.
       *
       * The Run action is also exposed through the kebab menu's "Run" item,
       * so touch and keyboard users always have a discoverable path even
       * without the hover state.
       */}
      {showRun && (
        <button
          className="orch-card-run-chip"
          onClick={(e: React.MouseEvent) => {
            e.stopPropagation();
            onRun!(orch!);
          }}
          title={`Run ${execution.orchestrationName}`}
          aria-label={`Run ${execution.orchestrationName}`}
        >
          <Icons.Play />
        </button>
      )}
    </div>
  );
}

/* ── Power-icon toggle for the trigger (header-resident) ─────────────── */

/**
 * Compact `⏻` button that flips the orchestration's trigger between enabled and
 * disabled. Replaces the bottom-of-card "Enabled / Disabled" pill, freeing the
 * action row to host only the primary verb (Run or Cancel). Color cue: green
 * when enabled, dim when disabled. Click bubbles `(orchId, !enabled)` to the
 * `onToggleTrigger` handler and stops propagation so the card's onClick (which
 * opens the modal) does not fire.
 */
function PowerIconToggle({ enabled, onClick }: { enabled: boolean; onClick: (e: React.MouseEvent) => void }) {
  return (
    <button
      className={`card-power-icon ${enabled ? 'enabled' : ''}`}
      onClick={onClick}
      title={enabled ? 'Trigger enabled — click to disable' : 'Trigger disabled — click to enable'}
      aria-label={enabled ? 'Disable trigger' : 'Enable trigger'}
      aria-pressed={enabled}
    >
      <Icons.Power />
    </button>
  );
}

/* ── Kebab menu (⋮) for tertiary card actions ────────────────────────── */

interface CardKebabMenuItem {
  /** User-visible label rendered in the popover. */
  label: string;
  /** Optional leading icon, e.g. <Icons.Copy />. */
  icon?: React.ReactNode;
  /** Invoked on item click; the menu closes itself afterwards. */
  onClick: () => void;
}

/**
 * Renders a `⋮` button in the card header that opens a popover with secondary
 * actions (copy webhook URL, future items). Renders nothing when `items` is
 * empty so cards without tertiary actions stay clean.
 *
 * Closes the popover on outside-click (document `mousedown` listener) and the
 * Escape key. Each item's onClick fires and then closes the menu.
 *
 * All click handlers (button + items) stopPropagation so the card's onClick
 * never fires accidentally when interacting with the menu.
 */
function CardKebabMenu({ items }: { items: CardKebabMenuItem[] }) {
  const [open, setOpen] = useState(false);
  const wrapperRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (!open) return;

    function handleMouseDown(e: MouseEvent) {
      const node = wrapperRef.current;
      if (node && e.target instanceof Node && !node.contains(e.target)) {
        setOpen(false);
      }
    }
    function handleKeyDown(e: KeyboardEvent) {
      if (e.key === 'Escape') setOpen(false);
    }
    document.addEventListener('mousedown', handleMouseDown);
    document.addEventListener('keydown', handleKeyDown);
    return () => {
      document.removeEventListener('mousedown', handleMouseDown);
      document.removeEventListener('keydown', handleKeyDown);
    };
  }, [open]);

  if (items.length === 0) return null;

  return (
    <div className="card-kebab-wrapper" ref={wrapperRef}>
      <button
        className="card-kebab-button"
        onClick={(e: React.MouseEvent) => {
          e.stopPropagation();
          setOpen((v) => !v);
        }}
        title="More actions"
        aria-label="More actions"
        aria-haspopup="menu"
        aria-expanded={open}
      >
        <Icons.Menu />
      </button>
      {open && (
        <div className="card-kebab-popover" role="menu">
          {items.map((item, idx) => (
            <button
              key={`${idx}-${item.label}`}
              role="menuitem"
              onClick={(e: React.MouseEvent) => {
                e.stopPropagation();
                item.onClick();
                setOpen(false);
              }}
            >
              {item.icon}
              {item.label}
            </button>
          ))}
        </div>
      )}
    </div>
  );
}

/* ── Inline pipe-separated meta row ──────────────────────────────────────
 * Replaces the 2-column labelled `card-meta-grid` for pending/manual/disabled
 * cards. Renders only segments that have data, separated by middle-dots, so an
 * orchestration that has never fired isn't padded out by "Never fired · Run
 * Count 0 · ...". Each segment can carry an optional icon prefix and tooltip
 * title — the title is the verbose label/value so users can still inspect what
 * each segment means on hover. */

interface InlineMetaSegment {
  value: string;
  title?: string;
  prefix?: React.ReactNode;
}

function InlineMetaRow({ segments }: { segments: (InlineMetaSegment | null | undefined | false)[] }) {
  // Materialise once, discarding the falsy segments callers use for compact
  // conditionals like `cond ? {...} : null`. Doing it here keeps callsites tidy.
  const present = segments.filter((s): s is InlineMetaSegment => !!s);
  if (present.length === 0) return null;

  return (
    <div
      className="card-meta-value"
      style={{
        display: 'flex',
        alignItems: 'center',
        flexWrap: 'wrap',
        gap: '4px',
      }}
    >
      {present.map((seg, idx) => (
        <React.Fragment key={idx}>
          {idx > 0 && (
            <span style={{ color: 'var(--text-dim)' }} aria-hidden>
              ·
            </span>
          )}
          <span
            title={seg.title}
            style={{ display: 'inline-flex', alignItems: 'center', gap: '3px' }}
          >
            {seg.prefix}
            {seg.value}
          </span>
        </React.Fragment>
      ))}
    </div>
  );
}

/* ── Skill badge with expand/collapse ────────────────────────────────── */

function SkillBadge({ skillDirs }: { skillDirs: string[] }) {
  const [expanded, setExpanded] = useState(false);

  // No outer margin: the parent .card-resources-row owns the inter-badge gap.
  // Adding a bottom margin here would make `align-items: center` on the row
  // treat the wrapper's outer box (including margin) as the centring target,
  // which visually shifts the chip content upward relative to its siblings —
  // the "skill badge sits higher than MCPs" misalignment users saw.
  return (
    <div>
      <span
        role="button"
        onClick={(e: React.MouseEvent) => {
          e.stopPropagation();
          setExpanded((v) => !v);
        }}
        style={{
          display: 'inline-flex',
          alignItems: 'center',
          gap: '4px',
          padding: '2px 6px',
          fontSize: '10px',
          background: 'rgba(240, 136, 62, 0.15)',
          border: '1px solid rgba(240, 136, 62, 0.3)',
          borderRadius: '4px',
          color: '#f0883e',
          cursor: 'pointer',
          userSelect: 'none',
        }}
        title={expanded ? 'Click to collapse' : 'Click to show skill directories'}
      >
        <Icons.Skill />
        {skillDirs.length === 1 ? '1 skill' : `${skillDirs.length} skills`}
        <span style={{ fontSize: '8px', marginLeft: '2px', opacity: 0.7 }}>
          {expanded ? '\u25B2' : '\u25BC'}
        </span>
      </span>
      {expanded && (
        <div style={{ display: 'flex', flexDirection: 'column', gap: '3px', marginTop: '4px' }}>
          {skillDirs.map((dir) => (
            <div
              key={dir}
              style={{
                fontSize: '10px',
                color: '#ffa657',
                fontFamily: 'monospace',
                padding: '2px 6px',
                background: 'var(--bg)',
                borderRadius: '3px',
                wordBreak: 'break-all',
              }}
            >
              {dir}
            </div>
          ))}
        </div>
      )}
    </div>
  );
}

/* ── Collapsible MCPs badge ──────────────────────────────────────────── */

/**
 * Renders the full MCPs list as a single collapsed-by-default badge with a
 * count, mirroring the Skills/Environment/Models pattern. Cards used to render
 * a flex-wrap of N MCP chips inline, which could span 2-3 rows when many MCPs
 * were attached and made cards visibly heterogeneous in the same grid row.
 * Click expands the chip list (preserving the existing inline/step colour
 * distinction); click stops propagation so the card's `onView` is not fired.
 */
function CollapsibleMcpsBadge({ mcps }: { mcps: { name: string; source: 'inline' | 'step' }[] }) {
  const [expanded, setExpanded] = useState(false);
  const count = mcps.length;
  const countLabel = count === 1 ? '1 MCP' : `${count} MCPs`;

  // No outer margin — the parent .card-resources-row controls inter-badge gap.
  // See SkillBadge for the alignment rationale.
  return (
    <div>
      <span
        role="button"
        onClick={(e: React.MouseEvent) => {
          e.stopPropagation();
          setExpanded((v) => !v);
        }}
        style={{
          display: 'inline-flex',
          alignItems: 'center',
          gap: '4px',
          padding: '2px 6px',
          fontSize: '10px',
          // Match the inline-MCP chip colour so the badge reads as "this is the
          // MCPs section" even when collapsed.
          background: 'rgba(139, 92, 246, 0.15)',
          border: '1px solid rgba(139, 92, 246, 0.3)',
          borderRadius: '4px',
          color: '#a78bfa',
          cursor: 'pointer',
          userSelect: 'none',
          fontFamily: 'monospace',
        }}
        title={expanded ? 'Click to collapse MCPs' : 'Click to show MCPs'}
      >
        <span style={{ fontWeight: 600 }}>MCPs:</span>
        <span>{countLabel}</span>
        <span style={{ fontSize: '8px', marginLeft: '2px', opacity: 0.7 }}>
          {expanded ? '\u25B2' : '\u25BC'}
        </span>
      </span>
      {expanded && (
        <div style={{ display: 'flex', flexWrap: 'wrap', gap: '4px', marginTop: '4px' }}>
          {mcps.map((mcp) => (
            <span
              key={mcp.name}
              style={{
                display: 'inline-flex',
                alignItems: 'center',
                padding: '2px 6px',
                fontSize: '10px',
                background: mcp.source === 'step'
                  ? 'rgba(56, 189, 248, 0.15)'
                  : 'rgba(139, 92, 246, 0.15)',
                border: `1px solid ${mcp.source === 'step'
                  ? 'rgba(56, 189, 248, 0.3)'
                  : 'rgba(139, 92, 246, 0.3)'}`,
                borderRadius: '4px',
                color: mcp.source === 'step' ? '#38bdf8' : '#a78bfa',
              }}
              title={mcp.source === 'step' ? 'Shared/global MCP (used in steps)' : 'Inline MCP'}
            >
              {mcp.name}
            </span>
          ))}
        </div>
      )}
    </div>
  );
}

/* ── Overflow chip row (used by Tags and Profiles) ───────────────────── */

interface OverflowChipRowProps<T> {
  items: T[];
  /** Maximum number of items to render inline before collapsing the remainder. */
  cap: number;
  /** Per-item chip renderer. Should return a chip-styled element with its own
   *  key (the wrapper relies on React's reconciler from `items.map`). */
  renderItem: (item: T, index: number) => React.ReactNode;
  /** Builds the label for the "+N more" chip. Defaults to `+N more`. */
  moreLabel?: (overflowCount: number) => string;
  /** Optional class for the "+N more" chip so it picks up the host visual
   *  language (tag-chip vs profile-badge). */
  moreClassName?: string;
  className?: string;
  style?: React.CSSProperties;
}

/**
 * Renders the first `cap` items as chips. If there are more, appends a single
 * `+N more` chip that, when clicked, reveals the rest inline. A second click
 * collapses again. The "+N more" chip stops click propagation so the card's
 * `onView` doesn't fire when users expand a long tag list.
 */
function OverflowChipRow<T>({
  items,
  cap,
  renderItem,
  moreLabel = (n) => `+${n} more`,
  moreClassName,
  className,
  style,
}: OverflowChipRowProps<T>) {
  const [expanded, setExpanded] = useState(false);
  const overflow = Math.max(0, items.length - cap);
  const shown = expanded || overflow === 0 ? items : items.slice(0, cap);

  return (
    <div className={className} style={style}>
      {shown.map((item, idx) => renderItem(item, idx))}
      {overflow > 0 && (
        <span
          role="button"
          className={moreClassName}
          onClick={(e: React.MouseEvent) => {
            e.stopPropagation();
            setExpanded((v) => !v);
          }}
          style={{ cursor: 'pointer', userSelect: 'none' }}
          title={expanded ? 'Click to collapse' : `Show ${overflow} more`}
        >
          {expanded ? '\u25B2 less' : moreLabel(overflow)}
        </span>
      )}
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

interface ContextSectionData {
  title: string;
  entries: { key: string; value: string; color?: string }[];
  /** Marks the Environment section as runtime-resolved (vs. definition-time refs). */
  isRuntime: boolean;
}

/**
 * Collects the per-orchestration context sections (Environment, Models) that the
 * card surfaces as collapsible badges. Returns an empty array when there is
 * nothing to show so the caller can omit the resources row entirely.
 *
 * Returning data (not JSX) lets the card place each section as a direct sibling
 * inside the single flex-wrap "resources" row alongside MCPs and Skills, rather
 * than nesting them in their own vertical column wrapper.
 */
function buildContextSections(
  orch: Orchestration | undefined,
  runContext: RunContext | undefined,
): ContextSectionData[] {
  const hasRunContext = !!runContext;
  const sections: ContextSectionData[] = [];

  // Environment variables
  if (hasRunContext && runContext!.accessedEnvironmentVariables) {
    const envEntries = Object.entries(runContext!.accessedEnvironmentVariables)
      .map(([k, v]) => ({
        key: k,
        value: v !== null ? v : '(not set)',
        color: v !== null ? '#7ee787' : '#f85149',
      }));
    if (envEntries.length > 0) {
      sections.push({ title: 'Environment', entries: envEntries, isRuntime: true });
    }
  } else if (!hasRunContext && orch?.referencedEnvVars && orch.referencedEnvVars.length > 0) {
    sections.push({
      title: 'Environment',
      entries: orch.referencedEnvVars.map(v => ({
        key: v,
        value: '{{env.' + v + '}}',
        color: '#d2a8ff',
      })),
      isRuntime: false,
    });
  }

  // Models (definition cards only; running cards expose model in step details)
  if (!hasRunContext && orch?.models && orch.models.length > 0) {
    sections.push({
      title: 'Models',
      entries: orch.models.map(m => ({ key: m, value: '', color: '#ffa657' })),
      isRuntime: false,
    });
  }

  return sections;
}

/* ── Collapsible badge for a single context section ─────────────────────── */

interface CollapsibleContextSectionProps {
  title: string;
  entries: { key: string; value: string; color?: string }[];
  /** Marks the Environment section as runtime-resolved (vs. definition-time refs). */
  isRuntime: boolean;
}

/**
 * Renders a single context section (Environment / Models / ...) as a collapsed-by-default
 * badge. Clicking the badge toggles the entries panel. Visual styling mirrors the existing
 * <c>SkillBadge</c> so users get a consistent "click chevron to expand" affordance across
 * the card.
 */
function CollapsibleContextSection({ title, entries, isRuntime }: CollapsibleContextSectionProps) {
  const [expanded, setExpanded] = useState(false);

  // Per-section colour theme — distinct from MCPs/Skills so users can scan a dense card
  // and tell sections apart at a glance even when all are collapsed.
  const theme = title === 'Environment'
    ? { color: '#7ee787', bg: 'rgba(126, 231, 135, 0.12)', border: 'rgba(126, 231, 135, 0.3)' }
    : title === 'Models'
      ? { color: '#ffa657', bg: 'rgba(255, 166, 87, 0.12)', border: 'rgba(255, 166, 87, 0.3)' }
      : { color: '#a78bfa', bg: 'rgba(167, 139, 250, 0.12)', border: 'rgba(167, 139, 250, 0.3)' };

  // Pluralise the count noun so the badge reads naturally: "1 model" / "3 models".
  const noun = title === 'Environment' ? 'env var' : title === 'Models' ? 'model' : 'item';
  const count = entries.length;
  const countLabel = count === 1 ? `1 ${noun}` : `${count} ${noun}s`;

  return (
    <div>
      <span
        role="button"
        onClick={(e: React.MouseEvent) => {
          e.stopPropagation();
          setExpanded((v) => !v);
        }}
        style={{
          display: 'inline-flex',
          alignItems: 'center',
          gap: '4px',
          padding: '2px 6px',
          fontSize: '10px',
          background: theme.bg,
          border: `1px solid ${theme.border}`,
          borderRadius: '4px',
          color: theme.color,
          cursor: 'pointer',
          userSelect: 'none',
          fontFamily: 'monospace',
        }}
        title={expanded ? `Click to collapse ${title.toLowerCase()}` : `Click to show ${title.toLowerCase()}`}
      >
        <span style={{ fontWeight: 600 }}>{title}:</span>
        <span>{countLabel}</span>
        {isRuntime && (
          <span style={{ opacity: 0.7, fontWeight: 'normal' }}>(runtime)</span>
        )}
        <span style={{ fontSize: '8px', marginLeft: '2px', opacity: 0.7 }}>
          {expanded ? '\u25B2' : '\u25BC'}
        </span>
      </span>

      {expanded && (
        <div
          style={{
            marginTop: '4px',
            padding: '6px 8px',
            background: 'var(--bg)',
            borderRadius: '6px',
            fontSize: '11px',
            fontFamily: 'monospace',
          }}
        >
          {title === 'Models' ? (
            <div style={{ display: 'flex', flexWrap: 'wrap', gap: '4px' }}>
              {entries.map(e => (
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
            <>
              {entries.slice(0, 5).map(e => (
                <ContextRow key={e.key} label={e.key} value={e.value} color={e.color} />
              ))}
              {entries.length > 5 && (
                <div style={{ color: 'var(--text-dim)', fontSize: '10px' }}>
                  +{entries.length - 5} more...
                </div>
              )}
            </>
          )}
        </div>
      )}
    </div>
  );
}
