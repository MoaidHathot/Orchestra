namespace Orchestra.Host.Profiles;

/// <summary>
/// Event raised when the effective active orchestration set changes due to
/// a profile being activated, deactivated, modified, or a scheduled transition.
/// </summary>
public class EffectiveActiveSetChangedEvent
{
	/// <summary>
	/// Orchestration IDs that are newly active (were inactive, now active).
	/// </summary>
	public required string[] ActivatedOrchestrationIds { get; init; }

	/// <summary>
	/// Orchestration IDs that are newly inactive (were active, now inactive).
	/// </summary>
	public required string[] DeactivatedOrchestrationIds { get; init; }

	/// <summary>
	/// The trigger that caused this change.
	/// </summary>
	public required string Trigger { get; init; }
}
