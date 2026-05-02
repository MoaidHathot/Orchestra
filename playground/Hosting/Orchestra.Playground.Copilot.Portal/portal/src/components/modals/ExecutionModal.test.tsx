import { describe, it, expect, vi } from 'vitest';
import { render, screen, waitFor, fireEvent } from '@testing-library/react';
import '@testing-library/jest-dom';
import ExecutionModal from './ExecutionModal';
import type { ExecutionModalState, Orchestration, StepEvent } from '../../types';

if (!Element.prototype.scrollIntoView) {
  Element.prototype.scrollIntoView = vi.fn();
}

vi.mock('../../mermaid', () => ({
  renderExecutionDag: (
    orchestration: Orchestration,
    _stepStatuses: Record<string, string>,
    _container: HTMLElement,
    setSelectedStep: (step: string) => void,
  ) => {
    const first = orchestration.steps?.[0];
    if (first && typeof first !== 'string') {
      setSelectedStep(first.name);
    }
  },
}));

function makeStepCompletedEvent(): StepEvent {
  return {
    type: 'step-completed',
    timestamp: new Date().toISOString(),
    selectedModel: 'gpt-5-high',
    actualModel: 'gpt-5-high',
    requestedModelInfo: {
      id: 'gpt-5',
      name: 'GPT-5',
      defaultReasoningEffort: 'medium',
      reasoningEfforts: ['low', 'medium', 'high'],
      supportsReasoningEffort: true,
      maxContextWindowTokens: 256000,
    },
    selectedModelInfo: {
      id: 'gpt-5-high',
      name: 'GPT-5 High',
      defaultReasoningEffort: 'high',
      reasoningEfforts: ['medium', 'high'],
      supportsReasoningEffort: true,
      supportsVision: true,
      billingMultiplier: 1.5,
      maxPromptTokens: 32000,
    },
    actualModelInfo: {
      id: 'gpt-5-high',
      name: 'GPT-5 High',
      policyState: 'allowed',
      policyTerms: 'preview',
      visionSupportedMediaTypes: ['image/png', 'image/jpeg'],
      maxPromptImages: 4,
      maxPromptImageSize: 10485760,
    },
  };
}

function makeProps(overrides: Partial<ExecutionModalState> = {}) {
  const orchestration: Orchestration = {
    id: 'orch-1',
    name: 'Test Orchestration',
    steps: [
      {
        name: 'analyze',
        type: 'Prompt',
        model: 'gpt-5',
      },
    ],
  };

  return {
    open: true,
    orchestration,
    executionId: 'exec-1',
    stepStatuses: { analyze: 'completed' },
    stepEvents: { analyze: [makeStepCompletedEvent()] },
    stepResults: { analyze: 'done' },
    stepTraces: {},
    stepAuditLogs: {},
    stepActorStreams: {},
    streamingContent: '',
    finalResult: 'done',
    status: 'success',
    errorMessage: null,
    completedByStep: null,
    runContext: null,
    hookExecutions: [],
    onClose: vi.fn(),
    onCancel: vi.fn(),
    ...overrides,
  };
}

