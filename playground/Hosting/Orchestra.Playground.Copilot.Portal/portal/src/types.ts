// Orchestration data from /api/orchestrations
export interface Orchestration {
  id: string;
  name: string;
  description?: string;
  version?: string;
  path?: string;
  mcpPath?: string;
  enabled?: boolean;
  steps?: Step[];
  parameters?: string[];
  trigger?: TriggerConfig;
  mcps?: McpConfig[];
  registeredAt?: string;
  contentHash?: string;
  tags?: string[];
}

/**
 * A step MCP reference as returned by the API.
 * The list endpoint returns `{ name, type }` while the detail endpoint
 * may include additional fields like `command`, `endpoint`, etc.
 * We accept both strings (for forward-compat) and objects.
 */
export type StepMcpRef = string | { name: string; type?: string; [key: string]: unknown };

export interface SubagentInfo {
  name: string;
  displayName?: string;
  description?: string;
  tools?: string[];
  mcps?: string[];
  infer?: boolean;
}

export interface Step {
  name: string;
  type?: string;
  enabled?: boolean;
  model?: string;
  systemPrompt?: string;
  userPrompt?: string;
  prompt?: string;
  dependsOn?: string[];
  condition?: string;
  output?: string;
  tools?: string[];
  mcps?: StepMcpRef[];
  temperature?: number;
  maxTokens?: number;
  reasoningLevel?: string;
  loopConfig?: LoopConfig;
  parameters?: string[];
  // Http step fields
  url?: string;
  method?: string;
  headers?: Record<string, string>;
  body?: string;
  contentType?: string;
  // Command step fields
  command?: string;
  arguments?: string[];
  workingDirectory?: string;
  // Transform step fields
  transform?: string;
  template?: string;
  // Subagents
  subagents?: SubagentInfo[];
  handler?: HandlerConfig;
}

export interface LoopConfig {
  collection?: string;
  variable?: string;
  maxIterations?: number;
}

export interface HandlerConfig {
  type: string;
  [key: string]: unknown;
}

export interface TriggerConfig {
  type: string;
  schedule?: string;
  webhookPath?: string;
  emailFilter?: string;
  [key: string]: unknown;
}

export interface McpConfig {
  name: string;
  type?: string;
  command?: string;
  args?: string[];
  env?: Record<string, string>;
  url?: string;
  tools?: McpTool[];
}

export interface McpTool {
  name: string;
  description?: string;
  inputSchema?: Record<string, unknown>;
}

// Execution/History data
export interface HistoryRun {
  name: string;
  runId: string;
  status: string;
  startedAt?: string;
  completedAt?: string;
  triggeredBy?: string;
  duration?: number;
  stepResults?: Record<string, StepResultData>;
  result?: Record<string, StepResultData>;
  parameters?: Record<string, unknown>;
}

export interface StepResultData {
  content?: string;
  status?: string;
  errorMessage?: string;
  actualModel?: string;
  usage?: UsageData;
  trace?: TraceData;
}

export interface UsageData {
  inputTokens?: number;
  outputTokens?: number;
  totalTokens?: number;
}

export interface TraceData {
  systemPrompt?: string;
  userPromptRaw?: string;
  userPromptProcessed?: string;
  reasoning?: string;
  toolCalls?: ToolCallData[];
  responseSegments?: ResponseSegment[];
  finalResponse?: string;
  outputHandlerResult?: string;
  mcpServers?: string[];
  warnings?: string[];
}

export interface ToolCallData {
  name: string;
  arguments?: string;
  result?: string;
}

export interface ResponseSegment {
  type: string;
  content: string;
}

// Active data from /api/active
export interface ActiveData {
  running: ActiveExecution[];
  pending: PendingExecution[];
}

export interface ActiveExecution {
  executionId: string;
  orchestrationId: string;
  orchestrationName: string;
  status: string;
  startedAt?: string;
  triggeredBy?: string;
  parameters?: Record<string, unknown>;
  webhookUrl?: string;
  stepCount?: number;
  totalSteps?: number;
  completedSteps?: number;
  currentStep?: string;
}

export interface PendingExecution {
  orchestrationId: string;
  orchestrationName: string;
  scheduledAt?: string;
  triggerType?: string;
  nextRunAt?: string;
}

// Server status from /api/status
export interface ServerStatus {
  outlook: OutlookStatus | null;
  orchestrationCount: number;
  activeTriggers: number;
  runningExecutions: number;
}

export interface OutlookStatus {
  status: string;
  errorMessage?: string;
}

// Execution modal state
export interface ExecutionModalState {
  open: boolean;
  orchestration: Orchestration | null;
  executionId: string | null;
  stepStatuses: Record<string, string>;
  stepEvents: Record<string, StepEvent[]>;
  stepResults: Record<string, string>;
  stepTraces: Record<string, TraceData>;
  streamingContent: string;
  finalResult: string;
  status: string;
  errorMessage: string | null;
  completedByStep: string | null;
  runContext: RunContext | null;
}

export interface RunContext {
  runId: string;
  orchestrationName: string;
  orchestrationVersion: string;
  startedAt: string;
  triggeredBy: string;
  triggerId?: string | null;
  parameters?: Record<string, string> | null;
  variables?: Record<string, string> | null;
  resolvedVariables?: Record<string, string> | null;
  accessedEnvironmentVariables?: Record<string, string | null> | null;
  dataDirectory?: string | null;
}

export interface StepEvent {
  type: string;
  stepName?: string;
  content?: string;
  toolName?: string;
  arguments?: string;
  result?: string;
  error?: string;
  timestamp?: string;
  [key: string]: unknown;
}

// File browser
export interface BrowseEntry {
  name: string;
  path: string;
  isDirectory: boolean;
  isParent: boolean;
  size?: number;
}

// ── Profiles & Tags ──

export interface ScheduleWindow {
  days: string[];
  startTime: string;
  endTime: string;
}

export interface ProfileSchedule {
  timezone?: string;
  windows: ScheduleWindow[];
}

export interface ProfileFilter {
  tags: string[];
  orchestrationIds: string[];
  excludeOrchestrationIds: string[];
}

export interface Profile {
  id: string;
  name: string;
  description?: string;
  isActive: boolean;
  activationTrigger?: string | null;
  activatedAt?: string;
  deactivatedAt?: string;
  filter: ProfileFilter;
  schedule?: ProfileSchedule | null;
  nextScheduledTransition?: string | null;
  nextTransitionType?: string | null;
  matchedOrchestrationCount?: number | null;
  createdAt: string;
  updatedAt: string;
}

export interface ProfileHistoryEntry {
  profileId: string;
  profileName: string;
  action: string;
  reason?: string;
  timestamp: string;
}

export interface TagCount {
  tag: string;
  count: number;
}
