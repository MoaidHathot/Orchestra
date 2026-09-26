import { describe, it, expect, vi } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import '@testing-library/jest-dom';
import ActiveOrchestrationCard, { CardExecution } from './ActiveOrchestrationCard';
import type { Orchestration, McpConfig, Profile } from '../types';

// ── Mocks ────────────────────────────────────────────────────────────────────

// Minimal stub for icons — they're SVGs; we just need *something* renderable.
vi.mock('../icons', () => ({
  Icons: {
    Spinner: () => <span data-testid="icon-spinner" />,
    Clock: () => <span data-testid="icon-clock" />,
    Eye: () => <span data-testid="icon-eye" />,
    Play: () => <span data-testid="icon-play" />,
    X: () => <span data-testid="icon-x" />,
    Copy: () => <span data-testid="icon-copy" />,
    Tag: () => <span data-testid="icon-tag" />,
    Shield: () => <span data-testid="icon-shield" />,
    Ban: () => <span data-testid="icon-ban" />,
    Skill: () => <span data-testid="icon-skill" />,
    Hand: () => <span data-testid="icon-hand" />,
    Power: () => <span data-testid="icon-power" />,
    Menu: () => <span data-testid="icon-menu" />,
  },
  getTriggerIcon: () => <span data-testid="icon-trigger" />,
}));

// ── Helpers ──────────────────────────────────────────────────────────────────

const baseExecution: CardExecution = {
  orchestrationId: 'orch-1',
  orchestrationName: 'Test Orchestration',
};

const noop = () => {};

function renderCard(overrides: {
  execution?: Partial<CardExecution>;
  type?: 'running' | 'pending' | 'manual' | 'disabled';
  orchestrations?: Orchestration[];
  profiles?: Profile[];
  onView?: typeof noop;
  onCancel?: (id: string) => void;
  onRun?: (orch: Orchestration) => void;
  onToggleTrigger?: (orchestrationId: string, currentlyEnabled: boolean) => void;
  awaitingInput?: boolean;
} = {}) {
  const execution = { ...baseExecution, ...overrides.execution };
  return render(
    <ActiveOrchestrationCard
      execution={execution}
      type={overrides.type ?? 'pending'}
      onView={overrides.onView ?? noop}
      onCancel={overrides.onCancel}
      onRun={overrides.onRun}
      onToggleTrigger={overrides.onToggleTrigger}
      orchestrations={overrides.orchestrations}
      profiles={overrides.profiles}
      awaitingInput={overrides.awaitingInput}
    />,
  );
}

// ── MCP rendering ────────────────────────────────────────────────────────────

describe('ActiveOrchestrationCard – MCP rendering', () => {
  it('renders MCP names when mcps are McpConfig objects', () => {
    const orchestrations: Orchestration[] = [
      {
        id: 'orch-1',
        name: 'Test',
        mcps: [
          { name: 'github-mcp', type: 'stdio' },
          { name: 'slack-mcp', type: 'sse' },
        ] as McpConfig[],
      },
    ];

    renderCard({ orchestrations });

    // MCPs are collapsed-by-default behind a count badge — click to expose the
    // chip list before asserting on chip names.
    fireEvent.click(screen.getByText('MCPs:'));
    expect(screen.getByText('github-mcp')).toBeInTheDocument();
    expect(screen.getByText('slack-mcp')).toBeInTheDocument();
  });

  it('renders MCP names when mcps are raw strings (forward-compat)', () => {
    const orchestrations: Orchestration[] = [
      {
        id: 'orch-1',
        name: 'Test',
        // Simulating the old API shape where mcps was string[]
        mcps: ['raw-string-mcp', 'another-mcp'] as unknown as McpConfig[],
      },
    ];

    renderCard({ orchestrations });

    fireEvent.click(screen.getByText('MCPs:'));
    expect(screen.getByText('raw-string-mcp')).toBeInTheDocument();
    expect(screen.getByText('another-mcp')).toBeInTheDocument();
  });

  it('renders MCP names when mcps is a mix of strings and objects', () => {
    const orchestrations: Orchestration[] = [
      {
        id: 'orch-1',
        name: 'Test',
        mcps: [
          'string-mcp',
          { name: 'object-mcp', type: 'stdio' },
        ] as unknown as McpConfig[],
      },
    ];

    renderCard({ orchestrations });

    fireEvent.click(screen.getByText('MCPs:'));
    expect(screen.getByText('string-mcp')).toBeInTheDocument();
    expect(screen.getByText('object-mcp')).toBeInTheDocument();
  });

  it('does not render MCP section when mcps is empty', () => {
    const orchestrations: Orchestration[] = [
      { id: 'orch-1', name: 'Test', mcps: [] },
    ];

    renderCard({ orchestrations });

    // The collapsed badge uses "MCPs:" (with colon); a stale assertion on plain "MCPs"
    // could pass even when the badge is rendered. Use the exact badge label.
    expect(screen.queryByText('MCPs:')).not.toBeInTheDocument();
  });

  it('does not render MCP section when mcps is undefined', () => {
    const orchestrations: Orchestration[] = [
      { id: 'orch-1', name: 'Test' },
    ];

    renderCard({ orchestrations });

    expect(screen.queryByText('MCPs:')).not.toBeInTheDocument();
  });

  it('does not render MCP section when no orchestration matches', () => {
    const orchestrations: Orchestration[] = [
      { id: 'orch-999', name: 'Other', mcps: [{ name: 'hidden-mcp' }] },
    ];

    renderCard({ orchestrations });

    expect(screen.queryByText('hidden-mcp')).not.toBeInTheDocument();
    // Also verify the badge itself is absent — the card has no matching orchestration.
    expect(screen.queryByText('MCPs:')).not.toBeInTheDocument();
  });
});

// ── Tags rendering ───────────────────────────────────────────────────────────

describe('ActiveOrchestrationCard – Tags rendering', () => {
  it('renders tags from matching orchestration', () => {
    const orchestrations: Orchestration[] = [
      { id: 'orch-1', name: 'Test', tags: ['production', 'monitoring'] },
    ];

    renderCard({ orchestrations });

    expect(screen.getByText('production')).toBeInTheDocument();
    expect(screen.getByText('monitoring')).toBeInTheDocument();
  });

  it('does not render tags section when tags is empty', () => {
    const orchestrations: Orchestration[] = [
      { id: 'orch-1', name: 'Test', tags: [] },
    ];

    const { container } = renderCard({ orchestrations });

    expect(container.querySelector('.orch-tags')).not.toBeInTheDocument();
  });
});

// ── Status badges ────────────────────────────────────────────────────────────

describe('ActiveOrchestrationCard – Status display', () => {
  it('shows Running status for running type', () => {
    renderCard({
      type: 'running',
      execution: { executionId: 'exec-1', status: 'Running', startedAt: new Date().toISOString() },
    });

    expect(screen.getByText('Running')).toBeInTheDocument();
  });

  it('shows Pending status for pending type', () => {
    renderCard({ type: 'pending' });

    expect(screen.getByText('Pending')).toBeInTheDocument();
  });

  it('shows Cancelling status when status is Cancelling', () => {
    renderCard({
      type: 'running',
      execution: { executionId: 'exec-1', status: 'Cancelling', startedAt: new Date().toISOString() },
    });

    expect(screen.getByText('Cancelling')).toBeInTheDocument();
  });
});

