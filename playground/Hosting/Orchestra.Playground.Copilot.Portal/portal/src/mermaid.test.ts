import { describe, it, expect, beforeAll } from 'vitest';
import mermaid from 'mermaid';
import type { Step } from './types';
import {
  generateDefinitionDagCode,
  generateExecutionDagCode,
  getMermaidConfig,
} from './mermaid';

// ---------------------------------------------------------------------------
// Initialize Mermaid once for the test suite (jsdom environment)
// ---------------------------------------------------------------------------
beforeAll(() => {
  mermaid.initialize(getMermaidConfig());
});

/**
 * Validate that a Mermaid code string can be parsed without errors.
 * Uses mermaid.parse() which validates syntax without rendering SVG.
 */
async function assertMermaidParses(code: string): Promise<void> {
  try {
    await mermaid.parse(code);
  } catch (err) {
    // Include the generated code in the error message for debugging
    const msg = (err as Error)?.message ?? String(err);
    throw new Error(
      `Mermaid parse error: ${msg}\n\nGenerated Mermaid code:\n${code}`,
    );
  }
}

// ---------------------------------------------------------------------------
// Test fixtures
// ---------------------------------------------------------------------------

/** Simple single-step prompt orchestration. */
function makeSimplePromptSteps(): Step[] {
  return [
    {
      name: 'analyze',
      type: 'Prompt',
      model: 'claude-opus-4.5',
    },
  ];
}

/** Multi-step orchestration with all step types. */
function makeAllStepTypes(): Step[] {
  return [
    {
      name: 'fetch-data',
      type: 'Http',
      method: 'GET',
      url: 'https://api.example.com/data',
    },
    {
      name: 'transform-data',
      type: 'Transform',
      template: 'Result: {{steps.fetch-data.output}}',
      dependsOn: ['fetch-data'],
    },
    {
      name: 'analyze',
      type: 'Prompt',
      model: 'claude-opus-4.5',
      dependsOn: ['transform-data'],
    },
    {
      name: 'run-script',
      type: 'Command',
      command: 'python',
      arguments: ['script.py', '--input', 'data.json'],
      dependsOn: ['analyze'],
    },
  ];
}

/** Orchestration with subagents on a prompt step. */
function makeStepsWithSubagents(): Step[] {
  return [
    {
      name: 'orchestrator',
      type: 'Prompt',
      model: 'claude-opus-4.5',
      subagents: [
        {
          name: 'researcher',
          displayName: 'Research Agent',
          description: 'Researches topics using web search',
        },
        {
          name: 'writer',
          displayName: 'Writer Agent',
          description: 'Writes content based on research',
        },
      ],
    } as Step,
    {
      name: 'review',
      type: 'Prompt',
      model: 'claude-opus-4.5',
      dependsOn: ['orchestrator'],
    },
  ];
}

/** Orchestration with a loop configuration. */
function makeStepsWithLoop(): Step[] {
  return [
    {
      name: 'generate',
      type: 'Prompt',
      model: 'claude-opus-4.5',
    },
    {
      name: 'evaluate',
      type: 'Prompt',
      model: 'claude-opus-4.5',
      dependsOn: ['generate'],
      loopConfig: {
        maxIterations: 3,
      },
    } as Step,
  ];
}

/** Orchestration with a disabled step. */
function makeStepsWithDisabled(): Step[] {
  return [
    {
      name: 'fetch-data',
      type: 'Http',
      method: 'GET',
      url: 'https://api.example.com/data',
    },
    {
      name: 'disabled-step',
      type: 'Prompt',
      model: 'claude-opus-4.5',
      enabled: false,
      dependsOn: ['fetch-data'],
    },
    {
      name: 'final-step',
      type: 'Prompt',
      model: 'claude-opus-4.5',
      dependsOn: ['disabled-step'],
    },
  ];
}

