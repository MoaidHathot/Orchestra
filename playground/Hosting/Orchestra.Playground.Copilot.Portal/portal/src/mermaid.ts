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
 * Get a compact symbol for the step type.
 * Prompt steps get no symbol (they're the default/most common).
 * Symbols are small, monochrome-friendly, and universally readable.
 */
function getTypeBadge(stepType: string): string {
  switch (stepType) {
    case 'http':
      return '\u2197'; // ↗  — request going out
    case 'command':
      return '>_';     // >_ — terminal prompt
    case 'transform':
      return '\u27F9'; // ⟹  — input transforms to output
    default:
      return '';
  }
}

/**
 * Truncate a step name for display inside a node.
 * - 0-22 chars: full name
 * - 23+ chars: truncate with ellipsis
 */
function truncateName(name: string, maxLen = 22): string {
  if (name.length <= maxLen) return name;
  return name.substring(0, maxLen - 1) + '\u2026'; // …
}

/**
 * Build a single compact metadata line for a step based on its type.
 * Returns at most one line to keep nodes compact.
 */
function buildMetadataLine(step: LooseStep | Step | string): string | null {
  if (typeof step === 'string') return null;
  const s = step as LooseStep;
  const stepType = getStepType(step);

  switch (stepType) {
    case 'prompt': {
      const model = s.model as string | undefined;
      if (model) {
        let shortModel = model.split('/').pop() || model;
        if (shortModel.length > 24) shortModel = shortModel.substring(0, 21) + '...';
        return shortModel;
      }
      return null;
    }
    case 'http': {
      const method = (s.method as string) || 'GET';
      const url = s.url as string | undefined;
      if (url) {
        let shortUrl = url;
        try {
          if (!url.includes('{{')) {
            const parsed = new URL(url);
            shortUrl = parsed.host + (parsed.pathname !== '/' ? parsed.pathname : '');
          }
        } catch {
          /* keep original */
        }
        if (shortUrl.length > 28) shortUrl = shortUrl.substring(0, 25) + '...';
        return `${method} ${shortUrl}`;
      }
      return method;
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
        if (cmdLine.length > 28) cmdLine = cmdLine.substring(0, 25) + '...';
        return cmdLine;
      }
      return null;
    }
    case 'transform': {
      const template = s.template as string | undefined;
      if (template) {
        let preview = template.replace(/\n/g, ' ').trim();
        if (preview.length > 28) preview = preview.substring(0, 25) + '...';
        return preview;
      }
      return null;
    }
  }

  return null;
}

/**
 * Build the Mermaid node declaration.
 * All step types use rounded rectangle for uniform width and compactness.
 */
function buildNodeDeclaration(safeId: string, label: string): string {
  return `  ${safeId}["${label}"]\n`;
}

/**
 * Build subagent inline label content for a step.
 * Renders as a compact list of subagent names within the parent node label.
 * Uses ▸ triangles to visually communicate "child agents branching off".
 */
