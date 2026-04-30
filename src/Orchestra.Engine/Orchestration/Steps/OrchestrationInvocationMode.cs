namespace Orchestra.Engine;

/// <summary>
/// Mode in which an <see cref="OrchestrationInvocationStep"/> launches its child orchestration.
/// </summary>
public enum OrchestrationInvocationMode
{
	/// <summary>
	/// Step blocks until the child reaches a terminal state. Step output is the child's final
	/// content. Step status reflects the child's status (Succeeded → Succeeded; Cancelled →
	/// Failed; Failed → Failed). The default mode.
	/// </summary>
	Sync,

	/// <summary>
	/// Step dispatches the child and returns immediately. Step output is a JSON object
	/// containing <c>executionId</c>, <c>orchestrationName</c>, <c>status: "dispatched"</c>,
	/// and <c>startedAt</c>. The child runs in the background; cancelling the parent does not
	/// cancel the child.
	/// </summary>
	Async,
}
