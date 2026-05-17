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
	/// Full paths of files saved by this step via orchestra_save_file.
	/// </summary>
	public string[] SavedFiles { get; init; } = [];

	/// <summary>
	/// History of retry attempts for this step, if retries occurred.
	/// </summary>
	public List<RetryAttemptRecord>? RetryHistory { get; init; }

	/// <summary>
	/// Structured error category for failures.
	/// </summary>
	public StepErrorCategory? ErrorCategory { get; init; }

	/// <summary>
	/// Structured details from the underlying agent session error, when the failure
	/// came from a Copilot SDK <c>session.error</c> event (HTTP status, request id,
	/// upstream URL, stack). Null when the failure had no corresponding SDK payload
	/// (e.g. cancellation, validation, command exit code, no-action override).
	/// Persisted into <c>run.json</c> via <see cref="StepRunRecord.ErrorDetails"/>.
	/// </summary>
	public AgentSessionErrorDetails? ErrorDetails { get; init; }

	/// <summary>
	/// The terminal status the step's LLM observed via <c>orchestra_set_status</c>
	/// before the result was finalised, or <c>null</c> if no such call was made.
	/// Captured even when an outer failure (e.g. agent transport error) ultimately
	/// produced a <see cref="ExecutionStatus.Failed"/> result so the executor-level
	/// swap-retry loop can detect "the LLM already decided" and skip the retry —
	/// otherwise the retry would re-run the prompt, discard the captured override,
	/// and potentially flip an LLM-declared success into a swap-induced failure.
	/// Not persisted into <c>run.json</c>; lives only inside the engine's executor
	/// pipeline.
	/// </summary>
	public ExecutionStatus? CapturedStatusOverride { get; init; }

	/// <summary>
	/// Set only for steps of type <see cref="OrchestrationStepType.Orchestration"/>.
	/// Carries the child run's execution id, per-step results, error message, and
	/// cancellation details, enabling parent templates to drill into the child's data
	/// via <c>{{stepName.executionId}}</c>, <c>{{stepName.steps.&lt;childStep&gt;.output}}</c>,
	/// etc. Null for all other step types.
	/// </summary>
	public ChildOrchestrationInfo? ChildOrchestrationInfo { get; init; }

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
		AvailableModelInfo? actualModelInfo = null,
		string[]? savedFiles = null,
		ChildOrchestrationInfo? childOrchestrationInfo = null) => new()
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
		SavedFiles = savedFiles ?? [],
		ChildOrchestrationInfo = childOrchestrationInfo,
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
		AvailableModelInfo? actualModelInfo = null,
		string[]? savedFiles = null,
		ChildOrchestrationInfo? childOrchestrationInfo = null,
		AgentSessionErrorDetails? errorDetails = null) => new()
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
		ErrorDetails = errorDetails,
		RetryHistory = retryHistory,
		SavedFiles = savedFiles ?? [],
		ChildOrchestrationInfo = childOrchestrationInfo,
	};

	public static ExecutionResult Skipped(string reason) => new()
	{
		Content = string.Empty,
		Status = ExecutionStatus.Skipped,
		ErrorMessage = reason,
	};

	public static ExecutionResult Cancelled(string? errorMessage = null, string[]? savedFiles = null) => new()
	{
		Content = string.Empty,
		Status = ExecutionStatus.Cancelled,
		ErrorMessage = errorMessage ?? "Cancelled",
		SavedFiles = savedFiles ?? [],
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
		AvailableModelInfo? actualModelInfo = null,
		string[]? savedFiles = null) => new()
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
		SavedFiles = savedFiles ?? [],
	};
}
