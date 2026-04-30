namespace Orchestra.Engine;

/// <summary>
/// Mode in which a child orchestration is launched.
/// </summary>
public enum ChildLaunchMode
{
	/// <summary>
	/// Caller awaits <see cref="ChildOrchestrationHandle.Completion"/> to block until the
	/// child orchestration reaches a terminal state. The completion task respects the
	/// request's <see cref="ChildLaunchRequest.TimeoutSeconds"/>.
	/// </summary>
	Sync,

	/// <summary>
	/// The child orchestration runs in the background. The launcher returns a handle as
	/// soon as the execution is registered, and <see cref="ChildOrchestrationHandle.Completion"/>
	/// completes only when the background task finishes.
	/// </summary>
	Async,
}
