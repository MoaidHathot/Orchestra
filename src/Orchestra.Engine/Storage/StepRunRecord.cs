namespace Orchestra.Engine;

/// <summary>
/// Detailed record of a single step execution, including timing and handler data.
/// Used for run persistence and history tracking.
/// </summary>
public class StepRunRecord
{
	public required string StepName { get; init; }
	public required ExecutionStatus Status { get; init; }
	public required DateTimeOffset StartedAt { get; init; }
	public required DateTimeOffset CompletedAt { get; init; }
	public TimeSpan Duration => CompletedAt - StartedAt;

	/// <summary>
	/// The final content after all handlers have been applied.
	/// </summary>
	public required string Content { get; init; }

	/// <summary>
	/// The raw content before the output handler was applied (null if no output handler).
	/// </summary>
	public string? RawContent { get; init; }

	/// <summary>
	/// Error message if the step failed.
	/// </summary>
	public string? ErrorMessage { get; init; }

	/// <summary>
	/// The actual parameters that were injected into this step.
	/// </summary>
	public IReadOnlyDictionary<string, string> Parameters { get; init; } = new Dictionary<string, string>();

	/// <summary>
	/// Loop iteration number (null if not a loop iteration, 0 for the initial run).
	/// </summary>
	public int? LoopIteration { get; init; }

	/// <summary>
	/// The raw dependency outputs before any prompt construction.
	/// Key is dependency step name, value is the raw output from that step.
	/// </summary>
	public IReadOnlyDictionary<string, string> RawDependencyOutputs { get; init; } = new Dictionary<string, string>();

	/// <summary>
	/// The actual prompt that was sent to the LLM (after all substitutions and handlers).
	/// </summary>
	public string? PromptSent { get; init; }

	/// <summary>
	/// The actual model identifier used for this step execution.
	/// </summary>
	public string? ActualModel { get; init; }

	/// <summary>
	/// The model selected by the server at session start.
	/// May differ from the configured model if the server substituted.
	/// </summary>
	public string? SelectedModel { get; init; }

	/// <summary>
	/// Token usage statistics for this step (input tokens, output tokens, total).
	/// </summary>
	public TokenUsage? Usage { get; init; }

	/// <summary>
	/// Detailed execution trace for debugging and inspection.
	/// Contains reasoning, tool calls, and response segments in execution order.
	/// </summary>
	public StepExecutionTrace? Trace { get; init; }

	/// <summary>
	/// History of retry attempts for this step, if retries occurred.
	/// Each entry records the error, timestamp, and delay for that attempt.
	/// Only the final (successful or exhausted) attempt is in the main record fields.
	/// </summary>
	public List<RetryAttemptRecord>? RetryHistory { get; init; }

	/// <summary>
	/// Structured error category for the step failure (if any).
	/// Enables filtering and aggregation by error type.
	/// </summary>
	public StepErrorCategory? ErrorCategory { get; init; }
}

/// <summary>
/// Token usage statistics for an LLM call.
/// </summary>
public class TokenUsage
{
	public int InputTokens { get; init; }
	public int OutputTokens { get; init; }
	public int TotalTokens => InputTokens + OutputTokens;

	/// <summary>
	/// Tokens read from the prompt cache (reduces cost).
	/// </summary>
	public int CacheReadTokens { get; init; }

	/// <summary>
	/// Tokens written to the prompt cache for future use.
	/// </summary>
	public int CacheWriteTokens { get; init; }

	/// <summary>
	/// Estimated cost of this LLM call (provider-specific units).
	/// </summary>
	public double? Cost { get; init; }

	/// <summary>
	/// Duration of the LLM call in seconds (as reported by the provider).
	/// </summary>
	public double? Duration { get; init; }
}

/// <summary>
/// Record of a single retry attempt for a step.
/// </summary>
public class RetryAttemptRecord
{
	/// <summary>
	/// The attempt number (1-based).
	/// </summary>
	public required int Attempt { get; init; }

	/// <summary>
	/// The error that caused the retry.
	/// </summary>
	public required string Error { get; init; }

	/// <summary>
	/// When this attempt was made.
	/// </summary>
	public required DateTimeOffset AttemptedAt { get; init; }

	/// <summary>
	/// The delay before the next retry in seconds.
	/// </summary>
	public required double DelaySeconds { get; init; }

	/// <summary>
	/// The error category for this attempt.
	/// </summary>
	public StepErrorCategory? ErrorCategory { get; init; }
}

/// <summary>
/// Structured error category for step failures.
/// </summary>
public enum StepErrorCategory
{
	/// <summary>Unknown or uncategorized error.</summary>
	Unknown,

	/// <summary>Step exceeded its timeout.</summary>
	Timeout,

	/// <summary>An MCP server failed to start or connect.</summary>
	McpFailure,

	/// <summary>The LLM model returned an error or was unavailable.</summary>
	ModelError,

	/// <summary>A tool call failed.</summary>
	ToolError,

