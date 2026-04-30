namespace Orchestra.Engine;

/// <summary>
/// Handle returned by <see cref="IChildOrchestrationLauncher.LaunchAsync"/> as soon as the
/// child execution has been registered (i.e. has an execution ID and an active reporter).
/// Callers may subscribe to the reporter, observe progress, or await
/// <see cref="Completion"/> to receive the final <see cref="ChildOrchestrationResult"/>.
/// </summary>
/// <remarks>
/// The launcher always drives <see cref="Completion"/> to completion in the background, even
/// when the caller does not await it. This guarantees that cleanup (active-executions
/// removal, terminal SSE events, run record persistence) happens regardless of the caller's
/// behavior.
/// </remarks>
public sealed class ChildOrchestrationHandle
{
	/// <summary>
	/// Execution ID assigned to the child run.
	/// </summary>
	public required string ExecutionId { get; init; }

	/// <summary>
	/// The registry ID of the child orchestration.
	/// </summary>
	public required string OrchestrationId { get; init; }

	/// <summary>
	/// Display name of the child orchestration (from the parsed orchestration file).
	/// </summary>
	public required string OrchestrationName { get; init; }

	/// <summary>
	/// Reporter for this run. Identical instance to <see cref="ChildLaunchRequest.Reporter"/>
	/// when one was supplied by the caller; otherwise a new instance from the registered
	/// <see cref="IOrchestrationReporterFactory"/>.
	/// </summary>
	public required IOrchestrationReporter Reporter { get; init; }

	/// <summary>
	/// Wall-clock time at which the child run was registered.
	/// </summary>
	public required DateTimeOffset StartedAt { get; init; }

	/// <summary>
	/// Task that completes when the child run reaches a terminal state. The task always
	/// completes successfully (it does not throw) — callers inspect the returned
	/// <see cref="ChildOrchestrationResult.Status"/> and <see cref="ChildOrchestrationResult.ErrorMessage"/>
	/// to determine the outcome.
	/// </summary>
	public required Task<ChildOrchestrationResult> Completion { get; init; }
}
