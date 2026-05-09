namespace Orchestra.Engine;

/// <summary>
/// Behavior when an Approval (or engine-tool) wait reaches its <c>timeoutSeconds</c>
/// without receiving a response.
/// </summary>
public enum ApprovalTimeoutBehavior
{
	/// <summary>
	/// Mark the step as Failed with <see cref="CancellationCauseKind.AwaitingInputTimeout"/>.
	/// This is the default when <c>timeoutSeconds</c> is set without an explicit
	/// <c>onTimeout</c>, matching Orchestra's "no answer ⇒ no progress" convention.
	/// </summary>
	Fail,

	/// <summary>
	/// Treat the timeout as if the user supplied <see cref="ApprovalOrchestrationStep.DefaultResponse"/>.
	/// Requires <see cref="ApprovalOrchestrationStep.DefaultResponse"/> to be non-null;
	/// otherwise validation fails at parse time.
	/// </summary>
	DefaultResponse,

	/// <summary>
	/// Cancel the entire orchestration. Equivalent to the LLM calling
	/// <c>orchestra_complete</c> with a "timeout while awaiting input" reason.
	/// </summary>
	Cancel,
}

/// <summary>
/// A declarative human-in-the-loop gate. When the executor reaches this step it persists
/// a <see cref="PendingInputRecord"/>, transitions to <see cref="ExecutionStatus.AwaitingInput"/>,
/// emits the <c>step.awaitingInput</c> hook event, and waits for the user's response via
/// <see cref="IHumanInputWaiter.WaitAsync"/>. The response becomes the step's content
/// (downstream steps see it via <c>{{stepName.output}}</c>).
/// </summary>
/// <remarks>
/// Approval steps pause across host restarts: the existing checkpoint mechanism
/// preserves the pending wait, and on restart the auto-resume path re-launches the step
/// which re-attaches to the still-persistent <see cref="PendingInputRecord"/>.
/// </remarks>
public class ApprovalOrchestrationStep : OrchestrationStep
{
	/// <summary>
	/// Human-readable prompt presented to the user (and included in notifications/SSE
	/// events). Supports template expressions resolved at execution time.
	/// </summary>
	public required string Prompt { get; init; }

	/// <summary>
	/// Optional list of allowed responses. When non-empty, the response endpoint
	/// validates that the supplied <c>choice</c> matches one of these (case-insensitive).
	/// When empty, any free-form reply is accepted.
	/// </summary>
	public string[] Choices { get; init; } = [];

	/// <summary>
	/// What to do when the per-step / per-orchestration timeout fires before a response
	/// arrives. Default is <see cref="ApprovalTimeoutBehavior.Fail"/>. Only takes effect
	/// when a non-null timeout is configured (Orchestra defaults remain "no timeout").
	/// </summary>
	public ApprovalTimeoutBehavior OnTimeout { get; init; } = ApprovalTimeoutBehavior.Fail;

	/// <summary>
	/// The fallback response value used when <see cref="OnTimeout"/> is
	/// <see cref="ApprovalTimeoutBehavior.DefaultResponse"/>. Applied as the resolved
	/// step content (mirrors a free-form reply). Ignored for other timeout behaviors.
	/// </summary>
	public string? DefaultResponse { get; init; }
}