	/// <summary>A network error occurred (HTTP step, MCP connection, etc.).</summary>
	NetworkError,

	/// <summary>Template resolution or parameter validation failed.</summary>
	ValidationError,

	/// <summary>A command execution failed.</summary>
	CommandError,

	/// <summary>An HTTP request returned a non-success status code.</summary>
	HttpError,

	/// <summary>Template transform evaluation failed.</summary>
	TransformError,

	/// <summary>
	/// The underlying agent client (e.g. Copilot CLI) has been declared unhealthy
	/// for the remainder of the run scope. Subsequent retries on the same client
	/// are guaranteed to fail and should be skipped.
	/// </summary>
	ClientUnhealthy,
}

/// <summary>
/// Detailed execution trace for a step, capturing all events in order.
/// </summary>
public class StepExecutionTrace
{
	/// <summary>
	/// The system prompt used for this step.
	/// </summary>
	public string? SystemPrompt { get; init; }

	/// <summary>
	/// The user prompt before input handler was applied.
	/// </summary>
	public string? UserPromptRaw { get; init; }

	/// <summary>
	/// The user prompt after input handler was applied (same as PromptSent).
	/// </summary>
	public string? UserPromptProcessed { get; init; }

	/// <summary>
	/// Full reasoning content (aggregated from all reasoning deltas).
	/// </summary>
	public string? Reasoning { get; init; }

	/// <summary>
	/// MCP tool calls in execution order.
	/// </summary>
	public List<ToolCallRecord> ToolCalls { get; init; } = [];

	/// <summary>
	/// Response segments from the LLM (aggregated, not deltas).
	/// Multiple segments if the LLM responds in multiple turns (e.g., after tool calls).
	/// </summary>
	public List<string> ResponseSegments { get; init; } = [];

	/// <summary>
	/// The final response before output handler was applied.
	/// </summary>
	public string? FinalResponse { get; init; }

	/// <summary>
	/// The response after output handler was applied (same as Content).
	/// </summary>
	public string? OutputHandlerResult { get; init; }

	/// <summary>
	/// MCP server configurations used by this step, for diagnostics.
	/// Each entry contains the server name and type (e.g., "icm (local: dnx Icm.Mcp ...)").
	/// </summary>
	public List<string> McpServers { get; init; } = [];

	/// <summary>
	/// Session warnings received from the SDK during execution (e.g., MCP server startup failures).
	/// </summary>
	public List<string> Warnings { get; init; } = [];

	/// <summary>
	/// Full conversation history in message order (system, user, assistant, tool results).
	/// Captures the complete multi-turn exchange for debugging multi-turn prompt steps.
	/// </summary>
	public List<ConversationMessage> ConversationHistory { get; init; } = [];

	/// <summary>
	/// Structured audit log entries captured by session hooks.
	/// Records tool permissions, prompt submissions, session lifecycle, and errors
	/// for compliance, debugging, and observability.
	/// </summary>
	public List<AuditLogEntry> AuditLog { get; init; } = [];
}

/// <summary>
/// A single message in the conversation history.
/// </summary>
public class ConversationMessage
{
	/// <summary>
	/// The role of the message sender (system, user, assistant, tool).
	/// </summary>
	public required string Role { get; init; }

	/// <summary>
	/// The content of the message.
	/// </summary>
	public string? Content { get; init; }

	/// <summary>
	/// Tool call ID if this is a tool result message.
	/// </summary>
	public string? ToolCallId { get; init; }

	/// <summary>
	/// Tool name if this is a tool call or tool result.
	/// </summary>
	public string? ToolName { get; init; }

	/// <summary>
	/// Timestamp when this message was recorded.
	/// </summary>
	public DateTimeOffset Timestamp { get; init; }
}

/// <summary>
/// Record of a single MCP tool call.
/// </summary>
public class ToolCallRecord
{
	/// <summary>
	/// Unique identifier for this tool call.
	/// </summary>
	public string? CallId { get; init; }

	/// <summary>
	/// The MCP server that owns this tool.
	/// </summary>
	public string? McpServer { get; init; }

	/// <summary>
	/// The name of the tool that was called.
	/// </summary>
	public required string ToolName { get; init; }

	/// <summary>
	/// The arguments passed to the tool (JSON string).
	/// </summary>
	public string? Arguments { get; init; }

	/// <summary>
	/// Whether the tool call succeeded.
	/// </summary>
	public bool Success { get; init; }

	/// <summary>
	/// The result returned by the tool.
	/// </summary>
	public string? Result { get; init; }

	/// <summary>
	/// Error message if the tool call failed.
	/// </summary>
	public string? Error { get; init; }

	/// <summary>
	/// When the tool call started.
	/// </summary>
	public DateTimeOffset? StartedAt { get; init; }

	/// <summary>
	/// When the tool call completed.
	/// </summary>
	public DateTimeOffset? CompletedAt { get; init; }
}
