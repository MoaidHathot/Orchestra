import mermaid from 'mermaid';
import type { Orchestration, Step } from './types';

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

/** Make a string safe for use as a Mermaid node ID. */
function safeNodeId(name: string): string {
  return name.replace(/[^a-zA-Z0-9]/g, '_');
}

/** Escape double quotes for use inside Mermaid labels. */
function escLabel(text: string): string {
  return text.replace(/"/g, '#quot;');
}

/**
 * Get the step type from the step data. Returns lowercase.
 */
function getStepType(step: LooseStep | Step | string): string {
  if (typeof step === 'string') return 'prompt';
  const t = (step as LooseStep).type ?? (step as Step).type;
  return typeof t === 'string' ? t.toLowerCase() : 'prompt';
}

/**
 * Get a type icon prefix for the step label.
 */
function getTypeIcon(stepType: string): string {
  switch (stepType) {
    case 'http':
      return '\u{1F310} '; // globe
    case 'command':
      return '\u{1F4BB} '; // terminal/laptop
    case 'transform':
      return '\u{1F504} '; // transform arrows
    default:
      return '';
  }
}

/**
 * Build the metadata line for a step based on its type.
 */
function buildMetadataLines(step: LooseStep | Step | string): string[] {
  if (typeof step === 'string') return [];
  const s = step as LooseStep;
  const lines: string[] = [];
  const stepType = getStepType(step);

  switch (stepType) {
    case 'prompt': {
      // Model info
      const model = s.model as string | undefined;
      if (model) {
        const shortModel = model.split('/').pop() || model;
        lines.push(`<small>${escLabel(shortModel)}</small>`);
      }

      // MCPs info
      const mcps = (s.mcps || s.mcp) as unknown;
      if (mcps && Array.isArray(mcps) && mcps.length > 0) {
        const mcpNames = mcps
          .map((m: unknown) => (typeof m === 'string' ? m : (m as Record<string, unknown>).name))
          .join(', ');
        lines.push(`<small>MCPs: ${escLabel(mcpNames)}</small>`);
      } else if (mcps && typeof mcps === 'string') {
        lines.push(`<small>MCP: ${escLabel(mcps)}</small>`);
      }
      break;
    }
    case 'http': {
      const method = (s.method as string) || 'GET';
      const url = s.url as string | undefined;
      if (url) {
        // Show just the host/path, truncated
        let shortUrl = url;
        try {
          // Handle template URLs gracefully
          if (!url.includes('{{')) {
            const parsed = new URL(url);
            shortUrl = parsed.host + (parsed.pathname !== '/' ? parsed.pathname : '');
          }
        } catch {
          /* keep original */
        }
        if (shortUrl.length > 35) shortUrl = shortUrl.substring(0, 32) + '...';
        lines.push(`<small>${escLabel(method)} ${escLabel(shortUrl)}</small>`);
      } else {
        lines.push(`<small>${escLabel(method)}</small>`);
      }
      break;
    }
    case 'command': {
      const cmd = s.command as string | undefined;
      if (cmd) {
        const args = (s.arguments as string[]) || [];
        let cmdLine = cmd;
        if (args.length > 0) {
          cmdLine += ' ' + args.slice(0, 2).join(' ');
          if (args.length > 2) cmdLine += ' ...';
        }
        if (cmdLine.length > 35) cmdLine = cmdLine.substring(0, 32) + '...';
        lines.push(`<small>${escLabel(cmdLine)}</small>`);
      }
      break;
    }
    case 'transform': {
      const template = s.template as string | undefined;
      if (template) {
        let preview = template.replace(/\n/g, ' ').trim();
        if (preview.length > 35) preview = preview.substring(0, 32) + '...';
        lines.push(`<small>${escLabel(preview)}</small>`);
      }
      break;
    }
  }

  return lines;
}

/**
 * Build the Mermaid node declaration with the appropriate shape for its type.
 * - Prompt:    rounded rectangle  ("...")
 * - Http:      hexagon            {{"..."}}
 * - Command:   stadium/pill       (["..."])
 * - Transform: rhombus/diamond    {"..."}
 */
function buildNodeDeclaration(safeId: string, label: string, stepType: string): string {
  switch (stepType) {
    case 'http':
      return `  ${safeId}{{"${label}"}}\n`;
    case 'command':
      return `  ${safeId}(["${label}"])\n`;
    case 'transform':
      return `  ${safeId}{"${label}"}\n`;
    default:
      // Prompt / unknown = rounded rectangle
      return `  ${safeId}["${label}"]\n`;
  }
}

/**
 * Build subagent section for a step. Returns mermaid code for a subgraph
 * wrapping the step node and its subagent nodes.
 */
function buildSubagentSection(
  safeId: string,
  subagents: Array<{ name: string; displayName?: string; description?: string }>,
): { subgraphCode: string; subagentIds: string[] } {
  const subagentIds: string[] = [];
  let code = '';

  // Create a subgraph that groups the parent step with its subagents
  code += `  subgraph ${safeId}_agents [" "]\n`;
  code += `    direction LR\n`;

  // Add subagent nodes inside the subgraph
  for (const sa of subagents) {
    const subId = `${safeId}_sa_${safeNodeId(sa.name)}`;
    subagentIds.push(subId);
    const displayName = sa.displayName || sa.name;
    const desc = sa.description ? `<br/><small>${escLabel(sa.description.length > 40 ? sa.description.substring(0, 37) + '...' : sa.description)}</small>` : '';
    code += `    ${subId}[/"${escLabel(displayName)}${desc}"/]\n`;
  }

  code += `  end\n`;

  return { subgraphCode: code, subagentIds };
}

/**
 * Detect whether an error is caused by a stale dynamic-import chunk
 * (happens when the app was rebuilt while the page was still open).
 * Returns user-friendly HTML with a reload button when detected.
 */
function renderDagError(err: unknown, container: HTMLElement): void {
  const msg = (err as Error)?.message ?? String(err);
  const isStaleImport =
    /Failed to fetch dynamically imported module/i.test(msg) ||
    /Loading chunk [\w-]+ failed/i.test(msg) ||
    /import.*failed/i.test(msg);

  if (isStaleImport) {
    container.innerHTML =
      `<div class="empty-state" style="text-align:center">` +
      `<div class="empty-text" style="margin-bottom:8px">` +
      `The application has been updated since this page was loaded.</div>` +
      `<button class="btn btn-sm" onclick="location.reload()">Reload Page</button></div>`;
  } else {
    container.innerHTML =
      `<div class="empty-state"><div class="empty-text">Failed to render DAG: ${msg}</div></div>`;
  }
}

/** Shared Mermaid initialization config. */
export function getMermaidConfig() {
  return {
    startOnLoad: false,
    theme: 'dark' as const,
    securityLevel: 'antiscript' as const,
    themeVariables: {
      primaryColor: '#21262d',
      primaryTextColor: '#e6edf3',
      primaryBorderColor: '#30363d',
      lineColor: '#58a6ff',
      secondaryColor: '#161b22',
      tertiaryColor: '#0d1117',
    },
  };
}

/**
 * Attach click-safety and node click handlers to the rendered SVG.
 */
function attachSvgHandlers(
  container: HTMLElement,
  stepNameToId: Map<string, string>,
  onNodeClick?: (stepName: string) => void,
  selectedStep?: string,
): void {
  // Capture-phase interceptor to block accidental navigation
  container.addEventListener(
    'click',
    (e: Event) => {
      const target = e.target as HTMLElement;
      const anchor = target.closest?.('a');
      if (anchor && container.contains(anchor)) {
        e.preventDefault();
      }
    },
    true,
  );

  // Strip href/xlink:href from <a> elements
  const svgAnchors = container.querySelectorAll('svg a');
  svgAnchors.forEach((a) => {
    a.removeAttribute('href');
    a.removeAttribute('xlink:href');
  });

  if (!onNodeClick) return;

  const svgElement = container.querySelector('svg');
  if (!svgElement) return;

  const nodes = svgElement.querySelectorAll('.node');
  nodes.forEach((node) => {
    const nodeId = node.id?.replace('flowchart-', '').replace(/-\d+$/, '');
    const stepName = stepNameToId.get(nodeId);

    if (stepName) {
      (node as HTMLElement).style.cursor = 'pointer';

      // Highlight selected step
      if (selectedStep === stepName) {
        const rect = node.querySelector('rect, polygon, circle, ellipse, path') as HTMLElement | null;
        if (rect) {
          rect.style.strokeWidth = '3px';
        }
      }

      node.addEventListener('click', (e) => {
        e.preventDefault();
        e.stopPropagation();
        onNodeClick(stepName);
      });

      node.addEventListener('mouseenter', () => {
        const rect = node.querySelector('rect, polygon, circle, ellipse, path') as HTMLElement | null;
        if (rect) {
          rect.style.filter = 'brightness(1.2)';
          rect.style.stroke = '#58a6ff';
          rect.style.strokeWidth = '2px';
        }
      });
      node.addEventListener('mouseleave', () => {
        const rect = node.querySelector('rect, polygon, circle, ellipse, path') as HTMLElement | null;
        if (rect) {
          rect.style.filter = 'none';
          rect.style.stroke = '';
          rect.style.strokeWidth = selectedStep === stepName ? '3px' : '';
        }
      });
    }
  });
}

// ---------------------------------------------------------------------------
// Type-based class definitions for the definition DAG
// ---------------------------------------------------------------------------
function getDefinitionClassDefs(): string {
  let code = '\n';
  // Step type styles (cursor is applied post-render via JS to avoid Mermaid parser issues)
  code += '  classDef promptStep fill:#1a2332,stroke:#58a6ff,color:#e6edf3\n';
  code += '  classDef httpStep fill:#1a2a1a,stroke:#3fb950,color:#e6edf3\n';
  code += '  classDef commandStep fill:#2a1a2a,stroke:#a371f7,color:#e6edf3\n';
  code += '  classDef transformStep fill:#2a2a1a,stroke:#d29922,color:#e6edf3\n';
  // Handler indicator styles
  code += '  classDef hasInputHandler fill:#1f3a1f,stroke:#3fb950,color:#e6edf3\n';
  code += '  classDef hasOutputHandler fill:#1f2a3f,stroke:#58a6ff,color:#e6edf3\n';
  code += '  classDef hasBothHandlers fill:#2d2a1f,stroke:#d29922,color:#e6edf3\n';
  code += '  classDef hasLoop fill:#2d1f2d,stroke:#a371f7,color:#e6edf3\n';
  // Subagent node style (font-size applied via <small> tags in labels)
  code += '  classDef subagentNode fill:#161b22,stroke:#8b949e,color:#8b949e\n';
  return code;
}

// ---------------------------------------------------------------------------
// generateDefinitionDagCode -- generates Mermaid code for an orchestration
// definition DAG. Extracted as a pure function for testability.
// ---------------------------------------------------------------------------
export function generateDefinitionDagCode(
  steps: Step[],
): { mermaidCode: string; stepNameToId: Map<string, string> } {
  let mermaidCode = 'graph TD\n';
  const stepNameToId = new Map<string, string>();

  // Collect steps by type for class application
  const typeGroups: Record<string, string[]> = {
    prompt: [],
    http: [],
    command: [],
    transform: [],
  };

  const stepsWithInputHandler: string[] = [];
  const stepsWithOutputHandler: string[] = [];
  const stepsWithBothHandlers: string[] = [];
  const stepsWithLoop: string[] = [];
  const subagentNodeIds: string[] = [];

  const loopEdges: string[] = [];
  const subgraphSections: string[] = [];
  const subagentEdges: string[] = [];

  steps.forEach((step) => {
    const s = step as unknown as LooseStep;
    const safeId = safeNodeId(step.name);
    stepNameToId.set(safeId, step.name);

    const stepType = getStepType(step);
    const typeIcon = getTypeIcon(stepType);

    // Build label
    let labelLine1 = typeIcon + step.name;
    const hasInput = s.inputHandlerPrompt;
    const hasOutput = s.outputHandlerPrompt;
    const hasLoop = s.loopConfig || s.loop;

    if (hasInput && hasOutput) {
      labelLine1 += ' \u21C4'; // ⇄
      stepsWithBothHandlers.push(safeId);
    } else if (hasInput) {
      labelLine1 += ' \u21E2'; // ⇢
      stepsWithInputHandler.push(safeId);
    } else if (hasOutput) {
      labelLine1 += ' \u21E0'; // ⇠
      stepsWithOutputHandler.push(safeId);
    }

    if (hasLoop) {
      labelLine1 += ' \u21BB'; // ↻
      stepsWithLoop.push(safeId);
    }

    // Add type badge
    if (stepType !== 'prompt') {
      labelLine1 += ` <small>[${stepType.toUpperCase()}]</small>`;
    }

    const labelParts = [escLabel(labelLine1)];

    // Add type-specific metadata
    const metaLines = buildMetadataLines(step);
    labelParts.push(...metaLines);

    // Subagent count indicator in the label
    const subagents = (s.subagents as Array<{ name: string; displayName?: string; description?: string }>) || [];
    if (subagents.length > 0) {
      labelParts.push(`<small>\u{1F916} ${subagents.length} subagent${subagents.length > 1 ? 's' : ''}</small>`);
    }

    const label = labelParts.join('<br/>');

    // Build the node with shape based on type
    mermaidCode += buildNodeDeclaration(safeId, label, stepType);

    // Track type for class assignment
    if (typeGroups[stepType]) {
      typeGroups[stepType].push(safeId);
    } else {
      typeGroups.prompt.push(safeId);
    }

    // Dependencies
    if (step.dependsOn && step.dependsOn.length > 0) {
      step.dependsOn.forEach((dep) => {
        const safeDep = safeNodeId(dep);
        mermaidCode += `  ${safeDep} --> ${safeId}\n`;
      });
    }

    // Loop edges
    if (hasLoop) {
      const loopConfig = (s.loopConfig || s.loop) as Record<string, unknown>;
      const targetName = (loopConfig.target || loopConfig.Target) as string | undefined;
      if (targetName) {
        const safeTarget = safeNodeId(targetName);
        const maxIter = loopConfig.maxIterations || loopConfig.MaxIterations || '?';
        loopEdges.push(`  ${safeId} -.->|"loop (max ${maxIter})"| ${safeTarget}\n`);
      }
    }

    // Subagent subgraph
    if (subagents.length > 0) {
      const { subgraphCode, subagentIds } = buildSubagentSection(safeId, subagents);
      subgraphSections.push(subgraphCode);
      subagentNodeIds.push(...subagentIds);
      // Connect the parent step to the subagent subgraph with a dotted edge
      subagentEdges.push(`  ${safeId} -.-o ${subagentIds[0]}\n`);
    }
  });

  // Add loop edges
  loopEdges.forEach((edge) => {
    mermaidCode += edge;
  });

  // Add subagent subgraphs
  subgraphSections.forEach((section) => {
    mermaidCode += section;
  });

  // Add subagent edges
  subagentEdges.forEach((edge) => {
    mermaidCode += edge;
  });

  // Class definitions
  mermaidCode += getDefinitionClassDefs();

  // Apply type-based classes
  for (const [type, ids] of Object.entries(typeGroups)) {
    if (ids.length > 0) {
      mermaidCode += `  class ${ids.join(',')} ${type}Step\n`;
    }
  }

  // Apply handler/loop classes (override type styles for steps with handlers)
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
  if (subagentNodeIds.length > 0) {
    mermaidCode += `  class ${subagentNodeIds.join(',')} subagentNode\n`;
  }

  return { mermaidCode, stepNameToId };
}

// ---------------------------------------------------------------------------
// renderMermaidDag -- renders the DAG for an orchestration *definition*
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

  const { mermaidCode, stepNameToId } = generateDefinitionDagCode(steps);

  try {
    mermaid.initialize(getMermaidConfig());

    const { svg } = await mermaid.render('dag-' + Date.now(), mermaidCode);
    container.innerHTML = svg;

    // Style subagent subgraphs with a subtle border
    const svgEl = container.querySelector('svg');
    if (svgEl) {
      const subgraphs = svgEl.querySelectorAll('.cluster rect');
      subgraphs.forEach((rect) => {
        (rect as HTMLElement).style.fill = 'rgba(139, 148, 158, 0.05)';
        (rect as HTMLElement).style.stroke = 'rgba(139, 148, 158, 0.3)';
        (rect as HTMLElement).style.strokeWidth = '1px';
        (rect as HTMLElement).style.strokeDasharray = '5,5';
        (rect as HTMLElement).style.rx = '8';
      });
    }

    attachSvgHandlers(container, stepNameToId, onNodeClick);
  } catch (err) {
    console.error('Mermaid render error:', err);
    renderDagError(err, container);
  }
}