/** Orchestration with handler indicators. */
function makeStepsWithHandlers(): Step[] {
  const steps: Step[] = [
    {
      name: 'step-with-input',
      type: 'Prompt',
      model: 'claude-opus-4.5',
    },
    {
      name: 'step-with-output',
      type: 'Prompt',
      model: 'claude-opus-4.5',
      dependsOn: ['step-with-input'],
    },
    {
      name: 'step-with-both',
      type: 'Prompt',
      model: 'claude-opus-4.5',
      dependsOn: ['step-with-output'],
    },
  ];
  // Add handler properties via LooseStep cast (these aren't in the Step type)
  (steps[0] as unknown as Record<string, unknown>).inputHandlerPrompt = 'validate input';
  (steps[1] as unknown as Record<string, unknown>).outputHandlerPrompt = 'format output';
  (steps[2] as unknown as Record<string, unknown>).inputHandlerPrompt = 'validate';
  (steps[2] as unknown as Record<string, unknown>).outputHandlerPrompt = 'format';
  return steps;
}

/** Complex orchestration combining multiple features. */
function makeComplexOrchestration(): Step[] {
  const steps: Step[] = [
    {
      name: 'fetch-api',
      type: 'Http',
      method: 'POST',
      url: 'https://api.example.com/query',
    },
    {
      name: 'transform-response',
      type: 'Transform',
      template: '{{steps.fetch-api.output | json}}',
      dependsOn: ['fetch-api'],
    },
    {
      name: 'main-agent',
      type: 'Prompt',
      model: 'claude-opus-4.5',
      dependsOn: ['transform-response'],
      subagents: [
        { name: 'helper-1', displayName: 'Helper 1' },
        { name: 'helper-2', displayName: 'Helper 2' },
        { name: 'helper-3', displayName: 'Helper 3' },
      ],
    } as Step,
    {
      name: 'validate',
      type: 'Command',
      command: 'node',
      arguments: ['validate.js'],
      dependsOn: ['main-agent'],
    },
    {
      name: 'publish',
      type: 'Http',
      method: 'PUT',
      url: 'https://api.example.com/publish',
      dependsOn: ['validate'],
    },
  ];
  (steps[2] as unknown as Record<string, unknown>).inputHandlerPrompt = 'prepare context';
  (steps[2] as unknown as Record<string, unknown>).outputHandlerPrompt = 'extract results';
  return steps;
}

