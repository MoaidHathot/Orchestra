import mermaid from 'mermaid';
import type { Orchestration } from './types';

/**
 * Extended step shape used at runtime by the DAG renderer.
 * The orchestration JSON coming from the server may carry fields that are
 * not (yet) captured in the strict `Step` interface, so we use a loose
 * record here to avoid runtime breakage.
 */
type LooseStep = Record<string, unknown>;

function getStepName(step: LooseStep | string): string | undefined {
  if (typeof step === 'string') return step;
  return step?.name as string | undefined;
}

// ---------------------------------------------------------------------------
// renderMermaidDag – renders the DAG for an orchestration *definition*
// ---------------------------------------------------------------------------
export async function renderMermaidDag(
  orchestration: Orchestration,
  container: HTMLElement,
  onNodeClick?: (stepName: string) => void,
): Promise<void> {
  const steps = orchestration?.steps;
  if (!steps || steps.length === 0) {
    container.innerHTML =
      '<div class="empty-state"><div class="empty-text">No steps to visualize</div></div>';
    return;
  }

  // Show loading spinner while rendering
  container.innerHTML = '<div class="empty-state"><div class="spinner"></div></div>';

  // Build mermaid DAG from steps
  let mermaidCode = 'graph TD\n';
  const stepNameToId = new Map<string, string>();
  const stepsWithInputHandler: string[] = [];
  const stepsWithOutputHandler: string[] = [];
  const stepsWithBothHandlers: string[] = [];

  const loopEdges: string[] = [];

  steps.forEach((step) => {
    const s = step as unknown as LooseStep;
    const safeId = step.name.replace(/[^a-zA-Z0-9]/g, '_');
    stepNameToId.set(safeId, step.name);

    // Build label with handler indicators and loop indicator
    let labelLine1 = step.name;
    const hasInput = s.inputHandlerPrompt;
    const hasOutput = s.outputHandlerPrompt;
    const hasLoop = s.loopConfig || s.loop;

    if (hasInput && hasOutput) {
      labelLine1 = `${step.name} ⇄`;
      stepsWithBothHandlers.push(safeId);
    } else if (hasInput) {
      labelLine1 = `${step.name} ⇢`;
      stepsWithInputHandler.push(safeId);
    } else if (hasOutput) {
      labelLine1 = `${step.name} ⇠`;
      stepsWithOutputHandler.push(safeId);
    }

    // Add loop indicator to label if step has a loop config
    if (hasLoop) {
      labelLine1 = `${labelLine1} ↻`;
    }

    // Build additional info lines for model and MCPs
    const labelParts = [labelLine1];

    // Add model info if available
    if (step.model) {
      // Shorten model name for display
      const shortModel = step.model.split('/').pop() || step.model;
      labelParts.push(`<small>${shortModel}</small>`);
    }

    // Add MCPs info if available
    const mcps = (s.mcps || s.mcp) as unknown;
    if (mcps && Array.isArray(mcps) && mcps.length > 0) {
      const mcpNames = mcps
        .map((m: unknown) => (typeof m === 'string' ? m : (m as Record<string, unknown>).name))
        .join(', ');
      labelParts.push(`<small>MCPs: ${mcpNames}</small>`);
    } else if (mcps && typeof mcps === 'string') {
      labelParts.push(`<small>MCP: ${mcps}</small>`);
    }

    const label = labelParts.join('<br/>');
    mermaidCode += `  ${safeId}["${label}"]\n`;

    if (step.dependsOn && step.dependsOn.length > 0) {
      step.dependsOn.forEach((dep) => {
        const safeDep = dep.replace(/[^a-zA-Z0-9]/g, '_');
        mermaidCode += `  ${safeDep} --> ${safeId}\n`;
      });
    }

    // If this step has a loop config, add a loop edge back to the target
    if (hasLoop) {
      const loopConfig = (s.loopConfig || s.loop) as Record<string, unknown>;
      const targetName = (loopConfig.target || loopConfig.Target) as string | undefined;
      if (targetName) {
        const safeTarget = targetName.replace(/[^a-zA-Z0-9]/g, '_');
        // Use dotted arrow with label showing max iterations
        const maxIter = loopConfig.maxIterations || loopConfig.MaxIterations || '?';
        loopEdges.push(`  ${safeId} -.->|"loop (max ${maxIter})"| ${safeTarget}\n`);
      }
    }
  });

  // Add loop edges (dotted arrows going back)
  loopEdges.forEach((edge) => {
    mermaidCode += edge;
  });

  // Add styling with hover effect and handler indicators
  mermaidCode += '\n  classDef default fill:#21262d,stroke:#30363d,color:#e6edf3,cursor:pointer\n';
  mermaidCode += '  classDef hover fill:#30363d,stroke:#58a6ff,color:#e6edf3\n';
  mermaidCode +=
    '  classDef hasInputHandler fill:#1f3a1f,stroke:#3fb950,color:#e6edf3,cursor:pointer\n';
  mermaidCode +=
    '  classDef hasOutputHandler fill:#1f2a3f,stroke:#58a6ff,color:#e6edf3,cursor:pointer\n';
  mermaidCode +=
    '  classDef hasBothHandlers fill:#2d2a1f,stroke:#d29922,color:#e6edf3,cursor:pointer\n';
  mermaidCode +=
    '  classDef hasLoop fill:#2d1f2d,stroke:#a371f7,color:#e6edf3,cursor:pointer\n';

  // Collect steps with loops for styling
  const stepsWithLoop = steps
    .filter((s) => {
      const loose = s as unknown as LooseStep;
      return loose.loopConfig || loose.loop;
    })
    .map((s) => s.name.replace(/[^a-zA-Z0-9]/g, '_'));

  // Apply classes
  if (stepsWithInputHandler.length > 0) {
    mermaidCode += `  class ${stepsWithInputHandler.join(',')} hasInputHandler\n`;
  }
  if (stepsWithOutputHandler.length > 0) {
    mermaidCode += `  class ${stepsWithOutputHandler.join(',')} hasOutputHandler\n`;
  }
  if (stepsWithBothHandlers.length > 0) {
    mermaidCode += `  class ${stepsWithBothHandlers.join(',')} hasBothHandlers\n`;
  }
  if (stepsWithLoop.length > 0) {
    mermaidCode += `  class ${stepsWithLoop.join(',')} hasLoop\n`;
  }

  try {
    mermaid.initialize({
      startOnLoad: false,
      theme: 'dark',
      securityLevel: 'loose', // Required for click handlers
      themeVariables: {
        primaryColor: '#21262d',
        primaryTextColor: '#e6edf3',
        primaryBorderColor: '#30363d',
        lineColor: '#58a6ff',
        secondaryColor: '#161b22',
        tertiaryColor: '#0d1117',
      },
    });

    const { svg } = await mermaid.render('dag-' + Date.now(), mermaidCode);
    container.innerHTML = svg;

    // Add click handlers to nodes
    if (onNodeClick) {
      const svgElement = container.querySelector('svg');
      if (svgElement) {
        // Find all nodes (g elements with class 'node')
        const nodes = svgElement.querySelectorAll('.node');
        nodes.forEach((node) => {
          // Get the node ID from the data-id attribute or the text content
          const nodeId = node.id?.replace('flowchart-', '').replace(/-\d+$/, '');
          const stepName = stepNameToId.get(nodeId);

          if (stepName) {
            (node as HTMLElement).style.cursor = 'pointer';
            node.addEventListener('click', (e) => {
              e.stopPropagation();
              onNodeClick(stepName);
            });
            // Add hover effect
            node.addEventListener('mouseenter', () => {
              const rect = node.querySelector('rect, polygon, circle, ellipse') as HTMLElement | null;
              if (rect) {
                rect.style.stroke = '#58a6ff';
                rect.style.strokeWidth = '2px';
              }
            });
            node.addEventListener('mouseleave', () => {
              const rect = node.querySelector('rect, polygon, circle, ellipse') as HTMLElement | null;
              if (rect) {
                rect.style.stroke = '#30363d';
                rect.style.strokeWidth = '1px';
              }
            });
          }
        });
      }
    }
  } catch (err) {
    console.error('Mermaid render error:', err);
    container.innerHTML = `<div class="empty-state"><div class="empty-text">Failed to render DAG: ${(err as Error).message}</div></div>`;
  }
}