// ── Interactions ─────────────────────────────────────────────────────────────

describe('ActiveOrchestrationCard – Interactions', () => {
  it('calls onView when card is clicked', () => {
    const onView = vi.fn();
    const { container } = renderCard({ onView });

    fireEvent.click(container.querySelector('.orch-card')!);

    expect(onView).toHaveBeenCalledTimes(1);
    expect(onView).toHaveBeenCalledWith(
      expect.objectContaining({ orchestrationId: 'orch-1' }),
      undefined, // no matching orch
    );
  });

  it('calls onView with matched orchestration when card is clicked', () => {
    // The explicit "View" button was removed in favour of the whole card being
    // clickable (less visual clutter; the card's hover state signals tap-ability).
    // The matched orchestration must still be passed through so the modal can
    // render the full definition view.
    const onView = vi.fn();
    const orch: Orchestration = { id: 'orch-1', name: 'Test' };
    const { container } = renderCard({ onView, orchestrations: [orch] });

    fireEvent.click(container.querySelector('.orch-card')!);

    expect(onView).toHaveBeenCalledWith(
      expect.objectContaining({ orchestrationId: 'orch-1' }),
      expect.objectContaining({ id: 'orch-1' }),
    );
  });

  it('calls onCancel when Cancel button is clicked on a running card', () => {
    const onCancel = vi.fn();
    renderCard({
      type: 'running',
      execution: { executionId: 'exec-123', status: 'Running', startedAt: new Date().toISOString() },
      onCancel,
    });

    fireEvent.click(screen.getByText('Cancel'));

    expect(onCancel).toHaveBeenCalledWith('exec-123');
  });

  it('calls onRun when the hover Run chip is clicked on a pending card', () => {
    // The visible "Run" verb is now an icon-only chip overlaid in the
    // bottom-right corner of the card (hidden at rest via CSS, revealed on
    // hover/focus). We assert the DOM presence + click semantics; the CSS
    // hover-reveal is verified visually and isn't testable in jsdom.
    const onRun = vi.fn();
    const orch: Orchestration = { id: 'orch-1', name: 'Test' };

    const { container } = renderCard({
      type: 'pending',
      orchestrations: [orch],
      onRun,
    });

    const chip = container.querySelector('.orch-card-run-chip') as HTMLElement | null;
    expect(chip).not.toBeNull();
    fireEvent.click(chip!);

    expect(onRun).toHaveBeenCalledWith(expect.objectContaining({ id: 'orch-1' }));
  });
});

// ── Progress bar ─────────────────────────────────────────────────────────────

describe('ActiveOrchestrationCard – Progress bar', () => {
  it('shows progress info for running orchestration with steps', () => {
    renderCard({
      type: 'running',
      execution: {
        executionId: 'exec-1',
        status: 'Running',
        startedAt: new Date().toISOString(),
        totalSteps: 5,
        completedSteps: 2,
      },
    });

    expect(screen.getByText('2/5 steps')).toBeInTheDocument();
  });

  it('shows current step name as a tooltip on the progress bar', () => {
    // We moved the current-step name out of a separate subtitle row into the bar's
    // `title=` so cards stay short while still being inspectable on hover.
    const { container } = renderCard({
      type: 'running',
      execution: {
        executionId: 'exec-1',
        status: 'Running',
        startedAt: new Date().toISOString(),
        totalSteps: 5,
        completedSteps: 2,
        currentStep: 'analyze-data',
      },
    });

    // The step name must NOT appear as standalone visible text on the card.
    expect(screen.queryByText('analyze-data')).not.toBeInTheDocument();
    // It is reachable via the progress bar's tooltip.
    const tooltipHost = container.querySelector('[title="Current step: analyze-data"]');
    expect(tooltipHost).not.toBeNull();
  });
});

// ── Manual card type ──────────────────────────────────────────────────────────

describe('ActiveOrchestrationCard – Manual type', () => {
  it('renders "Manual" status label', () => {
    renderCard({ type: 'manual' });
    expect(screen.getByText('Manual')).toBeInTheDocument();
  });

  it('shows "Manual (no trigger)" in the type meta', () => {
    renderCard({ type: 'manual' });
    expect(screen.getByText('Manual (no trigger)')).toBeInTheDocument();
  });

  it('exposes Run via the hover chip for manual cards', () => {
    // Manual cards (no trigger) still allow ad-hoc runs. The Run affordance
    // is the same hover chip used on triggered definition cards.
    const onRun = vi.fn();
    const orchestrations: Orchestration[] = [
      { id: 'orch-1', name: 'Test' },
    ];
    const { container } = renderCard({ type: 'manual', orchestrations, onRun });
    expect(container.querySelector('.orch-card-run-chip')).not.toBeNull();
  });

  it('renders with reduced opacity for disabled cards', () => {
    const { container } = renderCard({ type: 'disabled' });
    const card = container.querySelector('.orch-card');
    expect(card).toHaveStyle({ opacity: '0.6' });
  });
});

// ── Disabled card type ────────────────────────────────────────────────────────

describe('ActiveOrchestrationCard – Disabled type', () => {
  it('renders "Disabled" status label', () => {
    renderCard({ type: 'disabled' });
    expect(screen.getByText('Disabled')).toBeInTheDocument();
  });

  it('shows "Trigger disabled" in the type meta', () => {
    renderCard({ type: 'disabled' });
    expect(screen.getByText('Trigger disabled')).toBeInTheDocument();
  });

  it('has orch-card-disabled class', () => {
    const { container } = renderCard({ type: 'disabled' });
    const card = container.querySelector('.orch-card-disabled');
    expect(card).not.toBeNull();
  });
});

// ── Environment / Models collapsible badges ───────────────────────────────────

