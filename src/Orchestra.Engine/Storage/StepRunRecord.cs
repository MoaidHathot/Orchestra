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
	public Dictionary<string, string> Parameters { get; init; } = [];

	/// <summary>
	/// Loop iteration number (null if not a loop iteration, 0 for the initial run).
	/// </summary>
	public int? LoopIteration { get; init; }

	/// <summary>
	/// The raw dependency outputs before any prompt construction.
	/// Key is dependency step name, value is the raw output from that step.
	/// </summary>
	public Dictionary<string, string> RawDependencyOutputs { get; init; } = [];

	/// <summary>
	/// The actual prompt that was sent to the LLM (after all substitutions and handlers).
	/// </summary>
	public string? PromptSent { get; init; }

	/// <summary>
	/// The actual model identifier used for this step execution.
	/// </summary>
	public string? ActualModel { get; init; }

	/// <summary>
	/// Token usage statistics for this step (input tokens, output tokens, total).
	/// </summary>
	public TokenUsage? Usage { get; init; }

	/// <summary>
	/// Detailed execution trace for debugging and inspection.
	/// Contains reasoning, tool calls, and response segments in execution order.
	/// </summary>
	public StepExecutionTrace? Trace { get; init; }
}

/// <summary>
/// Token usage statistics for an LLM call.
/// </summary>
public class TokenUsage
{
	public int InputTokens { get; init; }
	public int OutputTokens { get; init; }
	public int TotalTokens => InputTokens + OutputTokens;
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
