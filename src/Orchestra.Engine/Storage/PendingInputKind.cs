namespace Orchestra.Engine;

/// <summary>
/// Identifies which HITL pause path produced a <see cref="PendingInputRecord"/>. The two
/// kinds have different semantics and restart behavior:
/// <list type="bullet">
///   <item><description><see cref="Approval"/> — declarative <c>Approval</c> step.
///     The orchestration step is in <see cref="ExecutionStatus.AwaitingInput"/>;
///     the run survives host restarts via the existing checkpoint/resume mechanism.</description></item>
///   <item><description><see cref="EngineTool"/> — LLM-decided pause via
///     <c>orchestra_request_user_input</c>. The pause is held inside a tool invocation;
///     the agent session is in-memory and cannot be resumed across host restarts.
///     On host restart, orphaned engine-tool records are surfaced as
///     <see cref="CancellationCauseKind.HostShutdownDuringWait"/>.</description></item>
/// </list>
/// </summary>
public enum PendingInputKind
{
	Approval,
	EngineTool,

	/// <summary>
	/// Copilot SDK <c>elicitation.requested</c> routed to a human operator (opt-in
	/// <c>humanInput</c>). Like <see cref="EngineTool"/> the pause is held inside the
	/// in-memory agent session, so orphaned records are cleaned up on host restart.
	/// </summary>
	Elicitation,

	/// <summary>
	/// Copilot SDK <c>exitPlanMode.requested</c> (plan approval) routed to a human
	/// operator (opt-in <c>humanInput</c>). Session-bound and volatile like
	/// <see cref="EngineTool"/>.
	/// </summary>
	ExitPlanMode,

	/// <summary>
	/// Copilot SDK permission request routed to a human operator (permission policy mode
	/// <c>requireHumanApproval</c>). Session-bound and volatile like <see cref="EngineTool"/>.
	/// </summary>
	Permission,
}