describe('ActiveOrchestrationCard – Environment & Models collapse-by-default', () => {
  it('renders Environment badge collapsed by default with a count, hiding the entries', () => {
    // Cards used to stretch vertically when an orchestration referenced many env vars,
    // forcing the whole grid row to match the tallest card. Collapsed-by-default keeps
    // every card short until the user opts in.
    const orchestrations: Orchestration[] = [
      { id: 'orch-1', name: 'Test', referencedEnvVars: ['HOME', 'USER', 'PATH'] },
    ];

    renderCard({ orchestrations });

    // Badge header is visible with a pluralised count.
    expect(screen.getByText('Environment:')).toBeInTheDocument();
    expect(screen.getByText('3 env vars')).toBeInTheDocument();
    // Individual entries (the rendered key names) must NOT appear until expanded.
    expect(screen.queryByText('HOME:')).not.toBeInTheDocument();
    expect(screen.queryByText('USER:')).not.toBeInTheDocument();
    expect(screen.queryByText('PATH:')).not.toBeInTheDocument();
  });

  it('expands Environment entries when the badge is clicked', () => {
    const orchestrations: Orchestration[] = [
      { id: 'orch-1', name: 'Test', referencedEnvVars: ['HOME', 'USER'] },
    ];

    renderCard({ orchestrations });

    // Click the Environment badge to expand. Click the badge header text (the count
    // label sits inside the same clickable span via event bubbling).
    fireEvent.click(screen.getByText('Environment:'));

    // Now the entries are visible.
    expect(screen.getByText('HOME:')).toBeInTheDocument();
    expect(screen.getByText('USER:')).toBeInTheDocument();
  });

  it('singularises the count noun when only one env var is referenced', () => {
    const orchestrations: Orchestration[] = [
      { id: 'orch-1', name: 'Test', referencedEnvVars: ['ONLY_ONE'] },
    ];

    renderCard({ orchestrations });

    // Singular form when count is 1 — keeps the badge text readable instead of "1 env vars".
    expect(screen.getByText('1 env var')).toBeInTheDocument();
  });

  it('renders Models badge collapsed by default, hiding the model chips', () => {
    const orchestrations: Orchestration[] = [
      { id: 'orch-1', name: 'Test', models: ['gpt-5.4', 'claude-opus-4.6'] },
    ];

    renderCard({ orchestrations });

    expect(screen.getByText('Models:')).toBeInTheDocument();
    expect(screen.getByText('2 models')).toBeInTheDocument();
    // Model chips are hidden until the user clicks the badge.
    expect(screen.queryByText('gpt-5.4')).not.toBeInTheDocument();
    expect(screen.queryByText('claude-opus-4.6')).not.toBeInTheDocument();
  });

  it('expands Models chips when the badge is clicked', () => {
    const orchestrations: Orchestration[] = [
      { id: 'orch-1', name: 'Test', models: ['gpt-5.4', 'claude-opus-4.6'] },
    ];

    renderCard({ orchestrations });

    fireEvent.click(screen.getByText('Models:'));

    expect(screen.getByText('gpt-5.4')).toBeInTheDocument();
    expect(screen.getByText('claude-opus-4.6')).toBeInTheDocument();
  });

  it('toggles back to collapsed when the badge is clicked twice', () => {
    const orchestrations: Orchestration[] = [
      { id: 'orch-1', name: 'Test', models: ['gpt-5.4'] },
    ];

    renderCard({ orchestrations });

    fireEvent.click(screen.getByText('Models:'));
    expect(screen.getByText('gpt-5.4')).toBeInTheDocument();

    fireEvent.click(screen.getByText('Models:'));
    // Second click collapses again — verifies the toggle is symmetric and `useState`
    // is wired both ways.
    expect(screen.queryByText('gpt-5.4')).not.toBeInTheDocument();
  });

  it('does NOT bubble badge clicks up to the card onView handler', () => {
    // The card itself opens the modal on click. Toggling an inline badge must NOT
    // also open the modal — otherwise users get an unwanted modal pop every time
    // they expand a section.
    const onView = vi.fn();
    const orchestrations: Orchestration[] = [
      { id: 'orch-1', name: 'Test', models: ['gpt-5.4'] },
    ];

    renderCard({ orchestrations, onView });

    fireEvent.click(screen.getByText('Models:'));

    expect(onView).not.toHaveBeenCalled();
  });

  it('renders both Environment and Models badges independently when both are present', () => {
    const orchestrations: Orchestration[] = [
      {
        id: 'orch-1',
        name: 'Test',
        referencedEnvVars: ['API_KEY'],
        models: ['gpt-5.4', 'claude-opus-4.6'],
      },
    ];

    renderCard({ orchestrations });

    // Both badges render side-by-side in collapsed state.
    expect(screen.getByText('Environment:')).toBeInTheDocument();
    expect(screen.getByText('Models:')).toBeInTheDocument();

    // Expanding one section does not affect the other.
    fireEvent.click(screen.getByText('Environment:'));
    expect(screen.getByText('API_KEY:')).toBeInTheDocument();
    // Models still collapsed.
    expect(screen.queryByText('gpt-5.4')).not.toBeInTheDocument();
  });

  it('renders nothing when neither Environment nor Models data is present', () => {
    // No referencedEnvVars and no models on the orchestration → no badges should render.
    const orchestrations: Orchestration[] = [
      { id: 'orch-1', name: 'Test' },
    ];

    renderCard({ orchestrations });

    expect(screen.queryByText('Environment:')).not.toBeInTheDocument();
    expect(screen.queryByText('Models:')).not.toBeInTheDocument();
  });
});

// ── MCPs collapsible badge (A2) ───────────────────────────────────────────────

describe('ActiveOrchestrationCard – MCPs collapsible badge', () => {
  it('renders MCPs as a collapsed badge by default, hiding individual chips', () => {
    // Cards with many MCPs used to flex-wrap to multiple rows. Collapsing keeps the
    // first visual height to a single badge.
    const orchestrations: Orchestration[] = [
      {
        id: 'orch-1',
        name: 'Test',
        mcps: [{ name: 'mcp-a' }, { name: 'mcp-b' }, { name: 'mcp-c' }],
      },
    ];

    renderCard({ orchestrations });

    expect(screen.getByText('MCPs:')).toBeInTheDocument();
    expect(screen.getByText('3 MCPs')).toBeInTheDocument();
    // Individual MCP chip names are hidden until expanded.
    expect(screen.queryByText('mcp-a')).not.toBeInTheDocument();
    expect(screen.queryByText('mcp-b')).not.toBeInTheDocument();
    expect(screen.queryByText('mcp-c')).not.toBeInTheDocument();
  });

  it('expands MCP chips when the badge is clicked', () => {
    const orchestrations: Orchestration[] = [
      {
        id: 'orch-1',
        name: 'Test',
        mcps: [{ name: 'mcp-a' }, { name: 'mcp-b' }],
      },
    ];

    renderCard({ orchestrations });

    fireEvent.click(screen.getByText('MCPs:'));

    expect(screen.getByText('mcp-a')).toBeInTheDocument();
    expect(screen.getByText('mcp-b')).toBeInTheDocument();
  });

  it('singularises the badge count when only one MCP is attached', () => {
    const orchestrations: Orchestration[] = [
      { id: 'orch-1', name: 'Test', mcps: [{ name: 'only-mcp' }] },
    ];

    renderCard({ orchestrations });

    // Singular form keeps the badge text natural — "1 MCP", not "1 MCPs".
    expect(screen.getByText('1 MCP')).toBeInTheDocument();
  });

  it('does NOT bubble badge clicks up to the card onView handler', () => {
    // The badge lives inside the card, which itself opens the modal on click. The
    // badge must stopPropagation so expanding MCPs doesn't accidentally open the
    // modal at the same time.
    const onView = vi.fn();
    const orchestrations: Orchestration[] = [
      { id: 'orch-1', name: 'Test', mcps: [{ name: 'mcp-a' }] },
    ];

    renderCard({ onView, orchestrations });

    fireEvent.click(screen.getByText('MCPs:'));

    expect(onView).not.toHaveBeenCalled();
  });
});

// ── Inline meta-row (B1) ──────────────────────────────────────────────────────

