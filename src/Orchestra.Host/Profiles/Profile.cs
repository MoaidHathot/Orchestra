namespace Orchestra.Host.Profiles;

/// <summary>
/// A named collection of orchestration filters that determines which orchestrations
/// are active when this profile is activated. Multiple profiles can be active simultaneously,
/// and an orchestration is active if it matches any active profile (union semantics).
/// </summary>
public class Profile
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
}
