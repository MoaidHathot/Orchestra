namespace Orchestra.Copilot;

using Orchestra.Engine;

/// <summary>
/// Thrown when a sibling session on the same Copilot CLI client is faulted because
/// the underlying client became unhealthy after another session failed. This indicates
/// a CLI-process-level fault (e.g. CLI hung, JSON-RPC stalled, model API outage that
/// took down the session pipe) rather than a per-session error.
///
/// Fast-failing siblings on an unhealthy client prevents the orchestration from sitting
/// silently for the per-step timeout when the CLI can no longer respond.
/// </summary>
public sealed class CopilotClientUnhealthyException : Exception, IAgentClientUnhealthyException
{
	/// <summary>
	/// The session id of the OTHER session that failed first and triggered the health probe.
	/// </summary>
	public string TriggeringSessionId { get; }

	/// <summary>
	/// The original failure reason from the triggering session.
	/// </summary>
	public string TriggeringFailureReason { get; }

	/// <summary>
	/// Optional details from the health probe (e.g. ping exception message, client state).
	/// </summary>
	public string? ProbeDetails { get; }

	public CopilotClientUnhealthyException(
		string triggeringSessionId,
		string triggeringFailureReason,
		string? probeDetails,
		string message)
		: base(message)
	{
		TriggeringSessionId = triggeringSessionId;
		TriggeringFailureReason = triggeringFailureReason;
		ProbeDetails = probeDetails;
	}
}