describe('ActiveOrchestrationCard – Inline meta-row for pending / manual / disabled', () => {
  it('renders only present segments and skips zero-state fields on pending cards', () => {
    // A freshly registered pending orchestration: trigger + status + step count.
    // No lastFireTime, no nextFireTime, no runCount → those segments must not appear.
    const orchestrations: Orchestration[] = [
      { id: 'orch-1', name: 'Test', steps: [{ name: 's1' }, { name: 's2' }] },
    ];

    renderCard({
      type: 'pending',
      execution: {
        ...baseExecution,
        triggeredBy: 'scheduler',
        status: 'Scheduled',
        // runCount, lastFireTime, nextFireTime intentionally absent
      },
      orchestrations,
    });

    expect(screen.getByText('scheduler')).toBeInTheDocument();
    expect(screen.getByText('Scheduled')).toBeInTheDocument();
    expect(screen.getByText('2 steps')).toBeInTheDocument();
    // None of these zero-state segments should render — they used to take rows in
    // the old 2x2 grid as "Never" / "0" / "Unknown".
    expect(screen.queryByText(/last/i)).not.toBeInTheDocument();
    expect(screen.queryByText(/next/i)).not.toBeInTheDocument();
    expect(screen.queryByText(/run/i)).not.toBeInTheDocument();
    // The old labelled-grid labels must be gone too.
    expect(screen.queryByText('Last Fired')).not.toBeInTheDocument();
    expect(screen.queryByText('Run Count')).not.toBeInTheDocument();
    expect(screen.queryByText('Next Fire')).not.toBeInTheDocument();
  });

  it('renders all populated segments on a pending card with full data', () => {
    const orchestrations: Orchestration[] = [
      { id: 'orch-1', name: 'Test', steps: [{ name: 's1' }, { name: 's2' }, { name: 's3' }] },
    ];

    renderCard({
      type: 'pending',
      execution: {
        ...baseExecution,
        triggeredBy: 'scheduler',
        status: 'Scheduled',
        runCount: 42,
        lastFireTime: new Date(Date.now() - 60_000).toISOString(),
        nextFireTime: new Date(Date.now() + 300_000).toISOString(),
      },
      orchestrations,
    });

    expect(screen.getByText('scheduler')).toBeInTheDocument();
    expect(screen.getByText('Scheduled')).toBeInTheDocument();
    expect(screen.getByText('3 steps')).toBeInTheDocument();
    expect(screen.getByText('42 runs')).toBeInTheDocument();
    // The inline segment uses formatTimeAgo / formatTimeUntil prefixed with "last"/"next".
    expect(screen.getByText(/^last /)).toBeInTheDocument();
    expect(screen.getByText(/^next /)).toBeInTheDocument();
  });

  it('singularises "1 step" and "1 run" on the inline row', () => {
    const orchestrations: Orchestration[] = [
      { id: 'orch-1', name: 'Test', steps: [{ name: 'only' }] },
    ];

    renderCard({
      type: 'pending',
      execution: {
        ...baseExecution,
        triggeredBy: 'scheduler',
        runCount: 1,
      },
      orchestrations,
    });

    expect(screen.getByText('1 step')).toBeInTheDocument();
    expect(screen.getByText('1 run')).toBeInTheDocument();
  });

  it('renders inline summary instead of labelled grid on manual cards', () => {
    const orchestrations: Orchestration[] = [
      { id: 'orch-1', name: 'Test', steps: [{ name: 's1' }, { name: 's2' }] },
    ];

    renderCard({ type: 'manual', orchestrations });

    // Inline label text replaces the old "TYPE / STEPS" labelled cells.
    expect(screen.getByText('Manual (no trigger)')).toBeInTheDocument();
    expect(screen.getByText('2 steps')).toBeInTheDocument();
    // Old grid labels gone.
    expect(screen.queryByText('Type')).not.toBeInTheDocument();
    expect(screen.queryByText('Steps')).not.toBeInTheDocument();
  });

  it('renders inline summary on disabled cards', () => {
    const orchestrations: Orchestration[] = [
      { id: 'orch-1', name: 'Test', steps: [{ name: 's1' }] },
    ];

    renderCard({ type: 'disabled', orchestrations });

    expect(screen.getByText('Trigger disabled')).toBeInTheDocument();
    expect(screen.getByText('1 step')).toBeInTheDocument();
  });
});

// ── Description one-line clamp (B2) ───────────────────────────────────────────

describe('ActiveOrchestrationCard – Description one-line clamp', () => {
  it('renders the description with the .card-description clamp class and full text in title=', () => {
    // The .card-description class applies -webkit-line-clamp: 1, so even a very long
    // description occupies a single row. The full unclipped string is available via
    // the title attribute for hover inspection.
    const longDescription =
      'This orchestration discovers ZTS-Official builds and dispatches a per-run tracking workflow. ' +
      'It runs every 15 minutes and emits SSE events for the Portal to render in real time.';
    const orchestrations: Orchestration[] = [
      { id: 'orch-1', name: 'Test', description: longDescription },
    ];

    const { container } = renderCard({ type: 'manual', orchestrations });

    const descEl = container.querySelector('.card-description');
    expect(descEl).not.toBeNull();
    expect(descEl!.getAttribute('title')).toBe(longDescription);
    expect(descEl!.textContent).toBe(longDescription);
  });

  it('does not render a description block when the orchestration has none', () => {
    const orchestrations: Orchestration[] = [
      { id: 'orch-1', name: 'Test', steps: [{ name: 's1' }] },
    ];

    const { container } = renderCard({ type: 'manual', orchestrations });

    expect(container.querySelector('.card-description')).toBeNull();
  });
});

// ── Webhook URL → kebab menu item ────────────────────────────────────────────

describe('ActiveOrchestrationCard – Webhook URL in kebab menu', () => {
  it('exposes a "Copy webhook URL" item in the header kebab menu on webhook cards', () => {
    // The webhook URL copy moved from an inline action-row button into the
    // header kebab popover. This keeps the action row clean (only Run / Cancel
    // remain) while preserving the affordance for webhook-triggered cards.
    const orchestrations: Orchestration[] = [
      { id: 'orch-1', name: 'Webhook Orch' },
    ];

    const { container } = renderCard({
      type: 'pending',
      orchestrations,
      execution: {
        ...baseExecution,
        triggeredBy: 'webhook',
        webhookUrl: '/api/webhooks/orch-1',
      },
    });

    // Kebab button is present in the header.
    const kebabButton = container.querySelector('.card-kebab-button');
    expect(kebabButton).not.toBeNull();

    // Item is initially hidden behind the popover.
    expect(screen.queryByText('Copy webhook URL')).not.toBeInTheDocument();

    // Open the menu — the item appears.
    fireEvent.click(kebabButton!);
    expect(screen.getByText('Copy webhook URL')).toBeInTheDocument();

    // Old inline button is gone.
    expect(screen.queryByText('Webhook URL')).not.toBeInTheDocument();
  });

  it('copies the full webhook URL to clipboard when the kebab item is clicked', () => {
    const writeText = vi.fn().mockResolvedValue(undefined);
    Object.assign(navigator, { clipboard: { writeText } });

    const orchestrations: Orchestration[] = [
      { id: 'orch-1', name: 'Webhook Orch' },
    ];

    const { container } = renderCard({
      type: 'pending',
      orchestrations,
      execution: {
        ...baseExecution,
        triggeredBy: 'webhook',
        webhookUrl: '/api/webhooks/orch-1',
      },
    });

    // Open the kebab and click the item.
    fireEvent.click(container.querySelector('.card-kebab-button')!);
    fireEvent.click(screen.getByText('Copy webhook URL'));

    expect(writeText).toHaveBeenCalledTimes(1);
    // JSDOM defaults origin to http://localhost; the path must be appended.
    expect(writeText.mock.calls[0][0]).toContain('/api/webhooks/orch-1');
  });

  it('does NOT render the kebab on non-webhook trigger cards (no tertiary actions)', () => {
    // The kebab disappears entirely when there is nothing to put inside it,
    // keeping the header visually minimal on cards that have no extras.
    const orchestrations: Orchestration[] = [
      { id: 'orch-1', name: 'Scheduler Orch' },
    ];

    const { container } = renderCard({
      type: 'pending',
      orchestrations,
      execution: { ...baseExecution, triggeredBy: 'scheduler' },
    });

    expect(container.querySelector('.card-kebab-button')).toBeNull();
    expect(screen.queryByText('Webhook URL')).not.toBeInTheDocument();
    expect(screen.queryByText('Copy webhook URL')).not.toBeInTheDocument();
  });
});

