namespace Orchestra.Engine;

/// <summary>
/// Identifies the code path that issued a <see cref="ChildLaunchRequest"/>. The launcher uses
/// this to interpret what it means when the caller-supplied <see cref="CancellationToken"/>
/// fires, so the child's persisted <see cref="CancellationDetails"/> names the real cause
/// instead of guessing from the free-form <see cref="ChildLaunchRequest.TriggeredBy"/> label.
/// </summary>
public enum ChildLaunchOrigin
{
	/// <summary>
	/// Not specified by the caller. External-token cancellation is recorded as a generic
	/// <see cref="CancellationCauseKind.External"/> cause.
	/// </summary>
	Unspecified = 0,

	/// <summary>
	/// Launched in-process by an <c>Orchestration</c> step of a parent run. The caller's token
	/// is the parent step's execution token, so its firing means the parent step (or the parent
	/// run) was cancelled or timed out — never an MCP transport event.
	/// </summary>
	OrchestrationStep,

	/// <summary>
	/// Launched by the data-plane MCP <c>invoke_orchestration</c> tool. The caller's token is the
	/// MCP request's cancellation token, so its firing means the upstream MCP client closed the
	/// request (recorded as <see cref="CancellationCauseKind.McpRequestAborted"/>).
	/// </summary>
	McpTool,
}
