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
	/// The permission decision made for a tool call (allow, deny, ask).
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

	/// <summary>Session-level token usage information was received.</summary>
	SessionUsageInfo,
}
