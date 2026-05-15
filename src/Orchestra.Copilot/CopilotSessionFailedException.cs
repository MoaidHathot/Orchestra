using Orchestra.Engine;

namespace Orchestra.Copilot;

/// <summary>
/// Thrown when a Copilot CLI session fails during execution (e.g. JSON-RPC ConnectionLost,
/// fatal SessionErrorEvent, abnormal SessionShutdownEvent with an error reason).
/// This exception MUST propagate up to the orchestration step so the run is marked as
/// Failed with a clear error category instead of silently succeeding with empty content.
///
/// Implements <see cref="IAgentSessionFailedException"/> so the engine's
/// <c>PromptExecutor</c> catch path can extract structured <see cref="Details"/>
/// without taking a hard reference on <c>Orchestra.Copilot</c>.
/// </summary>
public sealed class CopilotSessionFailedException : Exception, IAgentSessionFailedException
{
	/// <summary>
	/// The kind of failure that occurred (error event, abnormal shutdown, etc.).
	/// </summary>
	public CopilotSessionFailureKind Kind { get; }

	/// <summary>
	/// The model that the failed session was running.
	/// </summary>
	public string Model { get; }

	/// <summary>
	/// Optional reason string from the SDK (e.g. SessionShutdownEvent.ErrorReason).
	/// </summary>
	public string? Reason { get; }

	/// <summary>
	/// Structured details extracted from the SDK's <c>SessionErrorData</c> payload
	/// (error category, HTTP status, request id, URL, stack). Null when the failure
	/// did not originate from a <c>session.error</c> event (e.g. abnormal shutdown).
	/// Surfacing these lets the run record and structured logs carry the information
	/// the SDK actually delivered, rather than collapsing everything into the message string.
	/// </summary>
	public AgentSessionErrorDetails? Details { get; }

	public CopilotSessionFailedException(
		CopilotSessionFailureKind kind,
		string model,
		string message,
		string? reason = null,
		AgentSessionErrorDetails? details = null)
		: base(message)
	{
		Kind = kind;
		Model = model;
		Reason = reason;
		Details = details;
	}
}

/// <summary>
/// Categorises why a Copilot CLI session failed.
/// </summary>
public enum CopilotSessionFailureKind
{
	/// <summary>SDK emitted a SessionErrorEvent (fatal session-level error from the CLI).</summary>
	SessionError,

	/// <summary>SDK emitted a SessionShutdownEvent with a non-null ErrorReason (CLI shutting down due to error).</summary>
	AbnormalShutdown,
}
