namespace Orchestra.Host.Profiles;

/// <summary>
/// Records a profile activation or deactivation event for history tracking.
/// </summary>
public class ProfileHistoryEntry
{
	/// <summary>
	/// The action that occurred: "activated" or "deactivated".
	/// </summary>
	public required string Action { get; init; }

	/// <summary>
	/// When the action occurred.
	/// </summary>
	public required DateTimeOffset Timestamp { get; init; }

	/// <summary>
	/// What triggered the action: "manual" or "schedule".
	/// </summary>
	public required string Trigger { get; init; }

	/// <summary>
	/// Orchestration IDs that became active as a result of this action.
	/// Only includes orchestrations that were not already active via another profile.
	/// </summary>
	public string[] OrchestrationsActivated { get; init; } = [];

	/// <summary>
	/// Orchestration IDs that became inactive as a result of this action.
	/// Only includes orchestrations that are no longer matched by any active profile.
	/// </summary>
	public string[] OrchestrationsDeactivated { get; init; } = [];
}
