import { describe, it, expect, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
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
    expect(screen.getAllByText('Default effort').length).toBeGreaterThan(0);
    expect(screen.getAllByText('Max context tokens').length).toBeGreaterThan(0);
    expect(screen.getAllByText('Billing multiplier').length).toBeGreaterThan(0);
    expect(screen.getAllByText('Vision media types').length).toBeGreaterThan(0);
    expect(screen.getByText('image/png, image/jpeg')).toBeInTheDocument();
  });
});
