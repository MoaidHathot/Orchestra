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
	/// The model selected by the server at session start.
	/// May differ from the configured model if the server substituted.
	/// </summary>
	public string? SelectedModel { get; init; }

	/// <summary>
	/// SDK-reported metadata for the configured/requested model.
	/// </summary>
	public AvailableModelInfo? RequestedModelInfo { get; init; }

	/// <summary>
	/// SDK-reported metadata for the server-selected model.
	/// </summary>
	public AvailableModelInfo? SelectedModelInfo { get; init; }

	/// <summary>
	/// SDK-reported metadata for the actual model that produced the response.
	/// </summary>
	public AvailableModelInfo? ActualModelInfo { get; init; }

	/// <summary>
	/// Token usage statistics for this step.
	/// </summary>
	public TokenUsage? Usage { get; init; }

	/// <summary>
	/// Detailed execution trace for debugging and inspection.
	/// </summary>
	public StepExecutionTrace? Trace { get; init; }

	/// <summary>
	/// History of retry attempts for this step, if retries occurred.
	/// </summary>
	public List<RetryAttemptRecord>? RetryHistory { get; init; }

	/// <summary>
	/// Structured error category for failures.
	/// </summary>
	public StepErrorCategory? ErrorCategory { get; init; }

	/// <summary>
	/// When true, signals that the entire orchestration should complete immediately.
	/// Set by the orchestra_complete engine tool.
	/// </summary>
	public bool OrchestrationCompleteRequested { get; init; }

	/// <summary>
	/// The status to use for orchestration completion when <see cref="OrchestrationCompleteRequested"/> is true.
	/// </summary>
	public ExecutionStatus? OrchestrationCompleteStatus { get; init; }

	/// <summary>
	/// The name of the step that triggered orchestration completion when <see cref="OrchestrationCompleteRequested"/> is true.
	/// </summary>
	public string? OrchestrationCompleteStepName { get; init; }

	/// <summary>
	/// The reason for orchestration completion when <see cref="OrchestrationCompleteRequested"/> is true.
	/// </summary>
	public string? OrchestrationCompleteReason { get; init; }

	public static ExecutionResult Succeeded(
		string content,
		string? rawContent = null,
		Dictionary<string, string>? rawDependencyOutputs = null,
		string? promptSent = null,
		string? actualModel = null,
		TokenUsage? usage = null,
		StepExecutionTrace? trace = null,
		List<RetryAttemptRecord>? retryHistory = null,
		string? selectedModel = null,
		AvailableModelInfo? requestedModelInfo = null,
		AvailableModelInfo? selectedModelInfo = null,
		AvailableModelInfo? actualModelInfo = null) => new()
	{
		Content = content,
		Status = ExecutionStatus.Succeeded,
		RawContent = rawContent,
		RawDependencyOutputs = rawDependencyOutputs ?? [],
		PromptSent = promptSent,
		ActualModel = actualModel,
		SelectedModel = selectedModel,
		RequestedModelInfo = requestedModelInfo,
		SelectedModelInfo = selectedModelInfo,
		ActualModelInfo = actualModelInfo,
		Usage = usage,
		Trace = trace,
		RetryHistory = retryHistory,
	};

	public static ExecutionResult Failed(
		string errorMessage,
		Dictionary<string, string>? rawDependencyOutputs = null,
		string? promptSent = null,
		string? actualModel = null,
		StepExecutionTrace? trace = null,
		StepErrorCategory errorCategory = StepErrorCategory.Unknown,
		List<RetryAttemptRecord>? retryHistory = null,
		string? selectedModel = null,
		AvailableModelInfo? requestedModelInfo = null,
		AvailableModelInfo? selectedModelInfo = null,
		AvailableModelInfo? actualModelInfo = null) => new()
	{
		Content = string.Empty,
		Status = ExecutionStatus.Failed,
		ErrorMessage = errorMessage,
		RawDependencyOutputs = rawDependencyOutputs ?? [],
		PromptSent = promptSent,
		ActualModel = actualModel,
		SelectedModel = selectedModel,
		RequestedModelInfo = requestedModelInfo,
		SelectedModelInfo = selectedModelInfo,
		ActualModelInfo = actualModelInfo,
		Trace = trace,
		ErrorCategory = errorCategory,
		RetryHistory = retryHistory,
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

	/// <summary>
	/// Creates a NoAction result indicating the step completed but there is nothing to do.
	/// Downstream steps that depend on this step will be skipped.
	/// </summary>
	public static ExecutionResult NoAction(
		string reason,
		Dictionary<string, string>? rawDependencyOutputs = null,
		string? promptSent = null,
		string? actualModel = null,
		TokenUsage? usage = null,
		StepExecutionTrace? trace = null,
		string? selectedModel = null,
		AvailableModelInfo? requestedModelInfo = null,
		AvailableModelInfo? selectedModelInfo = null,
		AvailableModelInfo? actualModelInfo = null) => new()
	{
		Content = reason,
		Status = ExecutionStatus.NoAction,
		RawDependencyOutputs = rawDependencyOutputs ?? [],
		PromptSent = promptSent,
		ActualModel = actualModel,
		SelectedModel = selectedModel,
		RequestedModelInfo = requestedModelInfo,
		SelectedModelInfo = selectedModelInfo,
		ActualModelInfo = actualModelInfo,
		Usage = usage,
		Trace = trace,
	};
}
