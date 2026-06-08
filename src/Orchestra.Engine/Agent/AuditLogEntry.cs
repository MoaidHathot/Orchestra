namespace Orchestra.Engine;

/// <summary>
/// A single structured audit log entry captured by session hooks.
/// Records tool calls, prompt submissions, session lifecycle, and errors
/// for compliance, debugging, and observability.
/// </summary>
public class AuditLogEntry
{
	/// <summary>
	/// Monotonically increasing sequence number within the step execution.
	/// </summary>
	public required int Sequence { get; set; }

	/// <summary>
	/// When this audit event occurred.
	/// </summary>
	public required DateTimeOffset Timestamp { get; init; }

	/// <summary>
	/// The type of audit event.
	/// </summary>
	public required AuditEventType EventType { get; init; }

	/// <summary>
	/// The tool name involved (for PreToolUse, PostToolUse events).
	/// </summary>
	public string? ToolName { get; init; }

	/// <summary>
	/// Serialized tool arguments (for PreToolUse events).
	/// </summary>
	public string? ToolArguments { get; init; }

	/// <summary>
	/// The permission decision made for a tool call (allow, deny, ask). Historically
	/// reserved for hook-side pre-tool decisions; now also populated for SDK 1.0.0
	/// <see cref="AuditEventType.PermissionCompleted"/> entries with the result kind
	/// from the per-call permission gate (e.g. <c>"approved"</c>, <c>"deniedByRules"</c>,
	/// <c>"approvedForSession"</c>). See <see cref="PermissionDecisionReason"/> for any
	/// extra context (denial message, location key, cancellation reason).
	/// </summary>
	public string? PermissionDecision { get; init; }

	/// <summary>
	/// The tool result content (for PostToolUse events).
	/// </summary>
	public string? ToolResult { get; init; }

	/// <summary>
	/// Whether the tool call succeeded (for PostToolUse events).
	/// </summary>
	public bool? ToolSuccess { get; init; }

	/// <summary>
	/// The user prompt text (for PromptSubmitted events).
	/// </summary>
	public string? Prompt { get; init; }

	/// <summary>
	/// Error message (for Error events).
	/// </summary>
	public string? Error { get; init; }

	/// <summary>
	/// Error context/category (for Error events, e.g. "model_call", "tool_execution").
	/// </summary>
	public string? ErrorContext { get; init; }

	/// <summary>
	/// Error handling decision (for Error events: "retry", "skip", "abort").
	/// </summary>
	public string? ErrorHandling { get; init; }

	/// <summary>
	/// Additional context injected by hooks (e.g., session start context, post-tool notes).
	/// </summary>
	public string? AdditionalContext { get; init; }

	/// <summary>
	/// The session lifecycle source (for SessionStart: "startup", "resume", "new").
	/// </summary>
	public string? SessionSource { get; init; }

	/// <summary>
	/// The session end reason (for SessionEnd events).
	/// </summary>
	public string? SessionEndReason { get; init; }

	/// <summary>
	/// The hook type (for HookStart, HookEnd events, e.g. "preToolUse", "postToolUse", "sessionStart").
	/// </summary>
	public string? HookType { get; init; }

	/// <summary>
	/// Unique identifier for a hook invocation (for HookStart, HookEnd events).
	/// </summary>
	public string? HookInvocationId { get; init; }

	/// <summary>
	/// Whether the hook completed successfully (for HookEnd events).
	/// </summary>
	public bool? HookSuccess { get; init; }

	/// <summary>
	/// The turn identifier (for TurnStart events).
	/// </summary>
	public string? TurnId { get; init; }

	/// <summary>
	/// Maximum context window token limit (for SessionUsageInfo events).
	/// </summary>
	public double? TokenLimit { get; init; }

	/// <summary>
	/// Current token count used in the session (for SessionUsageInfo events).
	/// </summary>
	public double? CurrentTokens { get; init; }

	// ── Auto-mode switch + system notification telemetry (SDK 0.3.0) ──

	/// <summary>SDK request id correlating an auto-mode switch request with its completion.</summary>
	public string? AutoModeRequestId { get; init; }

	/// <summary>SDK error code that triggered an auto-mode switch (e.g. rate-limit code).</summary>
	public string? AutoModeErrorCode { get; init; }

	/// <summary>SDK response on auto-mode completion (typically the new model name).</summary>
	public string? AutoModeResponse { get; init; }

	/// <summary>System notification kind discriminator (agent_completed, shell_completed, etc.).</summary>
	public string? NotificationKind { get; init; }

	/// <summary>System notification message body.</summary>
	public string? NotificationMessage { get; init; }

	// ── Per-call permission lifecycle (SDK 1.0.0) ──

	/// <summary>
	/// Correlation id linking a <see cref="AuditEventType.PermissionRequested"/> entry
	/// to its matching <see cref="AuditEventType.PermissionCompleted"/> entry. Comes
	/// directly from the SDK's <c>PermissionRequestedData.RequestId</c> /
	/// <c>PermissionCompletedData.RequestId</c>.
	/// </summary>
	public string? PermissionRequestId { get; init; }

