import React, { useState, useCallback, useRef, useEffect, useMemo } from 'react';
import { Icons } from '../../icons';
import { useFocusTrap } from '../../hooks/useFocusTrap';

/* ------------------------------------------------------------------ */
/*  Types                                                              */
/* ------------------------------------------------------------------ */

interface Props {
  open: boolean;
  onClose: () => void;
  onSave: (json: string) => void;
}

interface BuilderStep {
  id: string;
  name: string;
  type: 'Prompt' | 'Http' | 'Transform';
  dependsOn: string[];
  x: number;
  y: number;
  // Prompt fields
  systemPrompt?: string;
  userPrompt?: string;
  model?: string;
  // Http fields
  method?: string;
  url?: string;
  headers?: Record<string, string>;
  body?: string;
  // Transform fields
  template?: string;
  // Common optional
  parameters?: string[];
  timeoutSeconds?: number;
}

interface OrchestrationConfig {
  name: string;
  description: string;
  version: string;
  timeoutSeconds: number;
  triggerType: 'none' | 'webhook' | 'scheduler' | 'loop';
  triggerEnabled: boolean;
  // webhook
  webhookSecret: string;
  webhookMaxConcurrent: number;
  // scheduler
  schedulerCron: string;
  schedulerIntervalSeconds: number;
  // loop
  loopDelaySeconds: number;
  loopMaxIterations: number;
}

/* ------------------------------------------------------------------ */
/*  Constants                                                          */
/* ------------------------------------------------------------------ */

const NODE_W = 200;
const NODE_H = 60;
const PORT_R = 8;
const MIN_ZOOM = 0.25;
const MAX_ZOOM = 3.0;
const ZOOM_STEP = 0.001;

const DEFAULT_CONFIG: OrchestrationConfig = {
  name: 'New Orchestration',
  description: '',
  version: '1.0.0',
  timeoutSeconds: 300,
  triggerType: 'none',
  triggerEnabled: false,
  webhookSecret: '',
  webhookMaxConcurrent: 1,
  schedulerCron: '',
  schedulerIntervalSeconds: 60,
  loopDelaySeconds: 5,
  loopMaxIterations: 100,
};

const TYPE_COLORS: Record<BuilderStep['type'], string> = {
  Prompt: 'var(--accent)',
  Http: 'var(--orange)',
  Transform: 'var(--purple)',
};

/* ------------------------------------------------------------------ */
/*  Helpers                                                            */
/* ------------------------------------------------------------------ */

let stepCounter = 0;
function nextStepId(): string {
  stepCounter += 1;
  return `step_${Date.now()}_${stepCounter}`;
}

function nextStepName(existing: BuilderStep[]): string {
  let idx = existing.length + 1;
  const names = new Set(existing.map(s => s.name));
  while (names.has(`step-${idx}`)) {
    idx += 1;
  }
  return `step-${idx}`;
}

/** Check if `target` is reachable from `source` via dependsOn edges (would create a cycle). */
function wouldCreateCycle(
  steps: BuilderStep[],
  sourceId: string,
  targetId: string,
): boolean {
  if (sourceId === targetId) return true;
  const byId = new Map(steps.map(s => [s.id, s]));
  const visited = new Set<string>();
  const stack = [sourceId];
  while (stack.length > 0) {
    const current = stack.pop()!;
    if (visited.has(current)) continue;
    visited.add(current);
    const node = byId.get(current);
    if (!node) continue;
    for (const depId of node.dependsOn) {
      if (depId === targetId) return true;
      stack.push(depId);
    }
  }
  return false;
}

/** Build a cubic bezier path from the output port of `from` to the input port of `to`. */
function edgePath(from: BuilderStep, to: BuilderStep): string {
  const x1 = from.x + NODE_W / 2;
  const y1 = from.y + NODE_H;
  const x2 = to.x + NODE_W / 2;
  const y2 = to.y;
  const dy = Math.abs(y2 - y1);
  const cp = Math.max(dy * 0.5, 40);
  return `M ${x1} ${y1} C ${x1} ${y1 + cp}, ${x2} ${y2 - cp}, ${x2} ${y2}`;
}

/* ------------------------------------------------------------------ */
/*  CSS (inline <style>)                                               */
/* ------------------------------------------------------------------ */