// ---------------------------------------------------------------------------
// Definition DAG tests
// ---------------------------------------------------------------------------
describe('generateDefinitionDagCode', () => {
  it('generates valid Mermaid for a simple prompt step', async () => {
    const { mermaidCode } = generateDefinitionDagCode(makeSimplePromptSteps());
    await assertMermaidParses(mermaidCode);
    expect(mermaidCode).toContain('graph TD');
    expect(mermaidCode).toContain('analyze');
  });

  it('generates valid Mermaid for all step types with uniform rounded rects', async () => {
    const { mermaidCode } = generateDefinitionDagCode(makeAllStepTypes());
    await assertMermaidParses(mermaidCode);

    // All step types now use rounded rectangle ["..."]
    expect(mermaidCode).toContain('fetch_data["');
    expect(mermaidCode).toContain('run_script["');
    expect(mermaidCode).toContain('transform_data["');
    expect(mermaidCode).toContain('analyze["');

    // Type badges as inline text (not emojis)
    expect(mermaidCode).toContain('HTTP');
    expect(mermaidCode).toContain('CMD');
    expect(mermaidCode).toContain('FN');
  });

  it('generates valid Mermaid with subagents rendered inline', async () => {
    const { mermaidCode } = generateDefinitionDagCode(makeStepsWithSubagents());
    await assertMermaidParses(mermaidCode);

    // Subagent names rendered inline within parent node (truncated to 10 chars)
    expect(mermaidCode).toContain('Research \u2026'); // "Research Agent" truncated
    expect(mermaidCode).toContain('Writer Ag\u2026'); // "Writer Agent" truncated

    // No separate subagent node or dotted edge — all inline
    expect(mermaidCode).not.toContain('-.-o');
    expect(mermaidCode).not.toContain('_sa_group');
  });

  it('generates valid Mermaid with loops', async () => {
    const { mermaidCode } = generateDefinitionDagCode(makeStepsWithLoop());
    await assertMermaidParses(mermaidCode);
  });

  it('generates valid Mermaid with handlers', async () => {
    const { mermaidCode } = generateDefinitionDagCode(makeStepsWithHandlers());
    await assertMermaidParses(mermaidCode);
  });

  it('generates valid Mermaid with a disabled step', async () => {
    const { mermaidCode } = generateDefinitionDagCode(makeStepsWithDisabled());
    await assertMermaidParses(mermaidCode);

    // Should contain the disabledStep classDef
    expect(mermaidCode).toContain('classDef disabledStep');

    // Should contain the DISABLED badge in the node label
    expect(mermaidCode).toContain('DISABLED');

    // Should apply the disabledStep class to the disabled step
    expect(mermaidCode).toContain('class disabled_step disabledStep');

    // Should NOT apply disabledStep class to enabled steps
    expect(mermaidCode).not.toContain('class fetch_data disabledStep');
    expect(mermaidCode).not.toContain('class final_step disabledStep');
  });

  it('generates valid Mermaid for a complex orchestration', async () => {
    const { mermaidCode } = generateDefinitionDagCode(makeComplexOrchestration());
    await assertMermaidParses(mermaidCode);

    // Subagents rendered inline
    expect(mermaidCode).toContain('Helper 1');
    expect(mermaidCode).toContain('Helper 2');
    expect(mermaidCode).toContain('Helper 3');

    // Should have dependency edges
    expect(mermaidCode).toContain('-->');
  });

  it('builds correct stepNameToId mapping', () => {
    const { stepNameToId } = generateDefinitionDagCode(makeAllStepTypes());
    expect(stepNameToId.get('fetch_data')).toBe('fetch-data');
    expect(stepNameToId.get('transform_data')).toBe('transform-data');
    expect(stepNameToId.get('analyze')).toBe('analyze');
    expect(stepNameToId.get('run_script')).toBe('run-script');
  });

  it('builds correct stepTypeMap', () => {
    const { stepTypeMap } = generateDefinitionDagCode(makeAllStepTypes());
    expect(stepTypeMap.get('fetch_data')).toBe('http');
    expect(stepTypeMap.get('transform_data')).toBe('transform');
    expect(stepTypeMap.get('analyze')).toBe('prompt');
    expect(stepTypeMap.get('run_script')).toBe('command');
  });

  it('truncates long step names with ellipsis', async () => {
    const steps: Step[] = [
      { name: 'orcas-island-tomorrow-forecast', type: 'Prompt' },
    ];
    const { mermaidCode } = generateDefinitionDagCode(steps);
    await assertMermaidParses(mermaidCode);
    // 30 chars should be truncated
    expect(mermaidCode).toContain('\u2026'); // ellipsis character
    expect(mermaidCode).not.toContain('orcas-island-tomorrow-forecast');
  });

  it('does not truncate short step names', async () => {
    const steps: Step[] = [
      { name: 'fetch-data', type: 'Prompt' },
    ];
    const { mermaidCode } = generateDefinitionDagCode(steps);
    await assertMermaidParses(mermaidCode);
    expect(mermaidCode).toContain('fetch-data');
  });
});

