namespace Orchestra.Engine;

/// <summary>
/// Centralized entry point for launching child orchestrations from inside the host process.
/// Replaces the duplicated invocation logic that previously lived in
/// <c>DataPlaneTools.InvokeOrchestration</c>, <c>TriggerManager.ExecuteOrchestrationCoreAsync</c>
/// and the manual SSE <c>/api/orchestrations/{id}/run</c> endpoint.
/// </summary>
public interface IChildOrchestrationLauncher
{
	/// <summary>
	/// Looks up the orchestration by ID, builds an executor, registers an active execution,
	/// and returns a handle whose <see cref="ChildOrchestrationHandle.Completion"/> task tracks
	/// the run to its terminal state.
	/// </summary>
	/// <remarks>
	/// The returned handle is available before the run actually finishes — even in
	/// <see cref="ChildLaunchMode.Sync"/> mode, callers must await
	/// <see cref="ChildOrchestrationHandle.Completion"/> to observe the final result.
	/// In <see cref="ChildLaunchMode.Async"/> mode, callers can ignore the completion task;
	/// the launcher always drives it to completion in the background and performs cleanup.
	/// </remarks>
	/// <exception cref="ChildOrchestrationLaunchException">
	/// Thrown synchronously for pre-execution failures: orchestration not found, parse error,
	/// or nesting depth exceeded.
	/// </exception>
	Task<ChildOrchestrationHandle> LaunchAsync(
		ChildLaunchRequest request,
		CancellationToken cancellationToken = default);
}
