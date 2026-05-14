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
	/// The model selected by the server at session start.
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
	/// Retry history for this step.
	/// </summary>
	public List<RetryAttemptRecord>? RetryHistory { get; init; }

	/// <summary>
	/// Structured error category for failures.
	/// </summary>
	public StepErrorCategory? ErrorCategory { get; init; }

	// ── Child orchestration pointer (Orchestration steps only) ──
	//
	// We persist ONLY the pointer triple — not the child's full StepResults map —
	// to keep checkpoints small even for deeply-nested orchestration trees. At
	// resume time the executor reads the child's own run.json via IRunStore and
	// reconstructs a ChildOrchestrationInfo on demand, so downstream steps in a
	// retry can still resolve template bindings like
	// {{stepName.steps.<childStep>.output}} and {{stepName.executionId}}.
	// Older checkpoints (predating these fields) have nulls here — they deserialize
	// cleanly and the executor simply skips rehydration for those steps.

	/// <summary>
	/// For Orchestration-step checkpoints: execution id of the child run. Null on
	/// all other step types and on legacy checkpoints.
	/// </summary>
	public string? ChildExecutionId { get; init; }

	/// <summary>
	/// For Orchestration-step checkpoints: the child orchestration's name.
	/// Required (alongside <see cref="ChildExecutionId"/>) to look up the child's
	/// run.json at rehydration time.
	/// </summary>
	public string? ChildOrchestrationName { get; init; }

	/// <summary>
	/// For Orchestration-step checkpoints: the child's terminal status as seen by
	/// the parent. Used by rehydration to short-circuit when the child's persisted
	/// record disagrees (which would indicate the child was retried or deleted).
	/// </summary>
	public ExecutionStatus? ChildStatus { get; init; }

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
		SelectedModel = SelectedModel,
		RequestedModelInfo = RequestedModelInfo,
		SelectedModelInfo = SelectedModelInfo,
		ActualModelInfo = ActualModelInfo,
		Usage = Usage,
		Trace = Trace,
		SavedFiles = SavedFiles,
		RetryHistory = RetryHistory,
		ErrorCategory = ErrorCategory,
		// NOTE: ChildOrchestrationInfo is intentionally NOT reconstructed here.
		// The full per-step data lives in the child's own run.json; the parent's
		// retry/resume path rehydrates it via IRunStore.GetRunAsync after this
		// restore completes. See OrchestrationExecutor's rehydration pass.
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
		SelectedModel = result.SelectedModel,
		RequestedModelInfo = result.RequestedModelInfo,
		SelectedModelInfo = result.SelectedModelInfo,
		ActualModelInfo = result.ActualModelInfo,
		Usage = result.Usage,
		Trace = result.Trace,
		SavedFiles = result.SavedFiles,
		RetryHistory = result.RetryHistory,
		ErrorCategory = result.ErrorCategory,
		// Capture just the pointer triple from ChildOrchestrationInfo (if any).
		// The child's per-step content lives on its own run.json — we don't
		// inline it here, mirroring the parent run.json's storage decision.
		ChildExecutionId = result.ChildOrchestrationInfo?.ExecutionId,
		ChildOrchestrationName = result.ChildOrchestrationInfo?.OrchestrationName,
		ChildStatus = result.ChildOrchestrationInfo?.Status,
	};
}