// ---------------------------------------------------------------------------
// renderExecutionDag – renders a DAG with execution status coloring
// ---------------------------------------------------------------------------
export async function renderExecutionDag(
  orchestration: Orchestration,
  stepStatuses: Record<string, string>,
  container: HTMLElement,
  onNodeClick?: (stepName: string) => void,
  selectedStep?: string,
): Promise<void> {
  const steps = orchestration?.steps;
  if (!steps || steps.length === 0) {
    container.innerHTML =
      '<div class="empty-state"><div class="empty-text">No steps to visualize</div></div>';
    return;
  }

  // Build mermaid DAG from steps with status styling
  let mermaidCode = 'graph TD\n';
  const stepNameToId = new Map<string, string | LooseStep>();
  const pendingSteps: string[] = [];
  const runningSteps: string[] = [];
  const completedSteps: string[] = [];
  const failedSteps: string[] = [];
  const cancelledSteps: string[] = [];
  const skippedSteps: string[] = [];

  const loopEdges: string[] = [];

  steps.forEach((step) => {
    // Handle both string and object step formats
    const stepName = getStepName(step as unknown as LooseStep | string);
    if (!stepName) return; // Skip invalid steps

    const safeId = stepName.replace(/[^a-zA-Z0-9]/g, '_');
    stepNameToId.set(safeId, stepName);

    // Store step object for later reference (MCPs, etc.)
    if (typeof step === 'object') {
      stepNameToId.set(`${safeId}_data`, step as unknown as LooseStep);
    }

    const s = step as unknown as LooseStep;

    // Build label with status emoji
    const status = stepStatuses[stepName] || 'pending';
    let statusIcon = '';
    switch (status) {
      case 'running':
        statusIcon = ' ...';
        break;
      case 'completed':
        statusIcon = ' ✓';
        break;
      case 'failed':
        statusIcon = ' ✗';
        break;
      case 'cancelled':
        statusIcon = ' ⊘';
        break;
      case 'skipped':
        statusIcon = ' ○';
        break;
      default:
        statusIcon = '';
        break;
    }

    // Check for loop config
    const hasLoop = typeof step === 'object' && (s.loopConfig || s.loop);
    const loopIndicator = hasLoop ? ' ↻' : '';

    // Build label with multiple lines for model and MCPs
    const labelParts = [`${stepName}${statusIcon}${loopIndicator}`];

    // Add model info if available (only for object steps)
    if (typeof step === 'object' && step.model) {
      const shortModel = step.model.split('/').pop() || step.model;
      labelParts.push(`<small>${shortModel}</small>`);
    }

    // Add MCPs info if available (only for object steps)
    if (typeof step === 'object') {
      const mcps = (s.mcps || s.mcp) as unknown;
      if (mcps && Array.isArray(mcps) && mcps.length > 0) {
        const mcpNames = mcps
          .map((m: unknown) => (typeof m === 'string' ? m : (m as Record<string, unknown>).name))
          .join(', ');
        labelParts.push(`<small>MCPs: ${mcpNames}</small>`);
      } else if (mcps && typeof mcps === 'string') {
        labelParts.push(`<small>MCP: ${mcps}</small>`);
      }
    }

    const label = labelParts.join('<br/>');
    mermaidCode += `  ${safeId}["${label}"]\n`;

    // Categorize by status
    switch (status) {
      case 'running':
        runningSteps.push(safeId);
        break;
      case 'completed':
        completedSteps.push(safeId);
        break;
      case 'failed':
        failedSteps.push(safeId);
        break;
      case 'cancelled':
        cancelledSteps.push(safeId);
        break;
      case 'skipped':
        skippedSteps.push(safeId);
        break;
      default:
        pendingSteps.push(safeId);
        break;
    }

    // Handle dependencies (only available if step is an object)
    const dependsOn = typeof step === 'object' ? step.dependsOn : null;
    if (dependsOn && dependsOn.length > 0) {
      dependsOn.forEach((dep) => {
        const safeDep = dep.replace(/[^a-zA-Z0-9]/g, '_');
        mermaidCode += `  ${safeDep} --> ${safeId}\n`;
      });
    }

    // If this step has a loop config, add a loop edge back to the target
    if (hasLoop) {
      const loopConfig = (s.loopConfig || s.loop) as Record<string, unknown>;
      const targetName = (loopConfig.target || loopConfig.Target) as string | undefined;
      if (targetName) {
        const safeTarget = targetName.replace(/[^a-zA-Z0-9]/g, '_');
        // Use dotted arrow with label showing max iterations
        const maxIter = loopConfig.maxIterations || loopConfig.MaxIterations || '?';
        loopEdges.push(`  ${safeId} -.->|"loop (max ${maxIter})"| ${safeTarget}\n`);
      }
    }
  });

  // Add loop edges (dotted arrows going back)
  loopEdges.forEach((edge) => {
    mermaidCode += edge;
  });

  // Add styling for different statuses
  mermaidCode += '\n  classDef pending fill:#21262d,stroke:#484f58,color:#8b949e,cursor:pointer\n';
  mermaidCode += '  classDef running fill:#0d2847,stroke:#58a6ff,color:#58a6ff,cursor:pointer\n';
  mermaidCode +=
    '  classDef completed fill:#0d331a,stroke:#3fb950,color:#3fb950,cursor:pointer\n';
  mermaidCode += '  classDef failed fill:#3d1418,stroke:#f85149,color:#f85149,cursor:pointer\n';
  mermaidCode +=
    '  classDef cancelled fill:#3d2e0d,stroke:#d29922,color:#d29922,cursor:pointer\n';
  mermaidCode += '  classDef skipped fill:#21262d,stroke:#484f58,color:#6e7681,cursor:pointer\n';
  mermaidCode += '  classDef selected stroke-width:3px\n';

  // Apply classes
  if (pendingSteps.length > 0) {
    mermaidCode += `  class ${pendingSteps.join(',')} pending\n`;
  }
  if (runningSteps.length > 0) {
    mermaidCode += `  class ${runningSteps.join(',')} running\n`;
  }
  if (completedSteps.length > 0) {
    mermaidCode += `  class ${completedSteps.join(',')} completed\n`;
  }
  if (failedSteps.length > 0) {
    mermaidCode += `  class ${failedSteps.join(',')} failed\n`;
  }
  if (cancelledSteps.length > 0) {
    mermaidCode += `  class ${cancelledSteps.join(',')} cancelled\n`;
  }
  if (skippedSteps.length > 0) {
    mermaidCode += `  class ${skippedSteps.join(',')} skipped\n`;
  }

  try {
    mermaid.initialize({
      startOnLoad: false,
      theme: 'dark',
      securityLevel: 'loose',
      themeVariables: {
        primaryColor: '#21262d',
        primaryTextColor: '#e6edf3',
        primaryBorderColor: '#30363d',
        lineColor: '#58a6ff',
        secondaryColor: '#161b22',
        tertiaryColor: '#0d1117',
      },
    });

    const { svg } = await mermaid.render('exec-dag-' + Date.now(), mermaidCode);
    container.innerHTML = svg;

    // Add click handlers to nodes
    if (onNodeClick) {
      const svgElement = container.querySelector('svg');
      if (svgElement) {
        const nodes = svgElement.querySelectorAll('.node');
        nodes.forEach((node) => {
          const nodeId = node.id?.replace('flowchart-', '').replace(/-\d+$/, '');
          const stepName = stepNameToId.get(nodeId) as string | undefined;

          if (stepName) {
            (node as HTMLElement).style.cursor = 'pointer';

            // Highlight selected step
            if (selectedStep === stepName) {
              const rect = node.querySelector(
                'rect, polygon, circle, ellipse',
              ) as HTMLElement | null;
              if (rect) {
                rect.style.strokeWidth = '3px';
              }
            }

            node.addEventListener('click', (e) => {
              e.stopPropagation();
              onNodeClick(stepName);
            });

            node.addEventListener('mouseenter', () => {
              const rect = node.querySelector(
                'rect, polygon, circle, ellipse',
              ) as HTMLElement | null;
              if (rect) {
                rect.style.filter = 'brightness(1.2)';
              }
            });
            node.addEventListener('mouseleave', () => {
              const rect = node.querySelector(
                'rect, polygon, circle, ellipse',
              ) as HTMLElement | null;
              if (rect) {
                rect.style.filter = 'none';
              }
            });
          }
        });
      }
    }
  } catch (err) {
    console.error('Mermaid render error:', err);
    container.innerHTML = `<div class="empty-state"><div class="empty-text">Failed to render DAG: ${(err as Error).message}</div></div>`;
  }
}
