using Orchestra.Engine;

namespace Orchestra.OpenCode;

/// <summary>
/// Thrown when an OpenCode session reports a fatal <c>session.error</c> (or the turn ends
/// abnormally). Implements <see cref="IAgentSessionFailedException"/> so the engine can
/// categorize the step failure and extract structured <see cref="AgentSessionErrorDetails"/>
/// without depending on this assembly.
/// </summary>
public sealed class OpenCodeSessionFailedException : Exception, IAgentSessionFailedException
{
	public OpenCodeSessionFailedException(string message, AgentSessionErrorDetails? details = null, Exception? innerException = null)
		: base(message, innerException)
	{
		Details = details;
	}

	public AgentSessionErrorDetails? Details { get; }
}

/// <summary>
/// Thrown when the OpenCode server backing a step is unreachable / unhealthy for the rest of
/// the run scope (process died, health probe failed, transport lost). Implements
/// <see cref="IAgentClientUnhealthyException"/> so the engine categorizes the failure as
/// <c>ClientUnhealthy</c> and skips wasteful retries on a dead server.
/// </summary>
public sealed class OpenCodeClientUnhealthyException : Exception, IAgentClientUnhealthyException
{
	public OpenCodeClientUnhealthyException(
		string triggeringSessionId,
		string triggeringFailureReason,
		string? probeDetails = null,
		string? message = null,
		Exception? innerException = null)
		: base(message ?? $"OpenCode server is unhealthy: {triggeringFailureReason}", innerException)
	{
		TriggeringSessionId = triggeringSessionId;
		TriggeringFailureReason = triggeringFailureReason;
		ProbeDetails = probeDetails;
	}

	public string TriggeringSessionId { get; }
	public string TriggeringFailureReason { get; }
	public string? ProbeDetails { get; }
}
