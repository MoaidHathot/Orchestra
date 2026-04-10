namespace Orchestra.Engine;

/// <summary>
/// Represents a checkpoint of an orchestration execution in progress.
/// Captures the state of all completed steps so execution can be resumed
/// from the last checkpoint if the process crashes or is interrupted.
/// </summary>
public class CheckpointData
{
	/// <summary>
	/// Unique run identifier for this execution.
	/// </summary>
	public required string RunId { get; init; }

	/// <summary>
	/// Name of the orchestration being executed.
	/// </summary>
	public required string OrchestrationName { get; init; }

	/// <summary>
	/// When the execution started.
	/// </summary>
	public required DateTimeOffset StartedAt { get; init; }

	/// <summary>
	/// When this checkpoint was created.
	/// </summary>
	public required DateTimeOffset CheckpointedAt { get; init; }

	/// <summary>
	/// Parameters provided for this run.
	/// </summary>
	public Dictionary<string, string> Parameters { get; init; } = [];

	/// <summary>
	/// Optional trigger ID that initiated this run.
	/// </summary>
	public string? TriggerId { get; init; }

	/// <summary>
	/// Results of all completed steps, keyed by step name.
	/// Each entry contains the full <see cref="ExecutionResult"/> serialized
	/// so it can be restored into the execution context on resume.
	/// </summary>
	public required Dictionary<string, CheckpointStepResult> CompletedSteps { get; init; }
}

/// <summary>
/// Serializable representation of a step's execution result for checkpoint persistence.
/// </summary>
public class CheckpointStepResult
{
	/// <summary>
	/// The execution status of the step.
	/// </summary>
	public required ExecutionStatus Status { get; init; }

	/// <summary>
	/// The final content after all handlers.
	/// </summary>
	public required string Content { get; init; }

	/// <summary>
	/// The raw content before output handler was applied.
	/// </summary>
	public string? RawContent { get; init; }

	/// <summary>
	/// Error message if the step failed.
	/// </summary>
	public string? ErrorMessage { get; init; }

	/// <summary>
	/// Raw dependency outputs used by this step.
	/// </summary>
	public Dictionary<string, string> RawDependencyOutputs { get; init; } = [];

	/// <summary>
	/// The prompt that was sent to the LLM.
	/// </summary>
	public string? PromptSent { get; init; }

	/// <summary>
	/// The actual model used for execution.
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

	/// <summary>
	/// Retry history for this step.
	/// </summary>
	public List<RetryAttemptRecord>? RetryHistory { get; init; }

	/// <summary>
	/// Structured error category for failures.
	/// </summary>
	public StepErrorCategory? ErrorCategory { get; init; }

	/// <summary>
	/// Converts this checkpoint step result to an <see cref="ExecutionResult"/>.
	/// </summary>
	public ExecutionResult ToExecutionResult() => new()
	{
		Status = Status,
		Content = Content,
		RawContent = RawContent,
		ErrorMessage = ErrorMessage,
		RawDependencyOutputs = RawDependencyOutputs,
		PromptSent = PromptSent,
		ActualModel = ActualModel,
		Usage = Usage,
		Trace = Trace,
		RetryHistory = RetryHistory,
		ErrorCategory = ErrorCategory,
	};

	/// <summary>
	/// Creates a <see cref="CheckpointStepResult"/> from an <see cref="ExecutionResult"/>.
	/// </summary>
	public static CheckpointStepResult FromExecutionResult(ExecutionResult result) => new()
	{
		Status = result.Status,
		Content = result.Content,
		RawContent = result.RawContent,
		ErrorMessage = result.ErrorMessage,
		RawDependencyOutputs = result.RawDependencyOutputs is Dictionary<string, string> dict
			? dict
			: new Dictionary<string, string>(result.RawDependencyOutputs),
		PromptSent = result.PromptSent,
		ActualModel = result.ActualModel,
		Usage = result.Usage,
		Trace = result.Trace,
		RetryHistory = result.RetryHistory,
		ErrorCategory = result.ErrorCategory,
	};
}
