namespace Orchestra.Host.Profiles;

/// <summary>
/// A named collection of orchestration filters that determines which orchestrations
/// are active when this profile is activated. Multiple profiles can be active simultaneously,
/// and an orchestration is active if it matches any active profile (union semantics).
/// </summary>
public record class Profile
{
	/// <summary>
	/// Unique identifier for the profile, derived from the name.
	/// </summary>
	public required string Id { get; init; }

	/// <summary>
	/// Display name for the profile.
	/// </summary>
	public required string Name { get; set; }

	/// <summary>
	/// Optional description of the profile's purpose.
	/// </summary>
	public string? Description { get; set; }

	/// <summary>
	/// Whether this profile is currently active. When active, its matching
	/// orchestrations have their triggers armed.
	/// </summary>
	public bool IsActive { get; set; }

	/// <summary>
	/// When the profile was last activated. Null if never activated.
	/// </summary>
	public DateTimeOffset? ActivatedAt { get; set; }

	/// <summary>
	/// When the profile was last deactivated. Null if never deactivated.
	/// </summary>
	public DateTimeOffset? DeactivatedAt { get; set; }

	/// <summary>
	/// What triggered the current activation: "manual" or "schedule".
	/// Null when the profile is inactive. When set to "manual", the schedule
	/// evaluator will not automatically deactivate this profile -- the user
	/// must deactivate it explicitly.
	/// </summary>
	public string? ActivationTrigger { get; set; }

	/// <summary>
	/// Filter that determines which orchestrations belong to this profile.
	/// </summary>
	public required ProfileFilter Filter { get; set; }

	/// <summary>
	/// Optional time-window schedule for automatic activation/deactivation.
	/// When null, the profile is manually controlled.
	/// </summary>
	public ProfileSchedule? Schedule { get; set; }

	/// <summary>
	/// When the profile was created.
	/// </summary>
	public DateTimeOffset CreatedAt { get; init; }

	/// <summary>
	/// When the profile was last updated.
	/// </summary>
	public DateTimeOffset UpdatedAt { get; set; }

	/// <summary>
	/// The original file path this profile was synced from, if it was loaded from an external
	/// scan directory. Null for profiles created via the API or UI.
	/// </summary>
	public string? SourcePath { get; set; }

	/// <summary>
	/// SHA-256 content hash of the source file at the time of last sync.
	/// Used to detect changes during directory sync and avoid unnecessary overwrites.
	/// Null for profiles not loaded from a scan directory.
	/// </summary>
	public string? ContentHash { get; set; }
}
