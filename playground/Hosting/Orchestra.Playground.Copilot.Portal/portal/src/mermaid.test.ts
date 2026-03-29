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

  it('generates valid Mermaid for all step types', async () => {
    const { mermaidCode } = generateDefinitionDagCode(makeAllStepTypes());
    await assertMermaidParses(mermaidCode);

    // Http step uses hexagon shape
    expect(mermaidCode).toContain('fetch_data{{');
    // Command step uses stadium shape
    expect(mermaidCode).toContain('run_script(["');
    // Transform step uses rhombus shape
    expect(mermaidCode).toContain('transform_data{"');
    // Prompt step uses rectangle
    expect(mermaidCode).toContain('analyze["');

    // Type badges
    expect(mermaidCode).toContain('[HTTP]');
    expect(mermaidCode).toContain('[COMMAND]');
    expect(mermaidCode).toContain('[TRANSFORM]');
  });

  it('generates valid Mermaid with subagents', async () => {
    const { mermaidCode } = generateDefinitionDagCode(makeStepsWithSubagents());
    await assertMermaidParses(mermaidCode);

    // Subagent subgraph
    expect(mermaidCode).toContain('subgraph');
    expect(mermaidCode).toContain('Research Agent');
    expect(mermaidCode).toContain('Writer Agent');
    expect(mermaidCode).toContain('2 subagents');
  });

  it('generates valid Mermaid with loops', async () => {
    const { mermaidCode } = generateDefinitionDagCode(makeStepsWithLoop());
    await assertMermaidParses(mermaidCode);
  });

  it('generates valid Mermaid with handlers', async () => {
    const { mermaidCode } = generateDefinitionDagCode(makeStepsWithHandlers());
    await assertMermaidParses(mermaidCode);
  });

  it('generates valid Mermaid for a complex orchestration', async () => {
    const { mermaidCode } = generateDefinitionDagCode(makeComplexOrchestration());
    await assertMermaidParses(mermaidCode);

    // Should have subagent subgraph
    expect(mermaidCode).toContain('subgraph');
    expect(mermaidCode).toContain('3 subagents');

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

  it('generates valid Mermaid for execution with subagents', async () => {
    const steps = makeStepsWithSubagents();
    const statuses: Record<string, string> = {
      orchestrator: 'running',
      review: 'pending',
    };

    const { mermaidCode } = generateExecutionDagCode(steps, statuses);
    await assertMermaidParses(mermaidCode);
    expect(mermaidCode).toContain('subgraph');
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

  it('handles many subagents', async () => {
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
    expect(mermaidCode).toContain('10 subagents');
  });

  it('handles empty statuses object in execution DAG', async () => {
    const steps = makeAllStepTypes();
    const { mermaidCode } = generateExecutionDagCode(steps, {});
    await assertMermaidParses(mermaidCode);
  });
});