// ---------------------------------------------------------------------------
// Execution DAG tests
// ---------------------------------------------------------------------------
describe('generateExecutionDagCode', () => {
  it('generates valid Mermaid for execution with all statuses', async () => {
    const steps = makeAllStepTypes();
    const statuses: Record<string, string> = {
      'fetch-data': 'completed',
      'transform-data': 'completed',
      'analyze': 'running',
      'run-script': 'pending',
    };

    const { mermaidCode } = generateExecutionDagCode(steps, statuses);
    await assertMermaidParses(mermaidCode);

    // Status class definitions
    expect(mermaidCode).toContain('classDef pending');
    expect(mermaidCode).toContain('classDef running');
    expect(mermaidCode).toContain('classDef completed');
    expect(mermaidCode).toContain('classDef failed');
  });

  it('generates valid Mermaid for execution with subagents inline', async () => {
    const steps = makeStepsWithSubagents();
    const statuses: Record<string, string> = {
      orchestrator: 'running',
      review: 'pending',
    };

    const { mermaidCode } = generateExecutionDagCode(steps, statuses);
    await assertMermaidParses(mermaidCode);
    // Subagents inline, no separate node
    expect(mermaidCode).not.toContain('_sa_group');
  });

  it('generates valid Mermaid for execution with failed/cancelled/skipped', async () => {
    const steps: Step[] = [
      { name: 'step1', type: 'Prompt' },
      { name: 'step2', type: 'Http', dependsOn: ['step1'] },
      { name: 'step3', type: 'Command', dependsOn: ['step1'] },
    ];
    const statuses: Record<string, string> = {
      step1: 'failed',
      step2: 'cancelled',
      step3: 'skipped',
    };

    const { mermaidCode } = generateExecutionDagCode(steps, statuses);
    await assertMermaidParses(mermaidCode);
  });

  it('generates valid Mermaid for complex execution', async () => {
    const steps = makeComplexOrchestration();
    const statuses: Record<string, string> = {
      'fetch-api': 'completed',
      'transform-response': 'completed',
      'main-agent': 'running',
      validate: 'pending',
      publish: 'pending',
    };

    const { mermaidCode } = generateExecutionDagCode(steps, statuses);
    await assertMermaidParses(mermaidCode);
  });

  it('generates valid Mermaid for execution with a disabled step', async () => {
    const steps = makeStepsWithDisabled();
    const statuses: Record<string, string> = {
      'fetch-data': 'completed',
      'disabled-step': 'skipped',
      'final-step': 'pending',
    };

    const { mermaidCode } = generateExecutionDagCode(steps, statuses);
    await assertMermaidParses(mermaidCode);

    // Should contain the disabledStep classDef
    expect(mermaidCode).toContain('classDef disabledStep');

    // Should contain the DISABLED badge in the node label
    expect(mermaidCode).toContain('DISABLED');

    // Should apply the disabledStep class to the disabled step
    expect(mermaidCode).toContain('class disabled_step disabledStep');

    // Should NOT apply disabledStep class to enabled steps
    expect(mermaidCode).not.toContain('class fetch_data disabledStep');
    expect(mermaidCode).not.toContain('class final_step disabledStep');
  });

  it('returns stepTypeMap for post-render accent bars', () => {
    const steps = makeAllStepTypes();
    const { stepTypeMap } = generateExecutionDagCode(steps, {});
    expect(stepTypeMap.get('fetch_data')).toBe('http');
    expect(stepTypeMap.get('transform_data')).toBe('transform');
    expect(stepTypeMap.get('analyze')).toBe('prompt');
    expect(stepTypeMap.get('run_script')).toBe('command');
  });
});

// ---------------------------------------------------------------------------
// Regression / safety tests for classDef syntax
// ---------------------------------------------------------------------------
describe('classDef safety', () => {
  /**
   * Mermaid classDef has several parser limitations:
   * - "default" is a reserved keyword (it is the name of the default class)
   * - Hyphenated CSS properties like "font-size" are fragile
   * - CSS values containing reserved words can confuse the parser
   *
   * These tests ensure we never accidentally reintroduce such patterns.
   */

  function getAllClassDefs(code: string): string[] {
    return code.split('\n').filter((line) => line.trim().startsWith('classDef'));
  }

  it('definition DAG classDefs do not use reserved words as values', () => {
    const { mermaidCode } = generateDefinitionDagCode(makeComplexOrchestration());
    const classDefs = getAllClassDefs(mermaidCode);
    expect(classDefs.length).toBeGreaterThan(0);

    for (const line of classDefs) {
      // "default" as a CSS value (e.g. cursor:default) breaks Mermaid
      expect(line).not.toMatch(/:\s*default\b/);
      // font-size with hyphen is fragile in classDef
      expect(line).not.toMatch(/font-size/i);
      // cursor property is applied via JS, not classDef
      expect(line).not.toMatch(/cursor/i);
    }
  });

  it('execution DAG classDefs do not use reserved words as values', () => {
    const { mermaidCode } = generateExecutionDagCode(
      makeComplexOrchestration(),
      { 'fetch-api': 'completed', 'main-agent': 'running' },
    );
    const classDefs = getAllClassDefs(mermaidCode);
    expect(classDefs.length).toBeGreaterThan(0);

    for (const line of classDefs) {
      expect(line).not.toMatch(/:\s*default\b/);
      expect(line).not.toMatch(/font-size/i);
      expect(line).not.toMatch(/cursor/i);
    }
  });

  it('classDef names do not conflict with Mermaid reserved words', () => {
    const reservedNames = ['default', 'graph', 'subgraph', 'end', 'style', 'linkStyle'];
    const { mermaidCode: defCode } = generateDefinitionDagCode(makeComplexOrchestration());
    const { mermaidCode: execCode } = generateExecutionDagCode(
      makeComplexOrchestration(),
      { 'fetch-api': 'completed' },
    );

    for (const code of [defCode, execCode]) {
      const classDefs = getAllClassDefs(code);
      for (const line of classDefs) {
        const match = line.match(/classDef\s+(\S+)/);
        if (match) {
          const className = match[1];
          expect(reservedNames).not.toContain(className);
        }
      }
    }
  });
});

