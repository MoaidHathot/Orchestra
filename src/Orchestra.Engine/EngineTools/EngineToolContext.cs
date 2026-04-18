namespace Orchestra.Engine;

/// <summary>
/// Shared mutable context passed to engine tools during a prompt step execution.
/// Engine tools record side effects here, which the executor inspects after completion.
/// </summary>
public sealed class EngineToolContext
{
	/// <summary>
	/// The temp file store for the current orchestration run.
	/// Provides file I/O operations scoped to a run-specific temp directory.
	/// May be null when no data path is configured (e.g., in-memory mode).
	/// </summary>
	public OrchestrationTempFileStore? TempFileStore { get; init; }

	/// <summary>
	/// The reporter for emitting live events (e.g., step-status-set).
	/// May be null when no reporter is configured.
	/// </summary>
	public IOrchestrationReporter? Reporter { get; init; }

	/// <summary>
	/// The name of the step this context belongs to.
	/// Used by engine tools to register artifacts (e.g., saved files) against the correct step.
	/// </summary>
	public string? StepName { get; init; }
	/// <summary>
	/// When set, the prompt step result will be overridden to the specified status
	/// regardless of the LLM's output content.
	/// </summary>
	public ExecutionStatus? StatusOverride { get; private set; }

	/// <summary>
	/// The reason provided by the LLM when signaling the execution status via the set_status tool.
	/// </summary>
	public string? StatusReason { get; private set; }

	/// <summary>
	/// Whether the status has been explicitly set by an engine tool.
	/// </summary>
	public bool HasStatusOverride => StatusOverride is not null;

	/// <summary>
	/// When set, signals that the entire orchestration should complete immediately.
	/// All pending and running steps will be cancelled.
	/// </summary>
	public bool OrchestrationCompleteRequested { get; private set; }

	/// <summary>
	/// The status to use for the orchestration completion (success or failed).
	/// </summary>
	public ExecutionStatus? OrchestrationCompleteStatus { get; private set; }

	/// <summary>
	/// The reason for orchestration completion.
	/// </summary>
	public string? OrchestrationCompleteReason { get; private set; }

	/// <summary>
	/// Whether an engine tool has signaled that the step should stop immediately
	/// (e.g., after calling <see cref="SetStatusTool"/> with a terminal status).
	/// </summary>
	public bool StepCompletionRequested { get; private set; }

	/// <summary>
	/// Cancellation token source that the executor sets before running the agent.
	/// When <see cref="RequestStepCompletion"/> is called, this is cancelled to
	/// interrupt the agent session so the status override takes effect immediately.
	/// </summary>
	internal CancellationTokenSource? StepCompletionCts { get; set; }

	/// <summary>
	/// Sets the execution status override. Can only transition to a "worse" state
	/// (e.g., from null to Failed). Once failed, cannot be reset to succeeded.
	/// NoAction can transition to Failed but not back to Succeeded.
	/// </summary>
	public void SetStatus(ExecutionStatus status, string? reason = null)
	{
		// Only allow setting status if not already failed
		if (StatusOverride == ExecutionStatus.Failed)
			return;

		StatusOverride = status;
		StatusReason = reason;
	}

	/// <summary>
	/// Signals that the current step should complete immediately. The agent session
	/// will be cancelled and the executor will use the <see cref="StatusOverride"/>
	/// to determine the step result.
	/// </summary>
	public void RequestStepCompletion()
	{
		StepCompletionRequested = true;
		try
		{
			StepCompletionCts?.Cancel();
		}
		catch (ObjectDisposedException)
		{
			// CTS may already be disposed if the agent completed naturally
		}
	}

	/// <summary>
	/// Signals that the entire orchestration should complete immediately.
	/// The orchestration will cancel all pending/running steps and finish.
	/// </summary>
	public void CompleteOrchestration(ExecutionStatus status, string? reason = null)
	{
		OrchestrationCompleteRequested = true;
		OrchestrationCompleteStatus = status;
		OrchestrationCompleteReason = reason;
	}
}