// ── Tag / Profile overflow (A3 + A4) ──────────────────────────────────────────

describe('ActiveOrchestrationCard – Tag overflow', () => {
  it('renders all tags inline when count is at or below the cap', () => {
    const orchestrations: Orchestration[] = [
      { id: 'orch-1', name: 'Test', tags: ['a', 'b', 'c'] },
    ];

    renderCard({ orchestrations });

    expect(screen.getByText('a')).toBeInTheDocument();
    expect(screen.getByText('b')).toBeInTheDocument();
    expect(screen.getByText('c')).toBeInTheDocument();
    // No overflow chip when at the cap.
    expect(screen.queryByText(/more$/)).not.toBeInTheDocument();
  });

  it('shows only the first 3 tags inline plus a "+N more" chip when there are more', () => {
    const orchestrations: Orchestration[] = [
      { id: 'orch-1', name: 'Test', tags: ['a', 'b', 'c', 'd', 'e'] },
    ];

    renderCard({ orchestrations });

    // First 3 tags visible.
    expect(screen.getByText('a')).toBeInTheDocument();
    expect(screen.getByText('b')).toBeInTheDocument();
    expect(screen.getByText('c')).toBeInTheDocument();
    // Hidden behind the overflow chip.
    expect(screen.queryByText('d')).not.toBeInTheDocument();
    expect(screen.queryByText('e')).not.toBeInTheDocument();
    // Overflow chip is present.
    expect(screen.getByText('+2 more')).toBeInTheDocument();
  });

  it('reveals all tags when the "+N more" chip is clicked', () => {
    const orchestrations: Orchestration[] = [
      { id: 'orch-1', name: 'Test', tags: ['a', 'b', 'c', 'd', 'e'] },
    ];

    renderCard({ orchestrations });

    fireEvent.click(screen.getByText('+2 more'));

    expect(screen.getByText('d')).toBeInTheDocument();
    expect(screen.getByText('e')).toBeInTheDocument();
  });

  it('does NOT bubble overflow-chip clicks up to the card onView handler', () => {
    // Critical: the tag list lives inside the card, which itself fires onView on click.
    // The overflow chip must stopPropagation so power users don't get a modal pop every
    // time they expand a long tag list.
    const onView = vi.fn();
    const orchestrations: Orchestration[] = [
      { id: 'orch-1', name: 'Test', tags: ['a', 'b', 'c', 'd'] },
    ];

    renderCard({ orchestrations, onView });

    fireEvent.click(screen.getByText('+1 more'));

    expect(onView).not.toHaveBeenCalled();
  });
});

describe('ActiveOrchestrationCard – Profile overflow', () => {
  it('shows only 3 profile badges inline with "+N more" when more profiles match', () => {
    const orchestrations: Orchestration[] = [
      { id: 'orch-1', name: 'Test', tags: ['production'] },
    ];
    // Five profiles, all with wildcard filters → all match. Cap=3 so 2 should collapse.
    // ProfileFilter uses { tags, orchestrationIds, excludeOrchestrationIds } where a
    // '*' in tags matches every orchestration (see profileFilterMatchesOrchestration).
    const wildcardFilter = { tags: ['*'], orchestrationIds: [], excludeOrchestrationIds: [] };
    const timestamps = { createdAt: '2026-05-01T00:00:00Z', updatedAt: '2026-05-01T00:00:00Z' };
    const profiles: Profile[] = [
      { id: 'p1', name: 'Profile 1', isActive: false, filter: wildcardFilter, ...timestamps },
      { id: 'p2', name: 'Profile 2', isActive: false, filter: wildcardFilter, ...timestamps },
      { id: 'p3', name: 'Profile 3', isActive: false, filter: wildcardFilter, ...timestamps },
      { id: 'p4', name: 'Profile 4', isActive: false, filter: wildcardFilter, ...timestamps },
      { id: 'p5', name: 'Profile 5', isActive: false, filter: wildcardFilter, ...timestamps },
    ];

    renderCard({ orchestrations, profiles });

    expect(screen.getByText('Profile 1')).toBeInTheDocument();
    expect(screen.getByText('Profile 2')).toBeInTheDocument();
    expect(screen.getByText('Profile 3')).toBeInTheDocument();
    expect(screen.queryByText('Profile 4')).not.toBeInTheDocument();
    expect(screen.queryByText('Profile 5')).not.toBeInTheDocument();
    expect(screen.getByText('+2 more')).toBeInTheDocument();
  });
});

// ── Single-line header (status chip replaces the old left-side dot) ──────────