const BUILDER_CSS = `
.builder-overlay {
  position: fixed;
  inset: 0;
  background: rgba(0,0,0,0.85);
  z-index: 2000;
  display: flex;
  flex-direction: column;
  opacity: 0;
  visibility: hidden;
  transition: all 0.2s ease;
}
.builder-overlay.visible {
  opacity: 1;
  visibility: visible;
}

/* Header */
.builder-header {
  display: flex;
  align-items: center;
  gap: 12px;
  padding: 10px 16px;
  background: var(--bg-secondary);
  border-bottom: 1px solid var(--border);
  flex-shrink: 0;
}
.builder-header-input {
  background: var(--bg);
  border: 1px solid var(--border);
  border-radius: 6px;
  color: var(--text);
  padding: 6px 10px;
  font-size: 14px;
  font-weight: 600;
  outline: none;
  transition: border-color 0.15s;
}
.builder-header-input:focus {
  border-color: var(--accent);
}
.builder-header-input.desc {
  font-weight: 400;
  font-size: 12px;
  flex: 1;
  min-width: 120px;
  color: var(--text-muted);
}

/* Body */
.builder-body {
  display: flex;
  flex: 1;
  overflow: hidden;
}

/* Left panel */
.builder-left {
  width: 180px;
  min-width: 180px;
  background: var(--bg-secondary);
  border-right: 1px solid var(--border);
  display: flex;
  flex-direction: column;
  padding: 12px;
  gap: 8px;
  overflow-y: auto;
  flex-shrink: 0;
}
.builder-left-title {
  font-size: 10px;
  font-weight: 600;
  text-transform: uppercase;
  letter-spacing: 0.8px;
  color: var(--text-muted);
  margin-bottom: 2px;
}
.builder-palette-btn {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 8px 10px;
  border-radius: 6px;
  border: 1px solid var(--border);
  background: var(--surface);
  color: var(--text);
  cursor: pointer;
  font-size: 12px;
  font-weight: 500;
  transition: all 0.15s;
}
.builder-palette-btn:hover {
  background: var(--surface-hover);
  border-color: var(--border-light);
}
.builder-palette-dot {
  width: 10px;
  height: 10px;
  border-radius: 3px;
  flex-shrink: 0;
}
.builder-settings-toggle {
  margin-top: auto;
  display: flex;
  align-items: center;
  gap: 6px;
  padding: 8px 10px;
  border-radius: 6px;
  border: 1px solid var(--border);
  background: var(--surface);
  color: var(--text-muted);
  cursor: pointer;
  font-size: 12px;
  transition: all 0.15s;
}
.builder-settings-toggle:hover {
  background: var(--surface-hover);
  color: var(--text);
}

/* Center canvas */
.builder-canvas-wrap {
  flex: 1;
  position: relative;
  overflow: hidden;
  background: var(--bg);
}
.builder-canvas {
  width: 100%;
  height: 100%;
  display: block;
  cursor: grab;
}
.builder-canvas.panning {
  cursor: grabbing;
}
.builder-canvas-empty {
  position: absolute;
  inset: 0;
  display: flex;
  align-items: center;
  justify-content: center;
}
.builder-add-first {
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 12px;
  color: var(--text-muted);
  font-size: 14px;
}
.builder-add-first button {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  padding: 10px 20px;
  font-size: 14px;
  font-weight: 500;
  border: 1px dashed var(--border-light);
  border-radius: 8px;
  cursor: pointer;
  background: var(--surface);
  color: var(--text);
  transition: all 0.15s;
}
.builder-add-first button:hover {
  border-color: var(--accent);
  background: var(--surface-hover);
}

/* Right panel */
.builder-right {
  width: 320px;
  min-width: 320px;
  background: var(--bg-secondary);
  border-left: 1px solid var(--border);
  display: flex;
  flex-direction: column;
  overflow-y: auto;
  flex-shrink: 0;
}
.builder-right-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 12px 16px;
  border-bottom: 1px solid var(--border);
}
.builder-right-title {
  font-size: 13px;
  font-weight: 600;
  color: var(--text);
}
.builder-right-body {
  padding: 16px;
  flex: 1;
  overflow-y: auto;
}
.builder-field {
  margin-bottom: 14px;
}
.builder-field-label {
  display: block;
  font-size: 11px;
  font-weight: 600;
  text-transform: uppercase;
  letter-spacing: 0.5px;
  color: var(--text-muted);
  margin-bottom: 4px;
}
.builder-field-input {
  width: 100%;
  padding: 7px 10px;
  background: var(--bg);
  border: 1px solid var(--border);
  border-radius: 5px;
  color: var(--text);
  font-size: 13px;
  outline: none;
  transition: border-color 0.15s;
}
.builder-field-input:focus {
  border-color: var(--accent);
}
.builder-field-textarea {
  width: 100%;
  padding: 7px 10px;
  background: var(--bg);
  border: 1px solid var(--border);
  border-radius: 5px;
  color: var(--text);
  font-size: 12px;
  font-family: monospace;
  outline: none;
  resize: vertical;
  min-height: 60px;
  transition: border-color 0.15s;
}
.builder-field-textarea:focus {
  border-color: var(--accent);
}
.builder-field-select {
  width: 100%;
  padding: 7px 10px;
  background: var(--bg);
  border: 1px solid var(--border);
  border-radius: 5px;
  color: var(--text);
  font-size: 13px;
  outline: none;
  cursor: pointer;
  transition: border-color 0.15s;
  appearance: none;
}
.builder-field-select:focus {
  border-color: var(--accent);
}
.builder-type-badge {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  padding: 3px 8px;
  border-radius: 4px;
  font-size: 11px;
  font-weight: 600;
}
.builder-dep-chip {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  padding: 2px 8px;
  background: var(--surface);
  border: 1px solid var(--border);
  border-radius: 4px;
  font-size: 11px;
  color: var(--text-muted);
  cursor: pointer;
  transition: all 0.15s;
}
.builder-dep-chip:hover {
  border-color: var(--error);
  color: var(--error);
}
.builder-dep-chip.available {
  border-style: dashed;
  opacity: 0.7;
}
.builder-dep-chip.available:hover {
  border-color: var(--accent);
  color: var(--accent);
  opacity: 1;
}
.builder-delete-btn {
  display: flex;
  align-items: center;
  justify-content: center;
  gap: 6px;
  width: 100%;
  padding: 8px;
  margin-top: 8px;
  border: 1px solid var(--error);
  border-radius: 6px;
  background: rgba(248, 81, 73, 0.1);
  color: var(--error);
  cursor: pointer;
  font-size: 12px;
  font-weight: 500;
  transition: all 0.15s;
}
.builder-delete-btn:hover {
  background: rgba(248, 81, 73, 0.25);
}

/* Bottom bar */
.builder-bottom {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 10px 16px;
  background: var(--bg-secondary);
  border-top: 1px solid var(--border);
  flex-shrink: 0;
}
.builder-step-count {
  font-size: 12px;
  color: var(--text-muted);
}

/* Dot grid pattern on SVG */
.builder-dot-pattern {
  fill: var(--text-dim);
  opacity: 0.3;
}

/* Section divider in right panel */
.builder-section-divider {
  border: none;
  border-top: 1px solid var(--border);
  margin: 14px 0;
}
`;