// ---------------------------------------------------------------------------
// generateExecutionDagCode -- generates Mermaid code for an execution DAG
// with status coloring. Extracted as a pure function for testability.
// ---------------------------------------------------------------------------
export function generateExecutionDagCode(
  steps: Step[],
  stepStatuses: Record<string, string>,
): { mermaidCode: string; stepNameToId: Map<string, string> } {
  let mermaidCode = 'graph TD\n';
  const stepNameToId = new Map<string, string>();
  const statusGroups: Record<string, string[]> = {
    pending: [],
    running: [],
    completed: [],
    completedEarly: [],
    failed: [],
    cancelled: [],
    skipped: [],
  };

  const loopEdges: string[] = [];
  const subgraphSections: string[] = [];
  const subagentEdges: string[] = [];
  const subagentNodeIds: string[] = [];

  steps.forEach((step) => {
    const stepName = getStepName(step as unknown as LooseStep | string);
    if (!stepName) return;

    const safeId = safeNodeId(stepName);
    stepNameToId.set(safeId, stepName);

    const s = step as unknown as LooseStep;
    const stepType = getStepType(step as unknown as LooseStep | string);
    const typeIcon = getTypeIcon(stepType);

    // Status icon
    const status = stepStatuses[stepName] || 'pending';
    let statusIcon = '';
    switch (status) {
      case 'running':
        statusIcon = ' ...';
        break;
      case 'completed':
        statusIcon = ' \u2713'; // ✓
        break;
      case 'failed':
        statusIcon = ' \u2717'; // ✗
        break;
      case 'cancelled':
        statusIcon = ' \u2298'; // ⊘
        break;
      case 'skipped':
        statusIcon = ' \u25CB'; // ○
        break;
      case 'noaction':
        statusIcon = ' \u2014'; // —
        break;
      case 'completed_early':
        statusIcon = ' \u23F9'; // ⏹
        break;
    }

    // Loop indicator
    const hasLoop = typeof step === 'object' && (s.loopConfig || s.loop);
    const loopIndicator = hasLoop ? ' \u21BB' : '';

    // Type badge
    const typeBadge = stepType !== 'prompt' ? ` <small>[${stepType.toUpperCase()}]</small>` : '';

    // Build label
    const labelParts = [escLabel(`${typeIcon}${stepName}${statusIcon}${loopIndicator}${typeBadge}`)];

    // Add type-specific metadata
    if (typeof step === 'object') {
      const metaLines = buildMetadataLines(step as unknown as LooseStep);
      labelParts.push(...metaLines);
    }

    // Subagent indicator
    const subagents = typeof step === 'object'
      ? (s.subagents as Array<{ name: string; displayName?: string; description?: string }>) || []
      : [];
    if (subagents.length > 0) {
      labelParts.push(`<small>\u{1F916} ${subagents.length} subagent${subagents.length > 1 ? 's' : ''}</small>`);
    }

    const label = labelParts.join('<br/>');

    // Build node with shape based on type
    mermaidCode += buildNodeDeclaration(safeId, label, stepType);

    // Categorize by status
    const mappedStatus = status === 'completed_early' ? 'completedEarly' : status;
    const group = statusGroups[mappedStatus] || statusGroups.pending;
    group.push(safeId);

    // Dependencies
    const dependsOn = typeof step === 'object' ? step.dependsOn : null;
    if (dependsOn && dependsOn.length > 0) {
      dependsOn.forEach((dep) => {
        const safeDep = safeNodeId(dep);
        mermaidCode += `  ${safeDep} --> ${safeId}\n`;
      });
    }

    // Loop edges
    if (hasLoop) {
      const loopConfig = (s.loopConfig || s.loop) as Record<string, unknown>;
      const targetName = (loopConfig.target || loopConfig.Target) as string | undefined;
      if (targetName) {
        const safeTarget = safeNodeId(targetName);
        const maxIter = loopConfig.maxIterations || loopConfig.MaxIterations || '?';
        loopEdges.push(`  ${safeId} -.->|"loop (max ${maxIter})"| ${safeTarget}\n`);
      }
    }

    // Subagent subgraph
    if (subagents.length > 0) {
      const { subgraphCode, subagentIds } = buildSubagentSection(safeId, subagents);
      subgraphSections.push(subgraphCode);
      subagentNodeIds.push(...subagentIds);
      subagentEdges.push(`  ${safeId} -.-o ${subagentIds[0]}\n`);
    }
  });

  // Add loop edges
  loopEdges.forEach((edge) => {
    mermaidCode += edge;
  });

  // Add subagent subgraphs
  subgraphSections.forEach((section) => {
    mermaidCode += section;
  });

  // Add subagent edges
  subagentEdges.forEach((edge) => {
    mermaidCode += edge;
  });

  // Status-based styling (cursor is applied post-render via JS to avoid Mermaid parser issues)
  mermaidCode += '\n  classDef pending fill:#21262d,stroke:#484f58,color:#8b949e\n';
  mermaidCode += '  classDef running fill:#0d2847,stroke:#58a6ff,color:#58a6ff\n';
  mermaidCode += '  classDef completed fill:#0d331a,stroke:#3fb950,color:#3fb950\n';
  mermaidCode += '  classDef failed fill:#3d1418,stroke:#f85149,color:#f85149\n';
  mermaidCode += '  classDef cancelled fill:#3d2e0d,stroke:#d29922,color:#d29922\n';
  mermaidCode += '  classDef skipped fill:#21262d,stroke:#484f58,color:#6e7681\n';
  mermaidCode += '  classDef noaction fill:#21262d,stroke:#8b949e,color:#8b949e\n';
  mermaidCode += '  classDef completedEarly fill:#0c2a3d,stroke:#38bdf8,color:#38bdf8\n';
  mermaidCode += '  classDef subagentNode fill:#161b22,stroke:#8b949e,color:#8b949e\n';

  // Apply status classes
  for (const [status, ids] of Object.entries(statusGroups)) {
    if (ids.length > 0) {
      mermaidCode += `  class ${ids.join(',')} ${status}\n`;
    }
  }

  // Apply subagent node class
  if (subagentNodeIds.length > 0) {
    mermaidCode += `  class ${subagentNodeIds.join(',')} subagentNode\n`;
  }

  return { mermaidCode, stepNameToId };
}

// ---------------------------------------------------------------------------
// renderExecutionDag -- renders a DAG with execution status coloring
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

  const { mermaidCode, stepNameToId } = generateExecutionDagCode(steps, stepStatuses);

  try {
    mermaid.initialize(getMermaidConfig());

    const { svg } = await mermaid.render('exec-dag-' + Date.now(), mermaidCode);
    container.innerHTML = svg;

    // Style subagent subgraphs
    const svgEl = container.querySelector('svg');
    if (svgEl) {
      const subgraphs = svgEl.querySelectorAll('.cluster rect');
      subgraphs.forEach((rect) => {
        (rect as HTMLElement).style.fill = 'rgba(139, 148, 158, 0.05)';
        (rect as HTMLElement).style.stroke = 'rgba(139, 148, 158, 0.3)';
        (rect as HTMLElement).style.strokeWidth = '1px';
        (rect as HTMLElement).style.strokeDasharray = '5,5';
        (rect as HTMLElement).style.rx = '8';
      });
    }

    attachSvgHandlers(container, stepNameToId, onNodeClick, selectedStep);
  } catch (err) {
    console.error('Mermaid render error:', err);
    renderDagError(err, container);
  }
}
