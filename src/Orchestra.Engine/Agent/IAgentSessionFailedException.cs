namespace Orchestra.Engine;

/// <summary>
/// Marker interface implemented by agent-implementation exceptions that signal an
/// upstream session error (the SDK / CLI reported a fatal <c>session.error</c> or
/// equivalent). Lets the engine extract structured details (<see cref="AgentSessionErrorDetails"/>)
/// from a concrete exception type without forcing <c>Orchestra.Engine</c> to depend
/// on agent-implementation assemblies (<c>Orchestra.Copilot</c>, etc.).
///
/// Concrete implementations:
/// <list type="bullet">
///   <item><c>Orchestra.Copilot.CopilotSessionFailedException</c> — populated from
///   the SDK's <c>SessionErrorData</c> payload (ErrorType / StatusCode / ProviderCallId
///   / Url / Stack). Carries non-null <see cref="Details"/>.</item>
/// </list>
/// </summary>
public interface IAgentSessionFailedException
{
	/// <summary>
	/// Structured details from the underlying session-error payload. May be null when
	/// the failure shape (e.g. an abnormal shutdown without a paired error event) has
	/// no SDK-side details to surface.
	/// </summary>
	AgentSessionErrorDetails? Details { get; }
}
