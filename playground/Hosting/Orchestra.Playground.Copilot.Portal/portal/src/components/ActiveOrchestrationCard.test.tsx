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
} = {}) {
  const execution = { ...baseExecution, ...overrides.execution };
  return render(
    <ActiveOrchestrationCard
      execution={execution}
      type={overrides.type ?? 'pending'}
      onView={overrides.onView ?? noop}
      onCancel={overrides.onCancel}
      onRun={overrides.onRun}
      orchestrations={overrides.orchestrations}
      profiles={overrides.profiles}
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

    expect(screen.getByText('string-mcp')).toBeInTheDocument();
    expect(screen.getByText('object-mcp')).toBeInTheDocument();
  });

  it('does not render MCP section when mcps is empty', () => {
    const orchestrations: Orchestration[] = [
      { id: 'orch-1', name: 'Test', mcps: [] },
    ];

    renderCard({ orchestrations });

    expect(screen.queryByText('MCPs')).not.toBeInTheDocument();
  });

  it('does not render MCP section when mcps is undefined', () => {
    const orchestrations: Orchestration[] = [
      { id: 'orch-1', name: 'Test' },
    ];

    renderCard({ orchestrations });

    expect(screen.queryByText('MCPs')).not.toBeInTheDocument();
  });

  it('does not render MCP section when no orchestration matches', () => {
    const orchestrations: Orchestration[] = [
      { id: 'orch-999', name: 'Other', mcps: [{ name: 'hidden-mcp' }] },
    ];

    renderCard({ orchestrations });

    expect(screen.queryByText('hidden-mcp')).not.toBeInTheDocument();
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
    const onView = vi.fn();
    const orch: Orchestration = { id: 'orch-1', name: 'Test' };
    renderCard({ onView, orchestrations: [orch] });

    fireEvent.click(screen.getByText('View'));

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

  it('calls onRun when Run button is clicked on a pending card', () => {
    const onRun = vi.fn();
    const orch: Orchestration = { id: 'orch-1', name: 'Test' };

    renderCard({
      type: 'pending',
      orchestrations: [orch],
      onRun,
    });

    fireEvent.click(screen.getByText('Run'));

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

  it('shows current step name if provided', () => {
    renderCard({
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

    expect(screen.getByText('analyze-data')).toBeInTheDocument();
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

  it('shows Run button for manual cards', () => {
    const onRun = vi.fn();
    const orchestrations: Orchestration[] = [
      { id: 'orch-1', name: 'Test' },
    ];
    renderCard({ type: 'manual', orchestrations, onRun });
    expect(screen.getByText('Run')).toBeInTheDocument();
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