describe('ActiveOrchestrationCard – Single-line header with status chip', () => {
  it('does NOT render the legacy left-side status dot inside the header', () => {
    // Old layout had a 10×10 div with the .step-status-badge class but also
    // overrode width/height/borderRadius inline. Because the class kept its
    // pill `padding: 2px 8px`, the dot actually rendered as a ~26×14 oval —
    // the visual bug we set out to remove. Assert that no such dot exists
    // anywhere inside the card header.
    const { container } = renderCard({
      type: 'pending',
      execution: { ...baseExecution, triggeredBy: 'scheduler' },
    });

    const header = container.querySelector('.card-header');
    expect(header).not.toBeNull();
    const dot = header!.querySelector('div[style*="border-radius: 50%"]');
    expect(dot).toBeNull();
  });

  it('removes the old two-line .card-version status row entirely', () => {
    // The previous header rendered the status icon + text on a second
    // `.card-version` row underneath the title. That row is gone now — status
    // is rendered as a chip on the title row instead, saving ~10 px of header
    // height per card.
    const { container } = renderCard({
      type: 'running',
      execution: {
        ...baseExecution,
        executionId: 'exec-1',
        status: 'Running',
        startedAt: new Date().toISOString(),
      },
    });

    expect(container.querySelector('.card-version')).toBeNull();
  });

  it('renders the status as a .step-status-badge pill with the correct state class', () => {
    // Each card state maps to a CSS pill class so colors come from App.css
    // (a single source of truth) instead of inline overrides. Verify the chip
    // wraps the visible status text with the correct state class — that's how
    // the right colour ends up applied at runtime.
    const cases: { type: 'running' | 'pending' | 'manual' | 'disabled'; text: string; stateClass: string }[] = [
      { type: 'running', text: 'Running', stateClass: 'running' },
      { type: 'pending', text: 'Pending', stateClass: 'pending' },
      { type: 'manual', text: 'Manual', stateClass: 'manual' },
      { type: 'disabled', text: 'Disabled', stateClass: 'disabled' },
    ];

    for (const c of cases) {
      const { container, unmount } = renderCard({
        type: c.type,
        execution: c.type === 'running'
          ? { ...baseExecution, executionId: 'e', status: 'Running', startedAt: new Date().toISOString() }
          : baseExecution,
      });

      // Find the chip that contains the status text and confirm it carries both
      // the base `step-status-badge` class and the per-state class.
      const chip = container.querySelector(`.step-status-badge.${c.stateClass}`);
      expect(chip).not.toBeNull();
      expect(chip!.textContent).toContain(c.text);

      unmount();
    }
  });

  it('keeps the Waiting chip on the same row as the title and status', () => {
    // Critical UX assertion: the "Waiting" affordance must remain visible at a
    // glance when a run is paused on a HITL prompt. After collapsing the header
    // to one row, all three (status chip, title, waiting chip) must share the
    // same .card-title-area flex parent so they line up horizontally.
    const { container } = renderCard({
      type: 'running',
      execution: {
        ...baseExecution,
        executionId: 'exec-1',
        status: 'Running',
        startedAt: new Date().toISOString(),
      },
    });

    // Render with awaitingInput separately because renderCard doesn't expose
    // it; mount via the component directly here.
    const renderHelper = () => render(
      <ActiveOrchestrationCard
        execution={{ ...baseExecution, executionId: 'exec-1', status: 'Running', startedAt: new Date().toISOString() }}
        type="running"
        onView={noop}
        awaitingInput
      />,
    );
    const { container: c2 } = renderHelper();
    const titleArea = c2.querySelector('.card-title-area');
    expect(titleArea).not.toBeNull();
    const chip = titleArea!.querySelector('.step-status-badge');
    const title = titleArea!.querySelector('.card-title');
    const waiting = titleArea!.querySelector('.waiting-inputs-chip');

    // All three must be present and all must be direct children of card-title-area
    // so the flex row lays them out as `[status] [title] [waiting]`.
    expect(chip).not.toBeNull();
    expect(title).not.toBeNull();
    expect(waiting).not.toBeNull();
    expect(chip!.parentElement).toBe(titleArea);
    expect(title!.parentElement).toBe(titleArea);
    expect(waiting!.parentElement).toBe(titleArea);

    // Without awaitingInput, the Waiting chip is gone but the status chip and
    // title still share the same parent.
    const titleAreaNoWait = container.querySelector('.card-title-area');
    expect(titleAreaNoWait!.querySelector('.waiting-inputs-chip')).toBeNull();
    expect(titleAreaNoWait!.querySelector('.step-status-badge')).not.toBeNull();
    expect(titleAreaNoWait!.querySelector('.card-title')).not.toBeNull();
  });
});

// ── Resources row + labels row + simplified actions (this round) ─────────────

describe('ActiveOrchestrationCard – Resources row', () => {
  it('groups MCPs, Skills, Environment, and Models on a single flex-wrap row', () => {
    // The old layout rendered each badge in its own block; this round merges
    // them into one `.card-resources-row` so up to four stacked rows collapse
    // into a single visual band. Each badge stays individually expandable.
    const orchestrations: Orchestration[] = [
      {
        id: 'orch-1',
        name: 'Test',
        mcps: [{ name: 'mcp-a' }, { name: 'mcp-b' }],
        models: ['gpt-5.4', 'claude-opus-4.6'],
        referencedEnvVars: ['HOME', 'PATH'],
        steps: [{ name: 's1', skillDirectories: ['/skill-a'] }],
      } as unknown as Orchestration,
    ];

    const { container } = renderCard({ orchestrations });

    const row = container.querySelector('.card-resources-row');
    expect(row).not.toBeNull();
    // All four badges' labels render inside the same row (collapsed state).
    expect(row!.textContent).toContain('MCPs:');
    expect(row!.textContent).toContain('skill'); // SkillBadge text
    expect(row!.textContent).toContain('Environment:');
    expect(row!.textContent).toContain('Models:');
  });

  it('does not render the resources row when no resources are present', () => {
    const orchestrations: Orchestration[] = [
      { id: 'orch-1', name: 'Test' },
    ];

    const { container } = renderCard({ orchestrations });

    expect(container.querySelector('.card-resources-row')).toBeNull();
  });
});

describe('ActiveOrchestrationCard – Labels row', () => {
  it('groups tags and profiles on a single row', () => {
    // Tags and profiles share a single flex-wrap row. They keep their distinct
    // chip styles so users can still tell them apart at a glance.
    const orchestrations: Orchestration[] = [
      { id: 'orch-1', name: 'Test', tags: ['prod'] },
    ];
    const timestamps = { createdAt: '2026-05-01T00:00:00Z', updatedAt: '2026-05-01T00:00:00Z' };
    const profiles: Profile[] = [
      {
        id: 'p1',
        name: 'Profile 1',
        isActive: true,
        filter: { tags: ['*'], orchestrationIds: [], excludeOrchestrationIds: [] },
        ...timestamps,
      },
    ];

    const { container } = renderCard({ orchestrations, profiles });

    const row = container.querySelector('.card-labels-row');
    expect(row).not.toBeNull();
    expect(row!.querySelector('.tag-chip')).not.toBeNull();
    expect(row!.querySelector('.profile-badge')).not.toBeNull();
  });

  it('does not render the labels row when there are no tags or profiles', () => {
    const orchestrations: Orchestration[] = [
      { id: 'orch-1', name: 'Test' },
    ];

    const { container } = renderCard({ orchestrations });

    expect(container.querySelector('.card-labels-row')).toBeNull();
  });
});

describe('ActiveOrchestrationCard – View button removed', () => {
  it('does NOT render an explicit "View" button anywhere on the card', () => {
    // The View button is gone. Card click already opens the modal, so the
    // explicit affordance is redundant; removing it cleans up the action row.
    renderCard({});

    expect(screen.queryByText('View')).not.toBeInTheDocument();
  });

  it('does not render the action row at all when no Run/Cancel applies', () => {
    // A pending card with no onRun callback has nothing to put in the action
    // row. We collapse the wrapper entirely so the card saves the row's
    // margin/padding (instead of rendering an empty `.card-actions` shell).
    const { container } = renderCard({ type: 'pending' });

    expect(container.querySelector('.card-actions')).toBeNull();
  });
});

