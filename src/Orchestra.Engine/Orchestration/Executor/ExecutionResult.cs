namespace Orchestra.Engine;

public class ExecutionResult
{
	public required string Content { get; init; }
	public required ExecutionStatus Status { get; init; }
	public string? ErrorMessage { get; init; }

	/// <summary>
	/// The raw content before output handler was applied.
	/// Null when no output handler exists or for non-succeeded results.
	/// </summary>
	public string? RawContent { get; init; }

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
	/// Token usage statistics for this step.
	/// </summary>
	public TokenUsage? Usage { get; init; }

	/// <summary>
	/// Detailed execution trace for debugging and inspection.
	/// </summary>
	public StepExecutionTrace? Trace { get; init; }

	public static ExecutionResult Succeeded(
		string content,
		string? rawContent = null,
		Dictionary<string, string>? rawDependencyOutputs = null,
		string? promptSent = null,
		string? actualModel = null,
		TokenUsage? usage = null,
		StepExecutionTrace? trace = null) => new()
	{
		Content = content,
		Status = ExecutionStatus.Succeeded,
		RawContent = rawContent,
		RawDependencyOutputs = rawDependencyOutputs ?? [],
		PromptSent = promptSent,
		ActualModel = actualModel,
		Usage = usage,
		Trace = trace,
	};

	public static ExecutionResult Failed(
		string errorMessage,
		Dictionary<string, string>? rawDependencyOutputs = null,
		string? promptSent = null,
		string? actualModel = null,
		StepExecutionTrace? trace = null) => new()
	{
		Content = string.Empty,
		Status = ExecutionStatus.Failed,
		ErrorMessage = errorMessage,
		RawDependencyOutputs = rawDependencyOutputs ?? [],
		PromptSent = promptSent,
		ActualModel = actualModel,
		Trace = trace,
	};

	public static ExecutionResult Skipped(string reason) => new()
	{
		Content = string.Empty,
		Status = ExecutionStatus.Skipped,
		ErrorMessage = reason,
	};

	public static ExecutionResult Cancelled(string? errorMessage = null) => new()
	{
		Content = string.Empty,
		Status = ExecutionStatus.Cancelled,
		ErrorMessage = errorMessage ?? "Cancelled",
	};
}
