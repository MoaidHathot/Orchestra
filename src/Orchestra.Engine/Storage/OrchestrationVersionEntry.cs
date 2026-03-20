namespace Orchestra.Engine;

/// <summary>
/// Represents a single version snapshot of an orchestration definition.
/// Each entry captures the state of the orchestration at a point in time,
/// identified by a SHA-256 content hash.
/// </summary>
public class OrchestrationVersionEntry
{
	/// <summary>
	/// SHA-256 hash of the orchestration JSON content (hex-encoded, lowercase).
	/// Acts as the unique identifier for this specific version of the content.
	/// </summary>
	public required string ContentHash { get; init; }

	/// <summary>
	/// The declared version string from the orchestration JSON (e.g., "1.0.0").
	/// This is the user-specified version, not the computed hash.
	/// </summary>
	public required string DeclaredVersion { get; init; }

	/// <summary>
	/// When this version was first observed / snapshotted.
	/// </summary>
	public required DateTimeOffset Timestamp { get; init; }

	/// <summary>
	/// Name of the orchestration at the time of this snapshot.
	/// </summary>
	public required string OrchestrationName { get; init; }

	/// <summary>
	/// Number of steps in the orchestration at the time of this snapshot.
	/// </summary>
	public int StepCount { get; init; }

	/// <summary>
	/// Optional description of what changed in this version.
	/// Automatically generated when possible (e.g., "Initial version", "Steps changed: +1 -0").
	/// </summary>
	public string? ChangeDescription { get; init; }
}
