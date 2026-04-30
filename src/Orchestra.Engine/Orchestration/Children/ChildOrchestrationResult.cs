namespace Orchestra.Engine;

/// <summary>
/// Terminal result of a child orchestration run.
/// </summary>
public sealed class ChildOrchestrationResult
{
	/// <summary>
	/// Execution ID of the child run.
	/// </summary>
	public required string ExecutionId { get; init; }

	/// <summary>
	/// Registry ID of the child orchestration.
	/// </summary>
	public required string OrchestrationId { get; init; }

	/// <summary>
	/// Display name of the child orchestration.
	/// </summary>
	public required string OrchestrationName { get; init; }

	/// <summary>
	/// Final status of the child run.
	/// </summary>
	public required ExecutionStatus Status { get; init; }

	/// <summary>
	/// Engine-level result of the run when it ran to a terminal state. Null if execution
	/// could not even be started (e.g. uncaught exception inside the run scope).
	/// </summary>
	public OrchestrationResult? OrchestrationResult { get; init; }

	/// <summary>
	/// Concise diagnostic message when <see cref="Status"/> is not <see cref="ExecutionStatus.Succeeded"/>.
	/// </summary>
	public string? ErrorMessage { get; init; }

	/// <summary>
	/// A summary of the run's terminal step outputs, suitable for surfacing as the parent
	/// step's content. Built from terminal step results when an <see cref="OrchestrationResult"/>
	/// is available.
	/// </summary>
	public string? FinalContent { get; init; }

	/// <summary>
	/// Wall-clock time at which the run was registered.
	/// </summary>
	public DateTimeOffset StartedAt { get; init; }

	/// <summary>
	/// Wall-clock time at which the run reached a terminal state.
	/// </summary>
	public DateTimeOffset CompletedAt { get; init; }

	/// <summary>
	/// True when the run was cancelled because the caller-specified <see cref="ChildLaunchRequest.TimeoutSeconds"/>
	/// elapsed (as opposed to a parent-driven cancellation or the orchestration's own timeout).
	/// </summary>
	public bool TimedOut { get; init; }
}
