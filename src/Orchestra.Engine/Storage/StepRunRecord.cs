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
