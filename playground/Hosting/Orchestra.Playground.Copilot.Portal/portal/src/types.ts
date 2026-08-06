// Orchestration data from /api/orchestrations
export interface Orchestration {
  id: string;
  name: string;
  description?: string;
  version?: string;
  path?: string;

  enabled?: boolean;
  steps?: Step[];
  parameters?: string[];
  inputs?: Record<string, InputDefinition>;
  variables?: Record<string, string>;
  referencedEnvVars?: string[];
  trigger?: TriggerConfig;
  mcps?: McpConfig[];
  hooks?: HookDefinition[];
  models?: string[];
  registeredAt?: string;
  contentHash?: string;
  tags?: string[];
}

export interface HookDefinition {
  name: string;
  eventType: string;
  source?: string;
  failurePolicy?: string;
  when?: {
    names?: string[] | null;
    status?: string;
    match?: string;
  } | null;
  payload?: {
    detail?: string;
    steps?: string | string[] | null;
    includeRefs?: boolean;
  } | null;
  action?: {
    type?: string;
    shell?: string | null;
    scriptFile?: string | null;
    workingDirectory?: string | null;
    arguments?: string[] | null;
    includeStdErr?: boolean | null;
    hasInlineScript?: boolean | null;
  } | null;
}

export interface HookExecution {
  hookName: string;
  eventType: string;
  source?: string;
  status: string;
  startedAt?: string;
  completedAt?: string;
  durationSeconds?: number;
  stepName?: string | null;
  errorMessage?: string | null;
  content?: string | null;
  failurePolicy?: string;
  actionType?: string;
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
  // Skill directories
  skillDirectories?: string[];
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

export interface InputDefinition {
  type: string;
  description?: string;
  required?: boolean;
  default?: string;
  enum?: string[];
  multiline?: boolean;
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
  totalUsage?: UsageData;
  allStepRecords?: Record<string, StepResultData[]>;
  /** When set, this run was started as a retry of the referenced source run. */
  retriedFromRunId?: string | null;
  /** Retry mode descriptor: "failed", "all", or "from-step:<stepName>". */
  retryMode?: string | null;
}

export interface StepResultData {
  content?: string;
  status?: string;
  errorMessage?: string;
  actualModel?: string;
  selectedModel?: string;
  requestedModelInfo?: ModelInfo;
  selectedModelInfo?: ModelInfo;
  actualModelInfo?: ModelInfo;
  /** Agent provider this step was configured to run on. */
  configuredProvider?: string;
  /** Agent provider that actually ran this step. */
  actualProvider?: string;
  usage?: UsageData;
  trace?: TraceData;
  errorCategory?: string;
  retryHistory?: RetryAttemptRecord[];
}

export interface ModelInfo {
  id: string;
  name?: string;
  defaultReasoningEffort?: string;
  billingMultiplier?: number;
  reasoningEfforts?: string[];
  policyState?: string;
  policyTerms?: string;
  supportsReasoningEffort?: boolean;
  supportsVision?: boolean;
  maxContextWindowTokens?: number;
  maxPromptTokens?: number;
  visionSupportedMediaTypes?: string[];
  maxPromptImages?: number;
  maxPromptImageSize?: number;
}

export interface UsageData {
  inputTokens?: number;
  outputTokens?: number;
  totalTokens?: number;
  cacheReadTokens?: number;
  cacheWriteTokens?: number;
  cost?: number;
  duration?: number;
}

export interface RetryAttemptRecord {
  attempt: number;
  error: string;
  attemptedAt: string;
  delaySeconds: number;
  errorCategory?: string;
}

export interface ConversationMessage {
  role: string;
  content?: string;
  toolCallId?: string;
  toolName?: string;
  timestamp: string;
}

export interface AuditLogEntry {
  sequence: number;
  timestamp: string;
  eventType: string; // "SessionStart" | "PromptSubmitted" | "PreToolUse" | "PostToolUse" | "Error" | "SessionEnd" | "CompactionStart" | "CompactionComplete"
  toolName?: string;
  toolArguments?: string;
  permissionDecision?: string;
  toolResult?: string;
  toolSuccess?: boolean;
  prompt?: string;
  error?: string;
  errorContext?: string;
  errorHandling?: string;
  additionalContext?: string;
  sessionSource?: string;
  sessionEndReason?: string;
}

export interface TraceData {
  parameters?: Record<string, string>;
  dependencyOutputs?: Record<string, string>;
  rawDependencyOutputs?: Record<string, string>;
  accessibleStepData?: Record<string, StepTraceData>;
  command?: string;
  commandArguments?: string[];
  shell?: string;
  scriptSource?: string;
  workingDirectory?: string;
  environment?: Record<string, string>;
  stdin?: string;
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
  conversationHistory?: ConversationMessage[];
  auditLog?: AuditLogEntry[];
}

export interface StepTraceData {
  status?: string;
  output?: string;
  rawOutput?: string;
  files?: string[];
}

export interface ToolCallData {
  name: string;
  arguments?: string;
  result?: string;
  durationMs?: number;
  startedAt?: string;
  completedAt?: string;
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
  orchestrationDescription?: string;
  stepCount?: number;
  nextFireTime?: string;
  lastFireTime?: string;
  lastExecutionId?: string;
  runCount?: number;
  status?: string;
  triggerType?: string;
  triggeredBy?: string;
  source?: string;
  webhookUrl?: string;
}

// Server status from /api/status
export interface ServerStatus {
  outlook: OutlookStatus | null;
  orchestrationCount: number;
  activeTriggers: number;
  runningExecutions: number;
  // Singular agentRuntime is the default provider's status (back-compat); agentRuntimes
  // lists every registered provider (copilot, opencode, …) for multi-provider hosts.
  agentRuntime?: AgentRuntimeStatus | null;
  agentRuntimes?: AgentRuntimeStatus[];
  defaultProvider?: string;
  providers?: string[];
}

export interface AgentRuntimeStatus {
  provider: string;
  activePools: number;
  cliInstances: number;
  activeSessions: number;
}

export interface OutlookStatus {
  status: string;
  errorMessage?: string;
}

// Execution modal state
/**
 * Per-step wall-clock timing used to render "running for Xs" (live) and the final
 * step duration (completed) in the execution modal.
 */
export interface StepTiming {
  /** ISO-8601 time the step started, or null/absent if not started yet. */
  startedAt?: string | null;
  /** ISO-8601 time the step reached a terminal state, or null/absent while running. */
  completedAt?: string | null;
  /**
   * Authoritative duration in seconds when the server provides it (historical runs).
   * When absent, the duration is derived from completedAt − startedAt.
   */
  durationSeconds?: number | null;
}

export interface ExecutionModalState {
  open: boolean;
  orchestration: Orchestration | null;
  executionId: string | null;
  stepStatuses: Record<string, string>;
  stepEvents: Record<string, StepEvent[]>;
  stepResults: Record<string, string>;
  stepTraces: Record<string, TraceData>;
  stepAuditLogs: Record<string, AuditLogEntry[]>;
  /**
   * Per-step actor-keyed streaming buffers. Lets the modal render sub-agent
   * activity (deltas, reasoning, tool calls) as inline indented cards instead
   * of merging everything into a single flat <c>streamingContent</c> string.
   */
  stepActorStreams: Record<string, StepActorStreams>;
  /**
   * Per-step wall-clock timing. Populated for historical runs from the run
   * detail endpoint (which carries authoritative startedAt/completedAt/duration).
   * For live runs this is omitted — the modal derives timing from
   * <c>stepActorStreams[step].main</c>, whose timestamps are server-stamped and
   * replay-safe.
   */
  stepTimings?: Record<string, StepTiming>;
  streamingContent: string;
  finalResult: string;
  status: string;
  errorMessage: string | null;
  completedByStep: string | null;
  runContext: RunContext | null;
  hookExecutions: HookExecution[];
  /** Full paths of files saved during this orchestration run. */
  savedFiles: string[];
  /** Full saved-file paths grouped by step name. */
  stepSavedFiles: Record<string, string[]>;
  /** When this run is a retry of an earlier run, the source RunId. */
  retriedFromRunId?: string | null;
  /** Retry mode descriptor when this run is a retry. */
  retryMode?: string | null;
  /** When viewing a historical (terminal) run, holds the run's name+id so retry buttons can build URLs. */
  historicalRun?: { name: string; runId: string } | null;
  /**
   * For Orchestration steps in this run: child run lineage (executionId / orchestrationName / status)
   * keyed by parent step name. Sourced from the /api/history projection's per-step
   * ChildExecutionId/ChildOrchestrationName/ChildStatus fields and used by the modal to
   * render a clickable "view child run" badge.
   */
  stepChildRuns?: Record<string, { executionId: string; orchestrationName: string; status?: string | null }>;
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
  /**
   * Identifies which actor (main agent or specific sub-agent invocation) produced
   * this event. Absent when the event came from the main agent (back-compat with
   * older Host versions that didn't stamp actor info).
   */
  actor?: ActorContext;
  [key: string]: unknown;
}

/**
 * Identifies the producer of a streamed agent event. Stamped on every relevant
 * SSE payload by the Host's <c>SseReporter</c>, mirroring the backend
 * <c>ActorContext</c>. Absent on the wire means "main agent".
 */
export interface ActorContext {
  agentName: string;
  displayName?: string;
  /**
   * The <c>ToolCallId</c> of the <c>SubagentStarted</c> event that opened the
   * current actor's scope. Stable per sub-agent invocation; the Portal uses this
   * as the bucket key so two consecutive invocations of the same sub-agent name
   * render as two separate cards.
   */
  toolCallId: string;
  /** Nesting depth (1 = first-level sub-agent, 2+ = nested). */
  depth: number;
}

/**
 * Streaming buffer for a single actor inside a step. The Portal accumulates
 * deltas separately per actor so sub-agent activity stays visually distinct
 * from main-agent output during a running orchestration.
 */
export interface ActorStream {
  /** Stable bucket key. For main: the literal "main". For sub-agents: the toolCallId. */
  key: string;
  /** Null when this stream represents the main agent. */
  actor: ActorContext | null;
  /** Live-streamed assistant content (concatenated content-delta chunks). */
  content: string;
  /** Live-streamed reasoning text (concatenated reasoning-delta chunks). */
  reasoning: string;
  /** Lifecycle events that belong to this actor (tool calls, sub-agent boundaries, etc.). */
  events: StepEvent[];
  /** Wall-clock time the first event arrived for this actor. */
  startedAt: string;
  /**
   * Wall-clock time the actor finished, set on subagent-completed/subagent-failed
   * for sub-agent streams; always undefined for the main stream while running.
   */
  completedAt?: string;
  /** Final status of the actor (set when it completes). */
  status?: 'running' | 'completed' | 'failed' | 'cancelled';
  /** Error message when status === 'failed'. */
  errorMessage?: string;
}

/**
 * Per-step grouping of actor streams. <c>main</c> is always present; sub-agent
 * streams are appended in start order so the UI can render them inline at the
 * point they were spawned.
 */
export interface StepActorStreams {
  main: ActorStream;
  subagents: ActorStream[];
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

// ── Run annotations ──

/**
 * User-curated metadata attached to a run: favorite, title, tags, note.
 *
 * Run records themselves are immutable, so annotations are stored separately
 * (keyed by run id) and merged into history rows at read time. Their purpose is
 * findability — machine-named runs carry no meaning, and a title makes them
 * searchable by the words a human would actually type. Favorited runs are also
 * exempt from retention deletion.
 *
 * Mirrors the payload of
 * <c>GET/PUT/PATCH /api/history/{name}/{runId}/annotation</c>.
 */
export interface RunAnnotation {
  runId: string;
  orchestrationName?: string | null;
  favorite: boolean;
  title?: string | null;
  tags: string[];
  note?: string | null;
  annotatedAt?: string | null;
  /** True when the annotation's run no longer exists. */
  orphaned?: boolean;
}

/** Fields a run row carries from its annotation, if any. */
export interface RunAnnotationFields {
  favorite?: boolean;
  title?: string | null;
  tags?: string[];
  note?: string | null;
}

/** Response of <c>GET /api/history/annotations</c>. */
export interface RunAnnotationsResponse {
  count: number;
  orphanCount: number;
  annotations: RunAnnotation[];
  tags: TagCount[];
}

// ── Human-in-the-loop (HITL) ──

/**
 * Identifies which HITL pause path produced a pending input record. Mirrors the
 * backend <c>PendingInputKind</c> enum. Strings instead of an enum so the wire
 * format is human-readable and the UI can render new kinds without a code change.
 */
export type HumanInputKind = 'Approval' | 'EngineTool' | string;

/**
 * A run that is currently paused waiting for human input. Surfaced from
 * <c>GET /api/runs/pending</c> and pushed live via the dashboard SSE
 * <c>awaiting-input</c> / <c>input-received</c> / <c>input-timeout</c> events.
 */
export interface PendingInputRecord {
  orchestrationName: string;
  runId: string;
  stepName: string;
  kind: HumanInputKind;
  prompt: string;
  /** Constrained set of allowed responses; absent or empty means free-form reply. */
  choices?: string[];
  /** ISO-8601 timestamp when the wait began. */
  createdAt: string;
  /** ISO-8601 timestamp when the wait will time out (absent = wait indefinitely). */
  expiresAt?: string;
}

/**
 * Body posted to
 * <c>POST /api/orchestrations/{name}/runs/{runId}/respond?step={step}</c>.
 * At least one of <c>choice</c> or <c>reply</c> must be set.
 */
export interface HumanInputResponse {
  choice?: string | null;
  reply?: string | null;
  respondedBy?: string | null;
}

/**
 * Authoritative per-step state served by the server's <c>execution-snapshot</c> SSE
 * frame (and the REST <c>GET /api/execution/{id}/state</c> endpoint). Mirrors the
 * <c>StepStateSnapshot</c> C# record. Survives circular-buffer eviction on the server
 * so the Portal can render the DAG and per-step details correctly even when the
 * earliest <c>step-started</c> / <c>step-completed</c> events have been overwritten.
 */
export interface SnapshotStepState {
  stepName: string;
  /**
   * Lower-case status string matching the values the UI already maps:
   * <c>pending</c> | <c>running</c> | <c>completed</c> | <c>failed</c> |
   * <c>cancelled</c> | <c>skipped</c> | <c>noaction</c> | <c>completed_early</c>.
   */
  status: string;
  startedAt?: string | null;
  completedAt?: string | null;
  error?: string | null;
  /** Full step output (server caps the size; long content can still arrive via step-output). */
  output?: string | null;
  /** Short content preview emitted by <c>step-completed</c>. */
  contentPreview?: string | null;
  /** Raw <c>step-trace</c> JSON payload kept as an opaque object for the trace panel. */
  trace?: unknown;
  savedFiles?: string[];
  /** Raw <c>audit-log</c> JSON payloads in arrival order. */
  auditEntries?: unknown[];
  requestedModel?: string | null;
  selectedModel?: string | null;
  actualModel?: string | null;
  /** Agent provider this step was configured to run on. */
  configuredProvider?: string | null;
  /** Agent provider that actually ran this step. */
  actualProvider?: string | null;
  activeSubagents?: number;
  retryCount?: number;
}

/**
 * Authoritative orchestration-level snapshot. Sent as the first SSE frame on every
 * <c>/api/orchestrations/{id}/run</c>, <c>/api/execution/{id}/attach</c>, and
 * <c>/api/orchestrations/{name}/runs/{runId}/attach</c> response, and also returned
 * by the REST <c>GET /api/execution/{id}/state</c> endpoint.
 */
export interface ExecutionStateSnapshot {
  executionId?: string | null;
  orchestrationId?: string | null;
  orchestrationName?: string | null;
  startedAt?: string | null;
  status?: string | null;
  triggeredBy?: string | null;
  parameters?: Record<string, string> | null;
  /** Raw <c>run-context</c> JSON payload if the engine has emitted one yet. */
  runContext?: unknown;
  steps: Record<string, SnapshotStepState>;
  /**
   * Highest sequence number written to the reporter at the time of the snapshot.
   * Clients can use this as the resume cursor for <c>Last-Event-Id</c> on reconnect.
   */
  lastEventSequence: number;
  isCompleted: boolean;
}
