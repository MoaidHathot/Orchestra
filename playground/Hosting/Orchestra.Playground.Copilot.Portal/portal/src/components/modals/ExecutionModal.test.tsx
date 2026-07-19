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
    savedFiles: [],
    stepSavedFiles: {},
    onClose: vi.fn(),
    onCancel: vi.fn(),
    ...overrides,
  };
}

describe('ExecutionModal step timing', () => {
  it('shows the selected step duration in the header for a historical completed step', () => {
    render(
      <ExecutionModal
        {...makeProps({
          stepStatuses: { analyze: 'completed' },
          stepTimings: {
            analyze: {
              startedAt: '2026-07-20T10:00:00.000Z',
              completedAt: '2026-07-20T10:00:12.400Z',
              durationSeconds: 12.4,
            },
          },
        })}
      />,
    );

    // The mermaid mock auto-selects the first step ('analyze'), so its header renders.
    expect(screen.getByText('12.4s')).toBeInTheDocument();
  });
});

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

describe('ExecutionModal provider labels', () => {
  it('labels the configured and actual provider when they match', async () => {
    const event: StepEvent = {
      type: 'step-completed',
      timestamp: new Date().toISOString(),
      actualModel: 'gpt-5-high',
      configuredProvider: 'copilot',
      actualProvider: 'copilot',
    };
    render(<ExecutionModal {...makeProps({ stepEvents: { analyze: [event] } })} />);

    await waitFor(() => {
      expect(screen.getByText('Provider:')).toBeInTheDocument();
    });

    expect(screen.getByText('Ran on:')).toBeInTheDocument();
    // Matching providers → no "substituted" warning badge.
    expect(screen.queryByText('substituted')).not.toBeInTheDocument();
  });

  it('flags a substituted provider when configured differs from actual', async () => {
    // Reproduces the Portal misconfiguration surfaced to the user: the step asked for
    // opencode but the host ran it on copilot.
    const event: StepEvent = {
      type: 'step-completed',
      timestamp: new Date().toISOString(),
      actualModel: 'claude-opus-4.8',
      configuredProvider: 'opencode',
      actualProvider: 'copilot',
    };
    render(<ExecutionModal {...makeProps({ stepEvents: { analyze: [event] } })} />);

    await waitFor(() => {
      expect(screen.getByText('Provider:')).toBeInTheDocument();
    });

    expect(screen.getByText('opencode')).toBeInTheDocument();
    expect(screen.getByText('copilot')).toBeInTheDocument();
    expect(screen.getByText('substituted')).toBeInTheDocument();
  });

  it('reads the provider pair from a step-trace event (live path)', async () => {
    const event: StepEvent = {
      type: 'step-trace',
      timestamp: new Date().toISOString(),
      hasTrace: true,
      configuredProvider: 'opencode',
      actualProvider: 'opencode',
    };
    render(<ExecutionModal {...makeProps({ stepEvents: { analyze: [event] } })} />);

    await waitFor(() => {
      expect(screen.getByText('Provider:')).toBeInTheDocument();
    });

    expect(screen.getByText('Ran on:')).toBeInTheDocument();
    expect(screen.queryByText('substituted')).not.toBeInTheDocument();
  });
});