/* ------------------------------------------------------------------ */
/*  Component                                                          */
/* ------------------------------------------------------------------ */

function BuilderModal({ open, onClose, onSave }: Props): React.JSX.Element {
  const modalRef = useFocusTrap<HTMLDivElement>(open, onClose);

  /* ---- state ---- */
  const [steps, setSteps] = useState<BuilderStep[]>([]);
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [config, setConfig] = useState<OrchestrationConfig>({ ...DEFAULT_CONFIG });

  // Canvas pan/zoom
  const [pan, setPan] = useState({ x: 0, y: 0 });
  const [zoom, setZoom] = useState(1);

  // Interaction refs (avoid state to prevent re-render during drag)
  const isPanning = useRef(false);
  const panStart = useRef({ x: 0, y: 0 });
  const panStartOffset = useRef({ x: 0, y: 0 });

  const isDraggingNode = useRef(false);
  const dragNodeId = useRef<string | null>(null);
  const dragNodeStart = useRef({ x: 0, y: 0 });
  const dragNodeOffset = useRef({ x: 0, y: 0 });

  const isConnecting = useRef(false);
  const connectSourceId = useRef<string | null>(null);
  const [connectLine, setConnectLine] = useState<{ x1: number; y1: number; x2: number; y2: number } | null>(null);

  const svgRef = useRef<SVGSVGElement | null>(null);
  const [canvasPanning, setCanvasPanning] = useState(false);

  // Settings panel toggle from left side
  const [showSettingsPanel, setShowSettingsPanel] = useState(false);

  /* ---- derived ---- */
  const selected = useMemo(
    () => steps.find(s => s.id === selectedId) ?? null,
    [steps, selectedId],
  );

  const stepMap = useMemo(
    () => new Map(steps.map(s => [s.id, s])),
    [steps],
  );

  /* ---- helpers ---- */
  const svgPoint = useCallback(
    (clientX: number, clientY: number): { x: number; y: number } => {
      const svg = svgRef.current;
      if (!svg) return { x: clientX, y: clientY };
      const rect = svg.getBoundingClientRect();
      return {
        x: (clientX - rect.left - pan.x) / zoom,
        y: (clientY - rect.top - pan.y) / zoom,
      };
    },
    [pan, zoom],
  );

  /* ---- step mutations ---- */
  const addStep = useCallback(
    (type: BuilderStep['type']) => {
      const svg = svgRef.current;
      let cx = 500;
      let cy = 300;
      if (svg) {
        const rect = svg.getBoundingClientRect();
        cx = (rect.width / 2 - pan.x) / zoom - NODE_W / 2;
        cy = (rect.height / 2 - pan.y) / zoom - NODE_H / 2;
      }
      // Offset a bit so stacked adds don't overlap perfectly
      cx += (steps.length % 5) * 30;
      cy += (steps.length % 5) * 30;

      const newStep: BuilderStep = {
        id: nextStepId(),
        name: nextStepName(steps),
        type,
        dependsOn: [],
        x: Math.round(cx),
        y: Math.round(cy),
        ...(type === 'Prompt' ? { model: 'claude-opus-4.5', systemPrompt: '', userPrompt: '' } : {}),
        ...(type === 'Http' ? { method: 'GET', url: '', body: '' } : {}),
        ...(type === 'Transform' ? { template: '' } : {}),
      };
      setSteps(prev => [...prev, newStep]);
      setSelectedId(newStep.id);
    },
    [steps, pan, zoom],
  );

  const updateStep = useCallback(
    (id: string, patch: Partial<BuilderStep>) => {
      setSteps(prev => prev.map(s => (s.id === id ? { ...s, ...patch } : s)));
    },
    [],
  );

  const deleteStep = useCallback(
    (id: string) => {
      setSteps(prev =>
        prev
          .filter(s => s.id !== id)
          .map(s => ({
            ...s,
            dependsOn: s.dependsOn.filter(d => d !== id),
          })),
      );
      setSelectedId(prev => (prev === id ? null : prev));
    },
    [],
  );

  const toggleDependency = useCallback(
    (stepId: string, depId: string) => {
      setSteps(prev =>
        prev.map(s => {
          if (s.id !== stepId) return s;
          if (s.dependsOn.includes(depId)) {
            return { ...s, dependsOn: s.dependsOn.filter(d => d !== depId) };
          }
          // Check for cycle before adding
          if (wouldCreateCycle(prev, depId, stepId)) return s;
          return { ...s, dependsOn: [...s.dependsOn, depId] };
        }),
      );
    },
    [],
  );

  /* ---- canvas mouse handlers ---- */
  const handleCanvasMouseDown = useCallback(
    (e: React.MouseEvent<SVGSVGElement>) => {
      // Only start pan on primary button and on the SVG background
      if (e.button !== 0) return;
      const target = e.target as Element;
      if (target.closest('.builder-node') || target.closest('.builder-port')) return;

      // Deselect and start panning
      setSelectedId(null);
      isPanning.current = true;
      panStart.current = { x: e.clientX, y: e.clientY };
      panStartOffset.current = { x: pan.x, y: pan.y };
      setCanvasPanning(true);
      e.preventDefault();
    },
    [pan],
  );

  const handleCanvasMouseMove = useCallback(
    (e: React.MouseEvent<SVGSVGElement>) => {
      if (isPanning.current) {
        const dx = e.clientX - panStart.current.x;
        const dy = e.clientY - panStart.current.y;
        setPan({
          x: panStartOffset.current.x + dx,
          y: panStartOffset.current.y + dy,
        });
        return;
      }

      if (isDraggingNode.current && dragNodeId.current) {
        const pt = svgPoint(e.clientX, e.clientY);
        const nx = pt.x - dragNodeOffset.current.x;
        const ny = pt.y - dragNodeOffset.current.y;
        setSteps(prev =>
          prev.map(s => (s.id === dragNodeId.current ? { ...s, x: Math.round(nx), y: Math.round(ny) } : s)),
        );
        return;
      }

      if (isConnecting.current && connectSourceId.current) {
        const src = stepMap.get(connectSourceId.current);
        if (src) {
          const pt = svgPoint(e.clientX, e.clientY);
          setConnectLine({
            x1: src.x + NODE_W / 2,
            y1: src.y + NODE_H,
            x2: pt.x,
            y2: pt.y,
          });
        }
      }
    },
    [svgPoint, stepMap],
  );

  const handleCanvasMouseUp = useCallback(
    (_e: React.MouseEvent<SVGSVGElement>) => {
      if (isPanning.current) {
        isPanning.current = false;
        setCanvasPanning(false);
      }
      if (isDraggingNode.current) {
        isDraggingNode.current = false;
        dragNodeId.current = null;
      }
      if (isConnecting.current) {
        isConnecting.current = false;
        connectSourceId.current = null;
        setConnectLine(null);
      }
    },
    [],
  );

  const handleWheel = useCallback(
    (e: React.WheelEvent<SVGSVGElement>) => {
      e.preventDefault();
      const delta = -e.deltaY * ZOOM_STEP;
      setZoom(prev => {
        const next = Math.min(MAX_ZOOM, Math.max(MIN_ZOOM, prev + delta));
        // Adjust pan so zoom centers on cursor
        const svg = svgRef.current;
        if (svg) {
          const rect = svg.getBoundingClientRect();
          const cx = e.clientX - rect.left;
          const cy = e.clientY - rect.top;
          const scale = next / prev;
          setPan(p => ({
            x: cx - scale * (cx - p.x),
            y: cy - scale * (cy - p.y),
          }));
        }
        return next;
      });
    },
    [],
  );

  /* ---- node mouse handlers ---- */
  const handleNodeMouseDown = useCallback(
    (e: React.MouseEvent<SVGGElement>, stepId: string) => {
      if (e.button !== 0) return;
      const target = e.target as Element;
      if (target.closest('.builder-port')) return; // handled by port handler
      e.stopPropagation();
      isDraggingNode.current = true;
      dragNodeId.current = stepId;
      const pt = svgPoint(e.clientX, e.clientY);
      const step = stepMap.get(stepId);
      if (step) {
        dragNodeOffset.current = { x: pt.x - step.x, y: pt.y - step.y };
      }
      setSelectedId(stepId);
    },
    [svgPoint, stepMap],
  );

  /* ---- port handlers ---- */
  const handleOutputPortMouseDown = useCallback(
    (e: React.MouseEvent<SVGCircleElement>, stepId: string) => {
      e.stopPropagation();
      e.preventDefault();
      isConnecting.current = true;
      connectSourceId.current = stepId;
    },
    [],
  );

  const handleInputPortMouseUp = useCallback(
    (e: React.MouseEvent<SVGCircleElement>, targetId: string) => {
      e.stopPropagation();
      if (isConnecting.current && connectSourceId.current) {
        const sourceId = connectSourceId.current;
        if (sourceId !== targetId) {
          // Add dependency: target depends on source
          setSteps(prev => {
            const target = prev.find(s => s.id === targetId);
            if (!target) return prev;
            if (target.dependsOn.includes(sourceId)) return prev;
            if (wouldCreateCycle(prev, sourceId, targetId)) return prev;
            return prev.map(s =>
              s.id === targetId ? { ...s, dependsOn: [...s.dependsOn, sourceId] } : s,
            );
          });
        }
        isConnecting.current = false;
        connectSourceId.current = null;
        setConnectLine(null);
      }
    },
    [],
  );

  /* ---- export ---- */
  const exportJson = useCallback(() => {
    const stepsPayload = steps.map(s => {
      const base: Record<string, unknown> = {
        name: s.name,
        type: s.type,
        dependsOn: s.dependsOn
          .map(id => stepMap.get(id)?.name)
          .filter((n): n is string => !!n),
      };
      if (s.type === 'Prompt') {
        base.model = s.model || 'claude-opus-4.5';
        base.systemPrompt = s.systemPrompt || '';
        base.userPrompt = s.userPrompt || '';
      }
      if (s.type === 'Http') {
        base.method = s.method || 'GET';
        base.url = s.url || '';
        if (s.body) base.body = s.body;
      }
      if (s.type === 'Transform') {
        base.template = s.template || '';
      }
      if (s.parameters && s.parameters.length > 0) {
        base.parameters = s.parameters;
      }
      if (s.timeoutSeconds) {
        base.timeoutSeconds = s.timeoutSeconds;
      }
      return base;
    });

    const result: Record<string, unknown> = {
      name: config.name,
      description: config.description,
      version: config.version,
      steps: stepsPayload,
    };

    if (config.triggerType !== 'none' && config.triggerEnabled) {
      const trigger: Record<string, unknown> = { type: config.triggerType };
      if (config.triggerType === 'webhook') {
        if (config.webhookSecret) trigger.secret = config.webhookSecret;
        trigger.maxConcurrent = config.webhookMaxConcurrent;
      }
      if (config.triggerType === 'scheduler') {
        if (config.schedulerCron) trigger.cron = config.schedulerCron;
        trigger.intervalSeconds = config.schedulerIntervalSeconds;
      }
      if (config.triggerType === 'loop') {
        trigger.delaySeconds = config.loopDelaySeconds;
        trigger.maxIterations = config.loopMaxIterations;
      }
      result.trigger = trigger;
    }

    const json = JSON.stringify(result, null, 2);
    onSave(json);
  }, [steps, stepMap, config, onSave]);

  /* ---- reset on open ---- */
  useEffect(() => {
    if (open) {
      setSteps([]);
      setSelectedId(null);
      setConfig({ ...DEFAULT_CONFIG });
      setPan({ x: 0, y: 0 });
      setZoom(1);
      setShowSettingsPanel(false);
      setConnectLine(null);
      stepCounter = 0;
    }
  }, [open]);

  /* ---- edge rendering ---- */
  const edges = useMemo(() => {
    const result: Array<{ key: string; path: string; fromId: string; toId: string }> = [];
    for (const step of steps) {
      for (const depId of step.dependsOn) {
        const dep = stepMap.get(depId);
        if (dep) {
          result.push({
            key: `${depId}->${step.id}`,
            path: edgePath(dep, step),
            fromId: depId,
            toId: step.id,
          });
        }
      }
    }
    return result;
  }, [steps, stepMap]);

  /* ---- render helpers ---- */
  const renderNode = useCallback(
    (step: BuilderStep) => {
      const isSelected = step.id === selectedId;
      const color = TYPE_COLORS[step.type];
      return (
        <g
          key={step.id}
          className="builder-node"
          transform={`translate(${step.x}, ${step.y})`}
          onMouseDown={(e: React.MouseEvent<SVGGElement>) => handleNodeMouseDown(e, step.id)}
          style={{ cursor: 'pointer' }}
        >
          {/* Selection glow */}
          {isSelected && (
            <rect
              x={-3}
              y={-3}
              width={NODE_W + 6}
              height={NODE_H + 6}
              rx={11}
              ry={11}
              fill="none"
              stroke={color}
              strokeWidth={2}
              opacity={0.5}
              filter="url(#glow)"
            />
          )}
          {/* Node body */}
          <rect
            x={0}
            y={0}
            width={NODE_W}
            height={NODE_H}
            rx={8}
            ry={8}
            fill="var(--surface)"
            stroke={isSelected ? color : 'var(--border)'}
            strokeWidth={isSelected ? 2 : 1}
          />
          {/* Color accent bar */}
          <rect x={0} y={0} width={4} height={NODE_H} rx={2} fill={color} />
          {/* Type label */}
          <text
            x={14}
            y={20}
            fontSize={10}
            fontWeight={600}
            fill={color}
            style={{ userSelect: 'none', pointerEvents: 'none' }}
          >
            {step.type.toUpperCase()}
          </text>
          {/* Step name */}
          <text
            x={14}
            y={42}
            fontSize={13}
            fontWeight={500}
            fill="var(--text)"
            style={{ userSelect: 'none', pointerEvents: 'none' }}
          >
            {step.name.length > 22 ? step.name.slice(0, 20) + '\u2026' : step.name}
          </text>
          {/* Input port (top center) */}
          <circle
            className="builder-port builder-port-input"
            cx={NODE_W / 2}
            cy={0}
            r={PORT_R}
            fill="var(--bg)"
            stroke="var(--text-muted)"
            strokeWidth={1.5}
            style={{ cursor: 'crosshair' }}
            onMouseUp={(e: React.MouseEvent<SVGCircleElement>) => handleInputPortMouseUp(e, step.id)}
          />
          {/* Output port (bottom center) */}
          <circle
            className="builder-port builder-port-output"
            cx={NODE_W / 2}
            cy={NODE_H}
            r={PORT_R}
            fill="var(--bg)"
            stroke="var(--text-muted)"
            strokeWidth={1.5}
            style={{ cursor: 'crosshair' }}
            onMouseDown={(e: React.MouseEvent<SVGCircleElement>) => handleOutputPortMouseDown(e, step.id)}
          />
        </g>
      );
    },
    [selectedId, handleNodeMouseDown, handleInputPortMouseUp, handleOutputPortMouseDown],
  );

  /* ---- right panel content ---- */
  const renderPropertiesPanel = () => {
    if (selected) {
      const otherSteps = steps.filter(s => s.id !== selected.id);
      return (
        <>
          <div className="builder-right-header">
            <span className="builder-right-title">Step Properties</span>
            <button
              className="btn-icon"
              onClick={() => setSelectedId(null)}
              aria-label="Close properties"
            >
              <Icons.X />
            </button>
          </div>
          <div className="builder-right-body">
            {/* Name */}
            <div className="builder-field">
              <label className="builder-field-label">Name</label>
              <input
                className="builder-field-input"
                type="text"
                value={selected.name}
                onChange={(e: React.ChangeEvent<HTMLInputElement>) =>
                  updateStep(selected.id, { name: e.target.value })
                }
              />
            </div>

            {/* Type (read-only) */}
            <div className="builder-field">
              <label className="builder-field-label">Type</label>
              <span
                className="builder-type-badge"
                style={{
                  background: `color-mix(in srgb, ${TYPE_COLORS[selected.type]} 15%, transparent)`,
                  color: TYPE_COLORS[selected.type],
                }}
              >
                {selected.type}
              </span>
            </div>

            {/* Depends On */}
            <div className="builder-field">
              <label className="builder-field-label">Depends On</label>
              <div style={{ display: 'flex', flexWrap: 'wrap', gap: 4 }}>
                {otherSteps.map(s => {
                  const isDependent = selected.dependsOn.includes(s.id);
                  return (
                    <button
                      key={s.id}
                      type="button"
                      className={`builder-dep-chip ${isDependent ? '' : 'available'}`}
                      style={
                        isDependent
                          ? { borderColor: TYPE_COLORS[s.type], color: TYPE_COLORS[s.type] }
                          : undefined
                      }
                      onClick={() => toggleDependency(selected.id, s.id)}
                    >
                      {s.name}
                      {isDependent && <span style={{ marginLeft: 2 }}>&times;</span>}
                    </button>
                  );
                })}
                {otherSteps.length === 0 && (
                  <span style={{ fontSize: 11, color: 'var(--text-dim)' }}>No other steps</span>
                )}
              </div>
            </div>

            <hr className="builder-section-divider" />

            {/* Type-specific fields */}
            {selected.type === 'Prompt' && (
              <>
                <div className="builder-field">
                  <label className="builder-field-label">Model</label>
                  <input
                    className="builder-field-input"
                    type="text"
                    value={selected.model ?? 'claude-opus-4.5'}
                    onChange={(e: React.ChangeEvent<HTMLInputElement>) =>
                      updateStep(selected.id, { model: e.target.value })
                    }
                  />
                </div>
                <div className="builder-field">
                  <label className="builder-field-label">System Prompt</label>
                  <textarea
                    className="builder-field-textarea"
                    value={selected.systemPrompt ?? ''}
                    onChange={(e: React.ChangeEvent<HTMLTextAreaElement>) =>
                      updateStep(selected.id, { systemPrompt: e.target.value })
                    }
                    rows={3}
                  />
                </div>
                <div className="builder-field">
                  <label className="builder-field-label">User Prompt</label>
                  <textarea
                    className="builder-field-textarea"
                    value={selected.userPrompt ?? ''}
                    onChange={(e: React.ChangeEvent<HTMLTextAreaElement>) =>
                      updateStep(selected.id, { userPrompt: e.target.value })
                    }
                    rows={3}
                  />
                </div>
              </>
            )}

            {selected.type === 'Http' && (
              <>
                <div className="builder-field">
                  <label className="builder-field-label">Method</label>
                  <select
                    className="builder-field-select"
                    value={selected.method ?? 'GET'}
                    onChange={(e: React.ChangeEvent<HTMLSelectElement>) =>
                      updateStep(selected.id, { method: e.target.value })
                    }
                  >
                    <option value="GET">GET</option>
                    <option value="POST">POST</option>
                    <option value="PUT">PUT</option>
                    <option value="DELETE">DELETE</option>
                  </select>
                </div>
                <div className="builder-field">
                  <label className="builder-field-label">URL</label>
                  <input
                    className="builder-field-input"
                    type="text"
                    placeholder="https://..."
                    value={selected.url ?? ''}
                    onChange={(e: React.ChangeEvent<HTMLInputElement>) =>
                      updateStep(selected.id, { url: e.target.value })
                    }
                  />
                </div>
                <div className="builder-field">
                  <label className="builder-field-label">Body</label>
                  <textarea
                    className="builder-field-textarea"
                    value={selected.body ?? ''}
                    onChange={(e: React.ChangeEvent<HTMLTextAreaElement>) =>
                      updateStep(selected.id, { body: e.target.value })
                    }
                    rows={4}
                    placeholder='{ "key": "value" }'
                  />
                </div>
              </>
            )}

            {selected.type === 'Transform' && (
              <div className="builder-field">
                <label className="builder-field-label">Template</label>
                <textarea
                  className="builder-field-textarea"
                  value={selected.template ?? ''}
                  onChange={(e: React.ChangeEvent<HTMLTextAreaElement>) =>
                    updateStep(selected.id, { template: e.target.value })
                  }
                  rows={6}
                  placeholder="Enter transform template..."
                />
              </div>
            )}

            {/* Timeout */}
            <div className="builder-field">
              <label className="builder-field-label">Timeout (seconds)</label>
              <input
                className="builder-field-input"
                type="number"
                min={0}
                value={selected.timeoutSeconds ?? ''}
                placeholder="Inherit from orchestration"
                onChange={(e: React.ChangeEvent<HTMLInputElement>) =>
                  updateStep(selected.id, {
                    timeoutSeconds: e.target.value ? parseInt(e.target.value, 10) : undefined,
                  })
                }
              />
            </div>

            <hr className="builder-section-divider" />

            {/* Delete */}
            <button
              type="button"
              className="builder-delete-btn"
              onClick={() => deleteStep(selected.id)}
            >
              <Icons.X /> Delete Step
            </button>
          </div>
        </>
      );
    }

    // No step selected: show orchestration settings
    return (
      <>
        <div className="builder-right-header">
          <span className="builder-right-title">Orchestration Settings</span>
        </div>
        <div className="builder-right-body">
          <div className="builder-field">
            <label className="builder-field-label">Version</label>
            <input
              className="builder-field-input"
              type="text"
              value={config.version}
              onChange={(e: React.ChangeEvent<HTMLInputElement>) =>
                setConfig(prev => ({ ...prev, version: e.target.value }))
              }
            />
          </div>
          <div className="builder-field">
            <label className="builder-field-label">Timeout (seconds)</label>
            <input
              className="builder-field-input"
              type="number"
              min={0}
              value={config.timeoutSeconds}
              onChange={(e: React.ChangeEvent<HTMLInputElement>) =>
                setConfig(prev => ({
                  ...prev,
                  timeoutSeconds: parseInt(e.target.value, 10) || 0,
                }))
              }
            />
          </div>

          <hr className="builder-section-divider" />

          {/* Trigger config */}
          <div className="builder-field">
            <label className="builder-field-label">Trigger Type</label>
            <select
              className="builder-field-select"
              value={config.triggerType}
              onChange={(e: React.ChangeEvent<HTMLSelectElement>) =>
                setConfig(prev => ({
                  ...prev,
                  triggerType: e.target.value as OrchestrationConfig['triggerType'],
                  triggerEnabled: e.target.value !== 'none',
                }))
              }
            >
              <option value="none">None</option>
              <option value="webhook">Webhook</option>
              <option value="scheduler">Scheduler</option>
              <option value="loop">Loop</option>
            </select>
          </div>

          {config.triggerType === 'webhook' && (
            <>
              <div className="builder-field">
                <label className="builder-field-label">Webhook Secret</label>
                <input
                  className="builder-field-input"
                  type="text"
                  placeholder="Optional secret..."
                  value={config.webhookSecret}
                  onChange={(e: React.ChangeEvent<HTMLInputElement>) =>
                    setConfig(prev => ({ ...prev, webhookSecret: e.target.value }))
                  }
                />
              </div>
              <div className="builder-field">
                <label className="builder-field-label">Max Concurrent</label>
                <input
                  className="builder-field-input"
                  type="number"
                  min={1}
                  value={config.webhookMaxConcurrent}
                  onChange={(e: React.ChangeEvent<HTMLInputElement>) =>
                    setConfig(prev => ({
                      ...prev,
                      webhookMaxConcurrent: parseInt(e.target.value, 10) || 1,
                    }))
                  }
                />
              </div>
            </>
          )}

          {config.triggerType === 'scheduler' && (
            <>
              <div className="builder-field">
                <label className="builder-field-label">Cron Expression</label>
                <input
                  className="builder-field-input"
                  type="text"
                  placeholder="*/5 * * * *"
                  value={config.schedulerCron}
                  onChange={(e: React.ChangeEvent<HTMLInputElement>) =>
                    setConfig(prev => ({ ...prev, schedulerCron: e.target.value }))
                  }
                />
              </div>
              <div className="builder-field">
                <label className="builder-field-label">Interval (seconds)</label>
                <input
                  className="builder-field-input"
                  type="number"
                  min={1}
                  value={config.schedulerIntervalSeconds}
                  onChange={(e: React.ChangeEvent<HTMLInputElement>) =>
                    setConfig(prev => ({
                      ...prev,
                      schedulerIntervalSeconds: parseInt(e.target.value, 10) || 60,
                    }))
                  }
                />
              </div>
            </>
          )}

          {config.triggerType === 'loop' && (
            <>
              <div className="builder-field">
                <label className="builder-field-label">Delay (seconds)</label>
                <input
                  className="builder-field-input"
                  type="number"
                  min={0}
                  value={config.loopDelaySeconds}
                  onChange={(e: React.ChangeEvent<HTMLInputElement>) =>
                    setConfig(prev => ({
                      ...prev,
                      loopDelaySeconds: parseInt(e.target.value, 10) || 0,
                    }))
                  }
                />
              </div>
              <div className="builder-field">
                <label className="builder-field-label">Max Iterations</label>
                <input
                  className="builder-field-input"
                  type="number"
                  min={1}
                  value={config.loopMaxIterations}
                  onChange={(e: React.ChangeEvent<HTMLInputElement>) =>
                    setConfig(prev => ({
                      ...prev,
                      loopMaxIterations: parseInt(e.target.value, 10) || 100,
                    }))
                  }
                />
              </div>
            </>
          )}

          {config.triggerType !== 'none' && (
            <div className="builder-field">
              <label style={{ display: 'flex', alignItems: 'center', gap: 8, cursor: 'pointer' }}>
                <input
                  type="checkbox"
                  checked={config.triggerEnabled}
                  onChange={(e: React.ChangeEvent<HTMLInputElement>) =>
                    setConfig(prev => ({ ...prev, triggerEnabled: e.target.checked }))
                  }
                />
                <span className="builder-field-label" style={{ margin: 0 }}>
                  Trigger Enabled
                </span>
              </label>
            </div>
          )}
        </div>
      </>
    );
  };

  /* ---- render ---- */
  return (
    <>
      <style>{BUILDER_CSS}</style>
      <div
        ref={modalRef}
        className={`builder-overlay ${open ? 'visible' : ''}`}
        role="dialog"
        aria-modal="true"
        aria-label="Visual Orchestration Builder"
      >
        {/* ===== HEADER ===== */}
        <div className="builder-header">
          <Icons.Workflow />
          <input
            className="builder-header-input"
            type="text"
            value={config.name}
            onChange={(e: React.ChangeEvent<HTMLInputElement>) =>
              setConfig(prev => ({ ...prev, name: e.target.value }))
            }
            aria-label="Orchestration name"
            style={{ width: 200 }}
          />
          <input
            className="builder-header-input desc"
            type="text"
            placeholder="Description..."
            value={config.description}
            onChange={(e: React.ChangeEvent<HTMLInputElement>) =>
              setConfig(prev => ({ ...prev, description: e.target.value }))
            }
            aria-label="Orchestration description"
          />
          <div style={{ marginLeft: 'auto', display: 'flex', gap: 8, alignItems: 'center' }}>
            <button
              className="btn btn-sm"
              onClick={() => {
                setSelectedId(null);
                setShowSettingsPanel(!showSettingsPanel);
              }}
              title="Settings"
              aria-label="Settings"
            >
              <Icons.Tool />
            </button>
            <button className="btn btn-sm btn-primary" onClick={exportJson} title="Export JSON">
              <Icons.Download /> Export
            </button>
            <button
              className="modal-close"
              onClick={onClose}
              aria-label="Close"
              data-autofocus
            >
              <Icons.X />
            </button>
          </div>
        </div>

        {/* ===== BODY ===== */}
        <div className="builder-body">
          {/* --- Left palette --- */}
          <div className="builder-left">
            <div className="builder-left-title">Step Types</div>
            <button
              type="button"
              className="builder-palette-btn"
              onClick={() => addStep('Prompt')}
            >
              <span className="builder-palette-dot" style={{ background: TYPE_COLORS.Prompt }} />
              Prompt
            </button>
            <button
              type="button"
              className="builder-palette-btn"
              onClick={() => addStep('Http')}
            >
              <span className="builder-palette-dot" style={{ background: TYPE_COLORS.Http }} />
              HTTP
            </button>
            <button
              type="button"
              className="builder-palette-btn"
              onClick={() => addStep('Transform')}
            >
              <span
                className="builder-palette-dot"
                style={{ background: TYPE_COLORS.Transform }}
              />
              Transform
            </button>

            <button
              type="button"
              className="builder-settings-toggle"
              onClick={() => {
                setSelectedId(null);
                setShowSettingsPanel(prev => !prev);
              }}
            >
              <Icons.Tool />
              Settings
            </button>
          </div>

          {/* --- Center canvas --- */}
          <div className="builder-canvas-wrap">
            {steps.length === 0 ? (
              <div className="builder-canvas-empty">
                <div className="builder-add-first">
                  <span>No steps yet</span>
                  <button type="button" onClick={() => addStep('Prompt')}>
                    <Icons.Plus /> Add First Step
                  </button>
                </div>
              </div>
            ) : null}
            <svg
              ref={svgRef}
              className={`builder-canvas${canvasPanning ? ' panning' : ''}`}
              onMouseDown={handleCanvasMouseDown}
              onMouseMove={handleCanvasMouseMove}
              onMouseUp={handleCanvasMouseUp}
              onMouseLeave={handleCanvasMouseUp}
              onWheel={handleWheel}
            >
              <defs>
                {/* Arrowhead marker */}
                <marker
                  id="builder-arrowhead"
                  markerWidth="10"
                  markerHeight="7"
                  refX="10"
                  refY="3.5"
                  orient="auto"
                >
                  <polygon
                    points="0 0, 10 3.5, 0 7"
                    fill="var(--text-muted)"
                  />
                </marker>
                {/* Glow filter for selected node */}
                <filter id="glow" x="-50%" y="-50%" width="200%" height="200%">
                  <feGaussianBlur stdDeviation="4" result="blur" />
                  <feMerge>
                    <feMergeNode in="blur" />
                    <feMergeNode in="SourceGraphic" />
                  </feMerge>
                </filter>
                {/* Dot pattern */}
                <pattern
                  id="builder-dots"
                  x="0"
                  y="0"
                  width="20"
                  height="20"
                  patternUnits="userSpaceOnUse"
                >
                  <circle cx="10" cy="10" r="1" className="builder-dot-pattern" />
                </pattern>
              </defs>

              {/* Background dot grid */}
              <rect width="100%" height="100%" fill="url(#builder-dots)" />

              {/* Pan/zoom group */}
              <g transform={`translate(${pan.x}, ${pan.y}) scale(${zoom})`}>
                {/* Edges */}
                {edges.map(edge => (
                  <path
                    key={edge.key}
                    d={edge.path}
                    fill="none"
                    stroke="var(--text-muted)"
                    strokeWidth={2}
                    markerEnd="url(#builder-arrowhead)"
                  />
                ))}

                {/* Connection line while dragging */}
                {connectLine && (
                  <path
                    d={`M ${connectLine.x1} ${connectLine.y1} C ${connectLine.x1} ${connectLine.y1 + 40}, ${connectLine.x2} ${connectLine.y2 - 40}, ${connectLine.x2} ${connectLine.y2}`}
                    fill="none"
                    stroke="var(--accent)"
                    strokeWidth={2}
                    strokeDasharray="6 3"
                    opacity={0.7}
                  />
                )}

                {/* Nodes */}
                {steps.map(renderNode)}
              </g>
            </svg>
          </div>

          {/* --- Right panel --- */}
          <div className="builder-right">{renderPropertiesPanel()}</div>
        </div>

        {/* ===== BOTTOM BAR ===== */}
        <div className="builder-bottom">
          <span className="builder-step-count">
            {steps.length} step{steps.length !== 1 ? 's' : ''}
            {' \u00b7 '}
            {edges.length} connection{edges.length !== 1 ? 's' : ''}
          </span>
          <div style={{ display: 'flex', gap: 8, alignItems: 'center' }}>
            <span style={{ fontSize: 11, color: 'var(--text-dim)' }}>
              Zoom: {Math.round(zoom * 100)}%
            </span>
            <button className="btn btn-sm btn-primary" onClick={exportJson}>
              <Icons.Download /> Export JSON
            </button>
          </div>
        </div>
      </div>
    </>
  );
}

export default BuilderModal;
