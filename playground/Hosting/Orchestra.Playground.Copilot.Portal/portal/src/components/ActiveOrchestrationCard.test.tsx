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