describe('ActiveOrchestrationCard – Trigger toggle in header (power icon)', () => {
  function renderWithTrigger(extras: {
    enabled: boolean;
    onToggleTrigger?: (id: string, currentlyEnabled: boolean) => void;
    onView?: typeof noop;
  }) {
    const orch: Orchestration = {
      id: 'orch-1',
      name: 'Triggered Orch',
      trigger: { type: 'scheduler', schedule: '0 * * * *' },
    };
    return renderCard({
      type: extras.enabled ? 'pending' : 'disabled',
      orchestrations: [orch],
      onToggleTrigger: extras.onToggleTrigger,
      onView: extras.onView,
    });
  }

  it('renders the power icon inside the header for triggered orchestrations', () => {
    const { container } = renderWithTrigger({ enabled: true, onToggleTrigger: () => {} });

    const titleArea = container.querySelector('.card-title-area');
    expect(titleArea).not.toBeNull();
    const power = titleArea!.querySelector('.card-power-icon');
    expect(power).not.toBeNull();
  });

  it('reflects the enabled state via the .enabled class on the power icon', () => {
    const enabled = renderWithTrigger({ enabled: true, onToggleTrigger: () => {} });
    const enabledPower = enabled.container.querySelector('.card-power-icon');
    expect(enabledPower).not.toBeNull();
    expect(enabledPower!.classList.contains('enabled')).toBe(true);
    enabled.unmount();

    const disabled = renderWithTrigger({ enabled: false, onToggleTrigger: () => {} });
    const disabledPower = disabled.container.querySelector('.card-power-icon');
    expect(disabledPower).not.toBeNull();
    expect(disabledPower!.classList.contains('enabled')).toBe(false);
  });

  it('invokes onToggleTrigger with the current state and stops propagation', () => {
    const onToggleTrigger = vi.fn();
    const onView = vi.fn();
    const { container } = renderWithTrigger({ enabled: true, onToggleTrigger, onView });

    const power = container.querySelector('.card-power-icon') as HTMLElement;
    fireEvent.click(power);

    // The callback signature is `(orchestrationId, currentlyEnabled)` — the
    // second arg is the CURRENT state at the moment of the click, not the
    // flipped state. The caller is responsible for inverting it server-side
    // (matches the contract of the previous TriggerToggle button).
    expect(onToggleTrigger).toHaveBeenCalledTimes(1);
    expect(onToggleTrigger).toHaveBeenCalledWith('orch-1', true);

    // Click did NOT bubble up to the card's onView.
    expect(onView).not.toHaveBeenCalled();
  });

  it('passes the disabled state correctly when clicking a disabled card power icon', () => {
    // Sanity check: disabled card → second arg is `false`.
    const onToggleTrigger = vi.fn();
    const { container } = renderWithTrigger({ enabled: false, onToggleTrigger });

    fireEvent.click(container.querySelector('.card-power-icon') as HTMLElement);

    expect(onToggleTrigger).toHaveBeenCalledWith('orch-1', false);
  });

  it('does NOT render the power icon for manual orchestrations (no trigger)', () => {
    // hasTrigger is computed from the orch.trigger.type ≠ "manual". Manual
    // orchestrations therefore omit the power icon — there's nothing to toggle.
    const orch: Orchestration = { id: 'orch-1', name: 'Manual Orch' };
    const { container } = renderCard({
      type: 'manual',
      orchestrations: [orch],
      onToggleTrigger: () => {},
    });

    expect(container.querySelector('.card-power-icon')).toBeNull();
  });
});

describe('ActiveOrchestrationCard – Kebab menu interactions', () => {
  it('stops click propagation so opening the kebab does not also call onView', () => {
    // The kebab lives inside the card and the card's root has onClick → onView.
    // Clicking the kebab button must NOT bubble up; otherwise users get a modal
    // pop every time they open the menu.
    const onView = vi.fn();
    const { container } = renderCard({
      type: 'pending',
      onView,
      execution: { ...baseExecution, triggeredBy: 'webhook', webhookUrl: '/api/webhooks/orch-1' },
    });

    const kebabButton = container.querySelector('.card-kebab-button') as HTMLElement;
    expect(kebabButton).not.toBeNull();
    fireEvent.click(kebabButton);

    expect(onView).not.toHaveBeenCalled();
  });
});

// ── Action button polish (compact size + bottom-pinned) ──────────────────────

describe('ActiveOrchestrationCard – Action button polish', () => {
  it('renders the Run chip outside .card-actions (icon-only, hover-revealed)', () => {
    // The Run verb is no longer a button inside .card-actions — it's a small
    // icon-only chip absolutely positioned at the bottom-right of the card,
    // hidden at rest via CSS and revealed on hover/focus. This test asserts
    // the DOM structure: the chip is a sibling of .card-body (not a child),
    // and .card-actions is not rendered on the (non-running) card.
    const orch: Orchestration = { id: 'orch-1', name: 'Test' };
    const { container } = renderCard({
      type: 'pending',
      onRun: () => {},
      orchestrations: [orch],
    });

    // No bottom action row at all on non-running cards.
    expect(container.querySelector('.card-actions')).toBeNull();

    // Run chip is present, has Play-icon child, no visible text label
    // (icon-only design with tooltip via title=).
    const chip = container.querySelector('.orch-card-run-chip') as HTMLElement | null;
    expect(chip).not.toBeNull();
    // Chip is a direct child of .orch-card (sibling of .card-body), so the
    // absolute positioning anchors to the card frame.
    expect(chip!.parentElement?.classList.contains('orch-card')).toBe(true);
    // Tooltip carries the Run verb + orchestration name.
    expect(chip!.getAttribute('title')).toContain('Run');
    expect(chip!.getAttribute('aria-label')).toContain('Run');
  });

  it('renders the Cancel button with the compact .btn-card-action class on running cards', () => {
    const { container } = renderCard({
      type: 'running',
      onCancel: () => {},
      execution: {
        ...baseExecution,
        executionId: 'exec-1',
        status: 'Running',
        startedAt: new Date().toISOString(),
      },
    });

    const cancelButton = container.querySelector('.card-actions .btn-card-action');
    expect(cancelButton).not.toBeNull();
    expect(cancelButton!.textContent).toContain('Cancel');
    expect(cancelButton!.classList.contains('btn-danger')).toBe(true);
  });

  it('does NOT apply an inline margin-top to the action row on running cards (CSS pins it instead)', () => {
    // The action row used to carry an inline `style={{ marginTop: '6px' }}`.
    // We removed it because `.card-actions { margin-top: auto }` (set in
    // App.css) now pins the row to the bottom of the card body — pushing
    // Cancel down to keep it in a consistent vertical position regardless
    // of how much content lives above it. The inline style would override
    // the auto-margin and break the pin.
    const { container } = renderCard({
      type: 'running',
      onCancel: () => {},
      execution: {
        ...baseExecution,
        executionId: 'exec-1',
        status: 'Running',
        startedAt: new Date().toISOString(),
      },
    });

    const actionRow = container.querySelector('.card-actions') as HTMLElement | null;
    expect(actionRow).not.toBeNull();
    expect(actionRow!.style.marginTop).toBe('');
  });
});

// ── Resources row badge alignment ────────────────────────────────────────────