// ---------------------------------------------------------------------------
// Edge case tests
// ---------------------------------------------------------------------------
describe('edge cases', () => {
  it('handles step names with special characters', async () => {
    const steps: Step[] = [
      { name: 'step-with-dashes', type: 'Prompt' },
      { name: 'step_with_underscores', type: 'Prompt', dependsOn: ['step-with-dashes'] },
      { name: 'step.with.dots', type: 'Prompt', dependsOn: ['step_with_underscores'] },
    ];
    const { mermaidCode } = generateDefinitionDagCode(steps);
    await assertMermaidParses(mermaidCode);
  });

  it('handles steps with long URLs in Http steps', async () => {
    const steps: Step[] = [
      {
        name: 'api-call',
        type: 'Http',
        method: 'POST',
        url: 'https://very-long-domain-name.example.com/api/v2/resources/items/details?filter=active&sort=created_at&page=1&per_page=100',
      },
    ];
    const { mermaidCode } = generateDefinitionDagCode(steps);
    await assertMermaidParses(mermaidCode);
  });

  it('handles steps with template URLs containing mustache syntax', async () => {
    const steps: Step[] = [
      {
        name: 'api-call',
        type: 'Http',
        method: 'GET',
        url: '{{config.baseUrl}}/api/items/{{steps.prev.id}}',
      },
    ];
    const { mermaidCode } = generateDefinitionDagCode(steps);
    await assertMermaidParses(mermaidCode);
  });

  it('handles steps with quotes in descriptions', async () => {
    const steps: Step[] = [
      {
        name: 'agent',
        type: 'Prompt',
        model: 'claude-opus-4.5',
        subagents: [
          {
            name: 'helper',
            displayName: 'The "Helper" Agent',
            description: 'Does things with "quotes" in it',
          },
        ],
      } as Step,
    ];
    const { mermaidCode } = generateDefinitionDagCode(steps);
    await assertMermaidParses(mermaidCode);
  });

  it('handles many subagents with count display', async () => {
    const subagents = Array.from({ length: 10 }, (_, i) => ({
      name: `agent-${i}`,
      displayName: `Agent ${i}`,
      description: `Subagent number ${i}`,
    }));
    const steps: Step[] = [
      {
        name: 'orchestrator',
        type: 'Prompt',
        model: 'claude-opus-4.5',
        subagents,
      } as Step,
    ];
    const { mermaidCode } = generateDefinitionDagCode(steps);
    await assertMermaidParses(mermaidCode);
    // >3 subagents shows count instead of individual names
    expect(mermaidCode).toContain('10 subagents');
  });

  it('handles empty statuses object in execution DAG', async () => {
    const steps = makeAllStepTypes();
    const { mermaidCode } = generateExecutionDagCode(steps, {});
    await assertMermaidParses(mermaidCode);
  });

  it('renders completed_early status with correct class and icon', async () => {
    const steps: Step[] = [
      {
        name: 'check-incidents',
        type: 'Prompt',
        model: 'claude-opus-4.5',
        dependsOn: [],
      },
      {
        name: 'acknowledge',
        type: 'Prompt',
        model: 'claude-opus-4.5',
        dependsOn: ['check-incidents'],
      },
    ];
    const statuses: Record<string, string> = {
      'check-incidents': 'completed_early',
      'acknowledge': 'cancelled',
    };
    const { mermaidCode } = generateExecutionDagCode(steps, statuses);

    // Should contain the completedEarly classDef
    expect(mermaidCode).toContain('classDef completedEarly');

    // Should assign the completedEarly class to the step
    expect(mermaidCode).toContain('completedEarly');

    // Should contain the stop icon (⏹) in the node label
    expect(mermaidCode).toContain('\u23F9');

    // Should parse as valid Mermaid syntax
    await assertMermaidParses(mermaidCode);
  });
});