describe('ExecutionModal saved files', () => {
  it('renders orchestration and selected step saved file paths', async () => {
    render(<ExecutionModal {...makeProps({
      savedFiles: ['C:\\orchestra\\run\\summary.md'],
      stepSavedFiles: { analyze: ['C:\\orchestra\\run\\analysis.json'] },
    })} />);

    await waitFor(() => {
      expect(screen.getByText('Saved Files')).toBeInTheDocument();
    });

    expect(screen.getByText('C:\\orchestra\\run\\summary.md')).toBeInTheDocument();
    expect(screen.getByText('Saved Files (1)')).toBeInTheDocument();
    expect(screen.getByText('C:\\orchestra\\run\\analysis.json')).toBeInTheDocument();
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

describe('ExecutionModal markdown output', () => {
  it('renders selected step markdown output as structured content', async () => {
    const props = makeProps({
      stepResults: {
        analyze: '# Analysis\n\n- **Status:** complete\n\n| Item | Value |\n| --- | --- |\n| Score | 10 |',
      },
      finalResult: '# Analysis\n\n- **Status:** complete',
    });

    render(<ExecutionModal {...props} />);

    await waitFor(() => {
      expect(screen.getByText(/Step Output: analyze/i)).toBeInTheDocument();
    });

    expect(screen.getByRole('heading', { name: 'Analysis', level: 1 })).toBeInTheDocument();
    expect(screen.getByRole('table')).toBeInTheDocument();
    expect(screen.getByText('Status:')).toBeInTheDocument();
  });

  it('does not render raw html in step output', async () => {
    const props = makeProps({
      stepResults: {
        analyze: '<img src=x onerror="alert(1)">\n\n<script>alert(1)</script>\n\n**safe output**',
      },
    });

    const { container } = render(<ExecutionModal {...props} />);

    await waitFor(() => {
      expect(screen.getByText(/Step Output: analyze/i)).toBeInTheDocument();
    });

    expect(container.querySelector('img')).not.toBeInTheDocument();
    expect(container.querySelector('script')).not.toBeInTheDocument();
    expect(screen.getByText('safe output')).toBeInTheDocument();
  });
});

describe('ExecutionModal command step output', () => {
  it('shows persisted command output for a completed command step', async () => {
    const props = makeProps({
      orchestration: {
        id: 'orch-1',
        name: 'Command Orchestration',
        steps: [
          {
            name: 'run-command',
            type: 'Command',
            command: 'pwsh',
            arguments: ['-NoProfile', '-Command', 'Write-Output command output'],
          },
        ],
      },
      stepStatuses: { 'run-command': 'completed' },
      stepEvents: { 'run-command': [makeStepCompletedEvent()] },
      stepResults: { 'run-command': 'line one from stdout\nline two from stdout' },
      finalResult: 'line one from stdout\nline two from stdout',
      status: 'success',
    });

    render(<ExecutionModal {...props} />);

    await waitFor(() => {
      expect(screen.getByText(/Step Output: run-command/i)).toBeInTheDocument();
    });

    expect(screen.getByText(/line one from stdout/)).toBeInTheDocument();
    expect(screen.getByText(/line two from stdout/)).toBeInTheDocument();
    expect(screen.queryByText('No output captured')).not.toBeInTheDocument();
  });

  it('shows live command output without switching to subagent cards', async () => {
    const props = makeProps({
      orchestration: {
        id: 'orch-1',
        name: 'Command Orchestration',
        steps: [
          {
            name: 'run-command',
            type: 'Command',
            command: 'pwsh',
          },
        ],
      },
      stepStatuses: { 'run-command': 'running' },
      stepEvents: { 'run-command': [] },
      stepResults: {},
      stepActorStreams: {
        'run-command': {
          main: {
            key: 'main',
            actor: null,
            content: 'live stdout line\n',
            reasoning: '',
            events: [],
            startedAt: new Date().toISOString(),
            status: 'running',
          },
          subagents: [],
        },
      },
      status: 'running',
    });

    render(<ExecutionModal {...props} />);

    await waitFor(() => {
      expect(screen.getByText(/Step Output: run-command/i)).toBeInTheDocument();
    });

    expect(screen.getByText(/live stdout line/)).toBeInTheDocument();
    expect(screen.queryByText('Main agent')).not.toBeInTheDocument();
  });

  it('shows failed command trace output when no step output event exists', async () => {
    const props = makeProps({
      orchestration: {
        id: 'orch-1',
        name: 'Command Orchestration',
        steps: [
          {
            name: 'run-command',
            type: 'Command',
            command: 'pwsh',
          },
        ],
      },
      stepStatuses: { 'run-command': 'failed' },
      stepEvents: { 'run-command': [] },
      stepResults: {},
      stepTraces: {
        'run-command': {
          finalResponse: 'stdout before failure',
          responseSegments: [{ type: 'stderr', content: 'stderr details' }],
        },
      },
      finalResult: 'orchestration failed summary',
      status: 'failed',
    });

    render(<ExecutionModal {...props} />);

    await waitFor(() => {
      expect(screen.getByText(/Step Output: run-command/i)).toBeInTheDocument();
    });

    expect(screen.getByText(/stdout before failure/)).toBeInTheDocument();
    expect(screen.getByText(/stderr details/)).toBeInTheDocument();
    expect(screen.queryByText(/orchestration failed summary/)).not.toBeInTheDocument();
  });

  it('labels command trace stdout and stderr as collapsible output sections', async () => {
    const props = makeProps({
      orchestration: {
        id: 'orch-1',
        name: 'Command Orchestration',
        steps: [
          {
            name: 'run-command',
            type: 'Command',
            command: 'pwsh',
            arguments: ['-NoProfile', '-Command', 'Write-Output command output'],
          },
        ],
      },
      stepStatuses: { 'run-command': 'completed' },
      stepEvents: { 'run-command': [makeStepCompletedEvent()] },
      stepResults: { 'run-command': 'stdout from result' },
      stepTraces: {
        'run-command': {
          command: 'pwsh',
          commandArguments: ['-NoProfile', '-Command', 'Write-Output command output'],
          workingDirectory: 'C:/repo',
          environment: { MODE: 'test' },
          systemPrompt: 'Working Directory: C:/repo',
          userPromptRaw: 'pwsh -NoProfile -Command "Write-Output command output"',
          finalResponse: 'stdout from trace',
          responseSegments: [{ type: 'stderr', content: 'stderr from trace' }],
        },
      },
      finalResult: 'stdout from result',
      status: 'success',
    });

    render(<ExecutionModal {...props} />);

    await waitFor(() => {
      expect(screen.getByText(/Step Output: run-command/i)).toBeInTheDocument();
    });

    expect(screen.getByText('Command Context')).toBeInTheDocument();
    expect(screen.getByText('Command Invocation')).toBeInTheDocument();
    expect(screen.getByText('Command Arguments')).toBeInTheDocument();
    expect(screen.getByText('Environment')).toBeInTheDocument();
    expect(screen.getAllByText('Command').length).toBeGreaterThan(0);
    expect(screen.getByText('Stdout')).toBeInTheDocument();
    expect(screen.getByText('Stderr (1)')).toBeInTheDocument();
    expect(screen.queryByText('System Prompt')).not.toBeInTheDocument();
    expect(screen.queryByText('Final Response (Before Output Handler)')).not.toBeInTheDocument();

    fireEvent.click(screen.getByText('Stdout'));
    fireEvent.click(screen.getByText('Stderr (1)'));
    fireEvent.click(screen.getByText('Command Arguments'));
    fireEvent.click(screen.getByText('Environment'));

    expect(screen.getByText(/stdout from trace/)).toBeInTheDocument();
    expect(screen.getByText(/stderr from trace/)).toBeInTheDocument();
    expect(screen.getByText(/\[2\] "Write-Output command output"/)).toBeInTheDocument();
    expect(screen.getByText(/MODE: test/)).toBeInTheDocument();
  });

  it('shows empty stdout and stderr trace sections for a completed command', async () => {
    const props = makeProps({
      orchestration: {
        id: 'orch-1',
        name: 'Command Orchestration',
        steps: [
          {
            name: 'run-command',
            type: 'Command',
            command: 'pwsh',
          },
        ],
      },
      stepStatuses: { 'run-command': 'completed' },
      stepEvents: { 'run-command': [makeStepCompletedEvent()] },
      stepResults: {},
      stepTraces: {
        'run-command': {
          command: 'pwsh',
          commandArguments: [],
          finalResponse: '',
          responseSegments: [],
        },
      },
      finalResult: '',
      status: 'success',
    });

    render(<ExecutionModal {...props} />);

    await waitFor(() => {
      expect(screen.getByText(/Step Output: run-command/i)).toBeInTheDocument();
    });

    expect(screen.getByText('Process completed with no stdout or stderr output.')).toBeInTheDocument();
    expect(screen.getByText('Stdout')).toBeInTheDocument();
    expect(screen.getByText('Stderr (0)')).toBeInTheDocument();

    fireEvent.click(screen.getByText('Stdout'));
    fireEvent.click(screen.getByText('Stderr (0)'));

    expect(screen.getByText('No stdout output captured.')).toBeInTheDocument();
    expect(screen.getByText('No stderr output captured.')).toBeInTheDocument();
  });

  it('shows input and accessible context trace sections collapsed by default', async () => {
    const props = makeProps({
      orchestration: {
        id: 'orch-1',
        name: 'Context Orchestration',
        steps: [
          {
            name: 'judge',
            type: 'Prompt',
            model: 'gpt-5',
          },
        ],
      },
      stepStatuses: { judge: 'completed' },
      stepEvents: { judge: [makeStepCompletedEvent()] },
      stepResults: { judge: 'judgement' },
      stepTraces: {
        judge: {
          parameters: { ticket: 'INC-123' },
          dependencyOutputs: { fetch: 'processed output' },
          rawDependencyOutputs: { fetch: 'raw output' },
          accessibleStepData: {
            fetch: {
              status: 'Succeeded',
              output: 'processed output',
              rawOutput: 'raw output',
              files: ['C:/temp/fetch.txt'],
            },
          },
          finalResponse: 'judgement',
        },
      },
      finalResult: 'judgement',
      status: 'success',
    });

    render(<ExecutionModal {...props} />);

    await waitFor(() => {
      expect(screen.getByText('Input Parameters')).toBeInTheDocument();
    });

    expect(screen.getByText('Dependency Outputs')).toBeInTheDocument();
    expect(screen.getByText('Raw Dependency Outputs')).toBeInTheDocument();
    expect(screen.getByText('Accessible Step Data')).toBeInTheDocument();
    expect(screen.queryByText(/INC-123/)).not.toBeInTheDocument();
    expect(screen.queryByText(/processed output/)).not.toBeInTheDocument();

    fireEvent.click(screen.getByText('Input Parameters'));
    fireEvent.click(screen.getByText('Accessible Step Data'));

    expect(screen.getByText(/ticket: INC-123/)).toBeInTheDocument();
    expect(screen.getByText(/processed output/)).toBeInTheDocument();
  });
});

describe('ExecutionModal child-run badge', () => {
  it('renders a clickable child-run badge on Orchestration steps when stepChildRuns provides the child id', async () => {
    // A parent Orchestration step with persisted child lineage must surface a clickable
    // badge in the selected-step header. This is the entry point from a parent's run
    // history into the child run's detail view.
    const onViewChildRun = vi.fn();
    const orchestration: Orchestration = {
      id: 'orch-parent',
      name: 'Parent Orchestration',
      steps: [
        {
          name: 'invoke-child',
          type: 'Orchestration',
        },
      ],
    };
    const props = makeProps({
      orchestration,
      stepStatuses: { 'invoke-child': 'completed' },
      stepEvents: { 'invoke-child': [makeStepCompletedEvent()] },
      stepResults: { 'invoke-child': 'child final content' },
      stepChildRuns: {
        'invoke-child': {
          executionId: 'child-exec-id-99',
          orchestrationName: 'child-orch',
          status: 'failed',
        },
      },
    }) as Parameters<typeof ExecutionModal>[0];
    (props as { onViewChildRun: typeof onViewChildRun }).onViewChildRun = onViewChildRun;

    render(<ExecutionModal {...props} />);

    const badge = await screen.findByTestId('child-run-badge');
    expect(badge).toBeInTheDocument();
    // Label contains the child orchestration name and its lowercased status.
    expect(badge.textContent).toContain('child-orch');
    expect(badge.textContent).toContain('failed');

    fireEvent.click(badge);
    expect(onViewChildRun).toHaveBeenCalledWith('child-orch', 'child-exec-id-99');
  });

  it('does not render the badge when the selected step has no child entry', async () => {
    // A plain Prompt step with no child orchestration must omit the badge entirely so
    // non-orchestration steps look unchanged from the pre-feature behaviour.
    const onViewChildRun = vi.fn();
    const props = makeProps({
      // No stepChildRuns map at all.
    }) as Parameters<typeof ExecutionModal>[0];
    (props as { onViewChildRun: typeof onViewChildRun }).onViewChildRun = onViewChildRun;

    render(<ExecutionModal {...props} />);

    // Wait for the modal to fully render and select the first step.
    await waitFor(() => {
      expect(screen.getByText('analyze')).toBeInTheDocument();
    });

    expect(screen.queryByTestId('child-run-badge')).not.toBeInTheDocument();
  });
});