describe('ActiveOrchestrationCard – Resources row badge alignment', () => {
  it('does not apply outer margins on SkillBadge or CollapsibleMcpsBadge wrappers', () => {
    // With `align-items: center` on .card-resources-row, a `margin-bottom` on
    // a child wrapper shifts the chip content upward relative to siblings —
    // the "skill badge sits higher than MCPs" misalignment users reported.
    // Both badges intentionally have no inline outer-margin styles; the row's
    // `gap` owns inter-badge spacing instead.
    const orchestrations: Orchestration[] = [
      {
        id: 'orch-1',
        name: 'Test',
        mcps: [{ name: 'mcp-a' }],
        steps: [{ name: 's1', skillDirectories: ['/skill-a'] }],
      } as unknown as Orchestration,
    ];

    const { container } = renderCard({ orchestrations });

    const row = container.querySelector('.card-resources-row');
    expect(row).not.toBeNull();
    // Collect each badge's direct wrapper <div> and assert no inline margin
    // styles are present. There are exactly two wrappers in this scenario
    // (MCPs + Skills); both must be margin-free.
    const wrappers = Array.from(row!.children) as HTMLElement[];
    expect(wrappers.length).toBe(2);
    for (const w of wrappers) {
      expect(w.style.marginTop).toBe('');
      expect(w.style.marginBottom).toBe('');
      expect(w.style.marginLeft).toBe('');
      expect(w.style.marginRight).toBe('');
    }
  });
});

// ── Hybrid Run affordance: hover chip + kebab fallback ───────────────────────

describe('ActiveOrchestrationCard – Hover Run chip', () => {
  function makeOrch(): Orchestration {
    return { id: 'orch-1', name: 'Test' };
  }

  it('renders the Run chip on non-running cards with an onRun callback', () => {
    // Chip is unconditionally in the DOM when the card is non-running and has
    // an onRun callback. CSS controls its visibility (hidden at rest, revealed
    // on hover/focus); jsdom can't simulate hover so we assert presence only.
    const { container } = renderCard({
      type: 'pending',
      onRun: () => {},
      orchestrations: [makeOrch()],
    });

    expect(container.querySelector('.orch-card-run-chip')).not.toBeNull();
  });

  it('does NOT render the Run chip on running cards', () => {
    // Running cards expose Cancel (safety affordance) and hide Run entirely —
    // re-running while already running is meaningless.
    const { container } = renderCard({
      type: 'running',
      onRun: () => {},
      onCancel: () => {},
      orchestrations: [makeOrch()],
      execution: {
        ...baseExecution,
        executionId: 'exec-1',
        status: 'Running',
        startedAt: new Date().toISOString(),
      },
    });

    expect(container.querySelector('.orch-card-run-chip')).toBeNull();
  });

  it('does NOT render the Run chip when no onRun callback is provided', () => {
    const { container } = renderCard({
      type: 'pending',
      orchestrations: [makeOrch()],
      // onRun intentionally omitted
    });

    expect(container.querySelector('.orch-card-run-chip')).toBeNull();
  });

  it('does NOT render the Run chip when no matching orchestration is found', () => {
    // showRun requires both onRun AND a matched `orch` (from the lookup) so
    // the chip click has a target. Without a match, the chip is suppressed.
    const { container } = renderCard({
      type: 'pending',
      onRun: () => {},
      // No orchestrations[] → orch lookup is undefined
    });

    expect(container.querySelector('.orch-card-run-chip')).toBeNull();
  });

  it('invokes onRun with the matched orchestration when the chip is clicked', () => {
    const onRun = vi.fn();
    const orch = makeOrch();
    const { container } = renderCard({
      type: 'pending',
      onRun,
      orchestrations: [orch],
    });

    const chip = container.querySelector('.orch-card-run-chip') as HTMLElement;
    fireEvent.click(chip);

    expect(onRun).toHaveBeenCalledTimes(1);
    expect(onRun).toHaveBeenCalledWith(expect.objectContaining({ id: 'orch-1' }));
  });

  it('stops click propagation so the chip click does NOT also call onView', () => {
    // Both the card root and the chip handle clicks — the chip must
    // stopPropagation so users running an orchestration don't also get the
    // modal popping open underneath.
    const onRun = vi.fn();
    const onView = vi.fn();
    const { container } = renderCard({
      type: 'pending',
      onRun,
      onView,
      orchestrations: [makeOrch()],
    });

    fireEvent.click(container.querySelector('.orch-card-run-chip') as HTMLElement);

    expect(onRun).toHaveBeenCalledTimes(1);
    expect(onView).not.toHaveBeenCalled();
  });
});

describe('ActiveOrchestrationCard – Kebab Run fallback', () => {
  it('exposes "Run" as the first item in the kebab menu on non-running cards', () => {
    // The kebab carries Run as the touch/keyboard-accessible fallback when
    // the hover-revealed chip isn't reachable. Run is the FIRST item because
    // it's the most primary verb on a definition card (above Copy webhook URL).
    const onRun = vi.fn();
    const { container } = renderCard({
      type: 'pending',
      onRun,
      orchestrations: [{ id: 'orch-1', name: 'Test' }],
      execution: {
        ...baseExecution,
        triggeredBy: 'webhook',
        webhookUrl: '/api/webhooks/orch-1',
      },
    });

    const kebabButton = container.querySelector('.card-kebab-button') as HTMLElement;
    expect(kebabButton).not.toBeNull();
    fireEvent.click(kebabButton);

    // The popover renders its items as <button>s in order.
    const items = container.querySelectorAll('.card-kebab-popover button');
    expect(items.length).toBeGreaterThanOrEqual(2);
    expect(items[0].textContent).toContain('Run');
    expect(items[1].textContent).toContain('Copy webhook URL');
  });

  it('invokes onRun with the matched orchestration when the kebab Run item is clicked', () => {
    const onRun = vi.fn();
    const { container } = renderCard({
      type: 'pending',
      onRun,
      orchestrations: [{ id: 'orch-1', name: 'Test' }],
    });

    fireEvent.click(container.querySelector('.card-kebab-button') as HTMLElement);
    fireEvent.click(screen.getByText('Run'));

    expect(onRun).toHaveBeenCalledTimes(1);
    expect(onRun).toHaveBeenCalledWith(expect.objectContaining({ id: 'orch-1' }));
  });

  it('does NOT add a Run kebab item on running cards', () => {
    // Running cards can be Cancelled but not Run-again; the kebab should
    // surface only legitimate tertiary actions.
    const { container } = renderCard({
      type: 'running',
      onRun: () => {},
      onCancel: () => {},
      orchestrations: [{ id: 'orch-1', name: 'Test' }],
      execution: {
        ...baseExecution,
        executionId: 'exec-1',
        status: 'Running',
        startedAt: new Date().toISOString(),
        triggeredBy: 'webhook',
        webhookUrl: '/api/webhooks/orch-1',
      },
    });

    // For running webhook cards, no kebab actions remain (the Copy webhook
    // URL item is also gated on !isRunning), so the kebab is omitted entirely.
    expect(container.querySelector('.card-kebab-button')).toBeNull();
  });
});
