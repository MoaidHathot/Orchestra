namespace Orchestra.Engine;

/// <summary>
/// Marker interface implemented by agent-implementation exceptions that signal the
/// underlying agent client (e.g. a Copilot CLI process) is permanently unhealthy
/// for the remainder of the run scope. The engine uses this marker to:
/// <list type="bullet">
///   <item>Categorize the step failure as <see cref="Orchestra.Engine.Storage.StepErrorCategory.ClientUnhealthy"/>.</item>
///   <item>Skip remaining retry attempts (retries on a dead client are guaranteed to fail).</item>
/// </list>
/// This avoids a project-reference cycle: <c>Orchestra.Engine</c> does not depend on
/// agent-implementation assemblies (<c>Orchestra.Copilot</c>, etc.); the marker is
/// implemented by the concrete exception types in those assemblies.
/// </summary>
public interface IAgentClientUnhealthyException
{
	/// <summary>The session id of the original failure that triggered the probe.</summary>
	string TriggeringSessionId { get; }

	/// <summary>The original failure reason from the triggering session.</summary>
	string TriggeringFailureReason { get; }

	/// <summary>Optional details from the health probe (state snapshot, ping outcome).</summary>
	string? ProbeDetails { get; }
}