describe('ExecutionModal model metadata', () => {
  it('renders configured, selected, and actual model metadata', async () => {
    render(<ExecutionModal {...makeProps()} />);

    await waitFor(() => {
      expect(screen.getByText('Configured Model:')).toBeInTheDocument();
    });

    expect(screen.getAllByText('gpt-5').length).toBeGreaterThan(0);
    expect(screen.getByText('Selected Model:')).toBeInTheDocument();
    expect(screen.getAllByText('gpt-5-high').length).toBeGreaterThan(0);
    expect(screen.getByText('Configured Metadata')).toBeInTheDocument();
    expect(screen.getByText('Selected Metadata')).toBeInTheDocument();
    expect(screen.getByText('Actual Metadata')).toBeInTheDocument();

    // Metadata cards are collapsed by default - detail rows should not be rendered yet
    expect(screen.queryByText('Default effort')).not.toBeInTheDocument();
    expect(screen.queryByText('Max context tokens')).not.toBeInTheDocument();
    expect(screen.queryByText('Billing multiplier')).not.toBeInTheDocument();
    expect(screen.queryByText('Vision media types')).not.toBeInTheDocument();
    expect(screen.queryByText('image/png, image/jpeg')).not.toBeInTheDocument();

    // Expand each metadata card by clicking its header
    fireEvent.click(screen.getByText('Configured Metadata'));
    fireEvent.click(screen.getByText('Selected Metadata'));
    fireEvent.click(screen.getByText('Actual Metadata'));

    expect(screen.getAllByText('Default effort').length).toBeGreaterThan(0);
    expect(screen.getAllByText('Max context tokens').length).toBeGreaterThan(0);
    expect(screen.getAllByText('Billing multiplier').length).toBeGreaterThan(0);
    expect(screen.getAllByText('Vision media types').length).toBeGreaterThan(0);
    expect(screen.getByText('image/png, image/jpeg')).toBeInTheDocument();
  });
});

describe('ExecutionModal output panel — empty-stream fall-through', () => {
  // Regression test for the bug where Orchestration / Transform / Script / Command / Http
  // steps showed "No output produced" in the execution view because the SubagentCard UI
  // was rendered for any step that had a stepActorStreams bucket — but those step types
  // never emit content-delta events, so the bucket was always empty. The fix is to render
  // the cards UI only when the actor stream actually has activity, otherwise fall through
  // to the persisted stepResults / finalContent.
  it('shows stepResults content when an Orchestration step has an empty actor stream', async () => {
    const props = makeProps({
      // Stream bucket exists (because step-started fired) but carries no content/reasoning/events.
      // This is the exact shape produced by Orchestration / Transform steps mid-flight or after
      // the run completed.
      stepActorStreams: {
        analyze: {
          main: {
            key: 'main',
            actor: null,
            content: '',
            reasoning: '',
            events: [],
            startedAt: new Date().toISOString(),
            status: 'completed',
          },
          subagents: [],
        },
      },
      stepResults: { analyze: 'orchestration step output that the cards UI used to hide' },
      stepStatuses: { analyze: 'completed' },
    });

    render(<ExecutionModal {...props} />);

    // Wait for the DAG mock to select the first step and propagate to the output panel.
    await waitFor(() => {
      expect(screen.getByText(/Step Output: analyze/i)).toBeInTheDocument();
    });

    // The persisted stepResults content must surface — the empty cards UI must not hide it.
    expect(screen.getByText(/orchestration step output that the cards UI used to hide/)).toBeInTheDocument();
    expect(screen.queryByText('No output produced')).not.toBeInTheDocument();
    expect(screen.queryByText('Waiting for output…')).not.toBeInTheDocument();
  });

  it('still shows the SubagentCard when the actor stream has content', async () => {
    // Use status='running' so SubagentCard stays expanded — it auto-collapses on completion
    // (covered by SubagentCard's own tests). Here we only assert that hasStreamActivity flips
    // the rendering branch from "fall through to displayContent" back to the cards UI.
    const props = makeProps({
      stepActorStreams: {
        analyze: {
          main: {
            key: 'main',
            actor: null,
            content: 'streamed assistant chunks here',
            reasoning: '',
            events: [],
            startedAt: new Date().toISOString(),
            status: 'running',
          },
          subagents: [],
        },
      },
      stepResults: {},
      stepStatuses: { analyze: 'running' },
      status: 'running',
    });

    render(<ExecutionModal {...props} />);

    await waitFor(() => {
      expect(screen.getByText(/Step Output: analyze/i)).toBeInTheDocument();
    });

    // Streamed content surfaces inside the SubagentCard.
    expect(screen.getByText(/streamed assistant chunks here/)).toBeInTheDocument();
  });
});