	/// <summary>
	/// Kind discriminator for the permission request (on
	/// <see cref="AuditEventType.PermissionRequested"/> entries): <c>"read"</c>,
	/// <c>"write"</c>, <c>"shell"</c>, <c>"url"</c>, <c>"mcp"</c>, <c>"memory"</c>,
	/// <c>"customTool"</c>, <c>"hook"</c>, <c>"extensionManagement"</c>,
	/// <c>"extensionPermissionAccess"</c>, or <c>"unknown"</c> for a future SDK kind.
	/// </summary>
	public string? PermissionKind { get; init; }

	/// <summary>
	/// Human-readable summary of the resource the permission applies to. Depends on
	/// <see cref="PermissionKind"/>: path for read/write, full command text for shell,
	/// URL for url, <c>"server::tool"</c> for mcp, subject for memory, tool name for
	/// customTool/hook, extension name for the extension kinds.
	/// </summary>
	public string? PermissionTarget { get; init; }

	/// <summary>
	/// The tool call id that triggered the permission request, when supplied by the SDK.
	/// Lets consumers stitch a Permission* audit entry to the originating
	/// <see cref="ToolName"/>-bearing PreToolUse / PostToolUse entry.
	/// </summary>
	public string? PermissionToolCallId { get; init; }

	/// <summary>
	/// Optional human-readable context for a permission decision on a
	/// <see cref="AuditEventType.PermissionCompleted"/> entry: location key for
	/// <c>approvedForLocation</c>, denial message / feedback / rule list for the denied
	/// results, cancellation reason for <c>cancelled</c>.
	/// </summary>
	public string? PermissionDecisionReason { get; init; }
}

/// <summary>
/// Types of audit events captured by session hooks.
/// </summary>
public enum AuditEventType
{
	/// <summary>Session started or resumed.</summary>
	SessionStart,

	/// <summary>User prompt was submitted.</summary>
	PromptSubmitted,

	/// <summary>A tool call is about to execute (pre-hook).</summary>
	PreToolUse,

	/// <summary>A tool call completed (post-hook).</summary>
	PostToolUse,

	/// <summary>
	/// A tool call completed with a failure. SDK 1.0.0 (PR #1013) introduced a dedicated
	/// failure-side hook (<c>SessionHooks.OnPostToolUseFailure</c>) that fires only when
	/// the tool execution errored — the success-path <see cref="PostToolUse"/> entry is
	/// not emitted in that case. Separating success and failure into distinct entries
	/// keeps audit-log consumers from having to inspect <c>ToolSuccess</c> to decide
	/// whether a tool call faulted; it also makes failure-rate analytics cheaper.
	/// </summary>
	PostToolUseFailure,

	/// <summary>An error occurred during the session.</summary>
	Error,

	/// <summary>The session ended.</summary>
	SessionEnd,

	/// <summary>Context compaction started (infinite sessions).</summary>
	CompactionStart,

	/// <summary>Context compaction completed (infinite sessions).</summary>
	CompactionComplete,

	/// <summary>An SDK hook started executing.</summary>
	HookStart,

	/// <summary>An SDK hook completed.</summary>
	HookEnd,

	/// <summary>A new assistant turn started in a multi-turn conversation.</summary>
	TurnStart,

	/// <summary>An assistant turn ended in a multi-turn conversation.</summary>
	TurnEnd,

	/// <summary>Session-level token usage information was received.</summary>
	SessionUsageInfo,

	/// <summary>SDK requested an auto-mode model switch (rate-limit or transient failure). SDK 0.3.0.</summary>
	AutoModeSwitchRequested,

	/// <summary>Auto-mode switch completed; new model is active. SDK 0.3.0.</summary>
	AutoModeSwitchCompleted,

	/// <summary>CLI emitted a system notification (agent_idle, shell_completed, etc.). SDK 0.3.0.</summary>
	SystemNotification,

	/// <summary>Per-bucket quota / entitlement snapshot was received with a usage event. SDK 0.3.0.</summary>
	QuotaSnapshot,

	/// <summary>
	/// SDK 1.0.0 <c>PermissionRequestedEvent</c>: a per-call permission gate fired before
	/// a side-effectful action (file read/write, shell command, URL fetch, MCP tool,
	/// memory access, custom tool, hook, extension management). Orchestra uses
	/// <c>PermissionHandler.ApproveAll</c> so every request resolves to "approved" in
	/// practice, but the audit entry captures exactly what was requested for compliance /
	/// forensic review. Pairs with <see cref="PermissionCompleted"/> via
	/// <see cref="AuditLogEntry.PermissionRequestId"/>.
	/// </summary>
	PermissionRequested,

	/// <summary>
	/// SDK 1.0.0 <c>PermissionCompletedEvent</c>: completion of a prior
	/// <see cref="PermissionRequested"/>, carrying the result kind
	/// (<see cref="AuditLogEntry.PermissionDecision"/>) and any contextual reason
	/// (<see cref="AuditLogEntry.PermissionDecisionReason"/>).
	/// </summary>
	PermissionCompleted,
}