function buildSubagentInlineLabel(
  subagents: Array<{ name: string; displayName?: string; description?: string }>,
): string {
  if (subagents.length <= 3) {
    // Show individual names as small triangles
    const names = subagents.map(sa => {
      const displayName = sa.displayName || sa.name;
      const short = displayName.length > 10 ? displayName.substring(0, 9) + '\u2026' : displayName;
      return escLabel(short);
    });
    return `<small>\u25B8 ${names.join(' \u25B8 ')}</small>`;
  }
  // Too many — just show count
  return `<small>\u25B8 ${subagents.length} subagents</small>`;
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

// ---------------------------------------------------------------------------
// Color mapping for step types (used by post-render SVG styling)
// ---------------------------------------------------------------------------
const TYPE_COLORS: Record<string, string> = {
  prompt: '#58a6ff',   // blue
  http: '#3fb950',     // green
  command: '#a371f7',  // purple
  transform: '#d29922', // yellow
};

/**
 * Post-render: apply a left-side accent border to each node based on step type.
 * This provides clear type differentiation without relying on different shapes.
 */
function applyTypeAccentBorders(
  svgElement: SVGElement,
  stepTypeMap: Map<string, string>, // safeId -> stepType
  stepNameToId: Map<string, string>, // safeId -> stepName
): void {
  const nodes = svgElement.querySelectorAll('.node');
  nodes.forEach((node) => {
    const nodeId = node.id?.replace('flowchart-', '').replace(/-\d+$/, '');
    if (!nodeId) return;

    const stepName = stepNameToId.get(nodeId);
    if (!stepName) return;

    // Find the type for this node
    let stepType: string | undefined;
    for (const [sid, stype] of stepTypeMap.entries()) {
      if (sid === nodeId) {
        stepType = stype;
        break;
      }
    }
    if (!stepType) return;

    const color = TYPE_COLORS[stepType] || TYPE_COLORS.prompt;
    const rect = node.querySelector('rect') as SVGRectElement | null;
    if (!rect) return;

    // Create a left accent bar by overlaying a narrow rect
    const x = parseFloat(rect.getAttribute('x') || '0');
    const y = parseFloat(rect.getAttribute('y') || '0');
    const height = parseFloat(rect.getAttribute('height') || '0');
    const rx = parseFloat(rect.getAttribute('rx') || '0');

    const accent = document.createElementNS('http://www.w3.org/2000/svg', 'rect');
    accent.setAttribute('x', String(x));
    accent.setAttribute('y', String(y));
    accent.setAttribute('width', '4');
    accent.setAttribute('height', String(height));
    accent.setAttribute('rx', String(Math.min(rx, 2)));
    accent.setAttribute('fill', color);
    accent.setAttribute('class', 'dag-type-accent');

    // Insert the accent bar before the label text (after the background rect)
    rect.parentNode?.insertBefore(accent, rect.nextSibling);
  });
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
        const rect = node.querySelector('rect:not(.dag-type-accent)') as HTMLElement | null;
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
        const rect = node.querySelector('rect:not(.dag-type-accent)') as HTMLElement | null;
        if (rect) {
          rect.style.filter = 'brightness(1.2)';
          rect.style.stroke = '#58a6ff';
          rect.style.strokeWidth = '2px';
        }
      });
      node.addEventListener('mouseleave', () => {
        const rect = node.querySelector('rect:not(.dag-type-accent)') as HTMLElement | null;
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
  // Step type styles — all use rounded rectangle, differentiated by fill/stroke color
  code += '  classDef promptStep fill:#1a2332,stroke:#58a6ff,color:#e6edf3\n';
  code += '  classDef httpStep fill:#1a2a1a,stroke:#3fb950,color:#e6edf3\n';
  code += '  classDef commandStep fill:#2a1a2a,stroke:#a371f7,color:#e6edf3\n';
  code += '  classDef transformStep fill:#2a2a1a,stroke:#d29922,color:#e6edf3\n';
  // Handler indicator styles
  code += '  classDef hasInputHandler fill:#1f3a1f,stroke:#3fb950,color:#e6edf3\n';
  code += '  classDef hasOutputHandler fill:#1f2a3f,stroke:#58a6ff,color:#e6edf3\n';
  code += '  classDef hasBothHandlers fill:#2d2a1f,stroke:#d29922,color:#e6edf3\n';
  code += '  classDef hasLoop fill:#2d1f2d,stroke:#a371f7,color:#e6edf3\n';
  // Disabled step style (muted colors, dashed border)
  code += '  classDef disabledStep fill:#161b22,stroke:#484f58,color:#6e7681,stroke-dasharray:5 5\n';
  return code;
}

// ---------------------------------------------------------------------------
// generateDefinitionDagCode -- generates Mermaid code for an orchestration
// definition DAG. Extracted as a pure function for testability.
// ---------------------------------------------------------------------------
export function generateDefinitionDagCode(
  steps: Step[],
): { mermaidCode: string; stepNameToId: Map<string, string>; stepTypeMap: Map<string, string> } {
  let mermaidCode = 'graph TD\n';
  const stepNameToId = new Map<string, string>();
  const stepTypeMap = new Map<string, string>(); // safeId -> stepType

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
  const disabledSteps: string[] = [];

  const loopEdges: string[] = [];

  steps.forEach((step) => {
    const s = step as unknown as LooseStep;
    const safeId = safeNodeId(step.name);
    stepNameToId.set(safeId, step.name);

    const stepType = getStepType(step);
    stepTypeMap.set(safeId, stepType);

    const typeBadge = getTypeBadge(stepType);
    const hasInput = s.inputHandlerPrompt;
    const hasOutput = s.outputHandlerPrompt;
    const hasLoop = s.loopConfig || s.loop;

    // Build line 1: [BADGE] step-name [indicators]
    let line1 = '';
    if (typeBadge) {
      line1 += `<small><b>${typeBadge}</b></small> `;
    }
    line1 += truncateName(step.name);

    // Handler/loop indicators as compact symbols
    const indicators: string[] = [];
    if (hasInput && hasOutput) {
      indicators.push('\u21C4'); // ⇄
      stepsWithBothHandlers.push(safeId);
    } else if (hasInput) {
      indicators.push('\u21E2'); // ⇢
      stepsWithInputHandler.push(safeId);
    } else if (hasOutput) {
      indicators.push('\u21E0'); // ⇠
      stepsWithOutputHandler.push(safeId);
    }
    if (hasLoop) {
      indicators.push('\u21BB'); // ↻
      stepsWithLoop.push(safeId);
    }
    const hasSkills = Array.isArray(s.skillDirectories) && (s.skillDirectories as string[]).length > 0;
    if (hasSkills) {
      indicators.push('\uD83D\uDCD6'); // 📖
    }
    if (indicators.length > 0) {
      line1 += ` ${indicators.join(' ')}`;
    }

    // Disabled indicator
    const isDisabled = step.enabled === false || (s.enabled === false);
    if (isDisabled) {
      disabledSteps.push(safeId);
    }

    const labelParts = [escLabel(line1)];

    // Line 2: single metadata line
    const metaLine = buildMetadataLine(step);
    if (metaLine) {
      labelParts.push(`<small>${escLabel(metaLine)}</small>`);
    }

    // Subagents inline (line 3, only if subagents exist)
    const subagents = (s.subagents as Array<{ name: string; displayName?: string; description?: string }>) || [];
    if (subagents.length > 0) {
      labelParts.push(buildSubagentInlineLabel(subagents));
    }

    // Disabled badge
    if (isDisabled) {
      labelParts.push(`<small>DISABLED</small>`);
    }

    const label = labelParts.join('<br/>');
    mermaidCode += buildNodeDeclaration(safeId, label);

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
  });

  // Add loop edges
  loopEdges.forEach((edge) => {
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

  // Apply disabled class last so it overrides type/handler styles
  if (disabledSteps.length > 0) {
    mermaidCode += `  class ${disabledSteps.join(',')} disabledStep\n`;
  }

  return { mermaidCode, stepNameToId, stepTypeMap };
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

  const { mermaidCode, stepNameToId, stepTypeMap } = generateDefinitionDagCode(steps);

  try {
    mermaid.initialize(getMermaidConfig());

    const { svg } = await mermaid.render('dag-' + Date.now(), mermaidCode);
    container.innerHTML = svg;

    // Apply type accent borders post-render
    const svgEl = container.querySelector('svg') as SVGElement | null;
    if (svgEl) {
      applyTypeAccentBorders(svgEl, stepTypeMap, stepNameToId);
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
): { mermaidCode: string; stepNameToId: Map<string, string>; stepTypeMap: Map<string, string> } {
  let mermaidCode = 'graph TD\n';
  const stepNameToId = new Map<string, string>();
  const stepTypeMap = new Map<string, string>(); // safeId -> stepType
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
  const disabledSteps: string[] = [];

  steps.forEach((step) => {
    const stepName = getStepName(step as unknown as LooseStep | string);
    if (!stepName) return;

    const safeId = safeNodeId(stepName);
    stepNameToId.set(safeId, stepName);

    const s = step as unknown as LooseStep;
    const stepType = getStepType(step as unknown as LooseStep | string);
    stepTypeMap.set(safeId, stepType);

    const typeBadge = getTypeBadge(stepType);

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

    // Skill indicator
    const hasSkills = typeof step === 'object' &&
      Array.isArray(s.skillDirectories) && (s.skillDirectories as string[]).length > 0;
    const skillIndicator = hasSkills ? ' \uD83D\uDCD6' : '';

    // Disabled indicator
    const isDisabled = (typeof step === 'object') &&
      ((step as Step).enabled === false || s.enabled === false);
    if (isDisabled) {
      disabledSteps.push(safeId);
    }

    // Build line 1: [BADGE] step-name [status] [loop] [skill]
    let line1 = '';
    if (typeBadge) {
      line1 += `<small><b>${typeBadge}</b></small> `;
    }
    line1 += `${truncateName(stepName)}${statusIcon}${loopIndicator}${skillIndicator}`;

    const labelParts = [escLabel(line1)];

    // Line 2: single metadata line
    if (typeof step === 'object') {
      const metaLine = buildMetadataLine(step as unknown as LooseStep);
      if (metaLine) {
        labelParts.push(`<small>${escLabel(metaLine)}</small>`);
      }
    }

    // Subagents inline
    const subagents = typeof step === 'object'
      ? (s.subagents as Array<{ name: string; displayName?: string; description?: string }>) || []
      : [];
    if (subagents.length > 0) {
      labelParts.push(buildSubagentInlineLabel(subagents));
    }

    // Disabled badge
    if (isDisabled) {
      labelParts.push(`<small>DISABLED</small>`);
    }

    const label = labelParts.join('<br/>');
    mermaidCode += buildNodeDeclaration(safeId, label);

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
  });

  // Add loop edges
  loopEdges.forEach((edge) => {
    mermaidCode += edge;
  });

  // Status-based styling
  mermaidCode += '\n  classDef pending fill:#21262d,stroke:#484f58,color:#8b949e\n';
  mermaidCode += '  classDef running fill:#0d2847,stroke:#58a6ff,color:#58a6ff\n';
  mermaidCode += '  classDef completed fill:#0d331a,stroke:#3fb950,color:#3fb950\n';
  mermaidCode += '  classDef failed fill:#3d1418,stroke:#f85149,color:#f85149\n';
  mermaidCode += '  classDef cancelled fill:#3d2e0d,stroke:#d29922,color:#d29922\n';
  mermaidCode += '  classDef skipped fill:#21262d,stroke:#484f58,color:#6e7681\n';
  mermaidCode += '  classDef noaction fill:#21262d,stroke:#8b949e,color:#8b949e\n';
  mermaidCode += '  classDef completedEarly fill:#0c2a3d,stroke:#38bdf8,color:#38bdf8\n';
  // Disabled step style (muted colors, dashed border) - applied last to override status styles
  mermaidCode += '  classDef disabledStep fill:#161b22,stroke:#484f58,color:#6e7681,stroke-dasharray:5 5\n';

  // Apply status classes
  for (const [status, ids] of Object.entries(statusGroups)) {
    if (ids.length > 0) {
      mermaidCode += `  class ${ids.join(',')} ${status}\n`;
    }
  }

  // Apply disabled class last so it overrides status styles
  if (disabledSteps.length > 0) {
    mermaidCode += `  class ${disabledSteps.join(',')} disabledStep\n`;
  }

  return { mermaidCode, stepNameToId, stepTypeMap };
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

  const { mermaidCode, stepNameToId, stepTypeMap } = generateExecutionDagCode(steps, stepStatuses);

  try {
    mermaid.initialize(getMermaidConfig());

    const { svg } = await mermaid.render('exec-dag-' + Date.now(), mermaidCode);
    container.innerHTML = svg;

    // Apply type accent borders post-render
    const svgEl = container.querySelector('svg') as SVGElement | null;
    if (svgEl) {
      applyTypeAccentBorders(svgEl, stepTypeMap, stepNameToId);
    }

    attachSvgHandlers(container, stepNameToId, onNodeClick, selectedStep);
  } catch (err) {
    console.error('Mermaid render error:', err);
    renderDagError(err, container);
  }
}
