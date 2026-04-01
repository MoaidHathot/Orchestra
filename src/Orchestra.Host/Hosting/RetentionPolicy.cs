namespace Orchestra.Host.Hosting;

/// <summary>
/// Configuration for automatic cleanup of old orchestration run records.
/// Both limits can be active simultaneously — a run is deleted if it violates either rule.
/// Set both to null/zero to keep runs forever (no automatic deletion).
/// </summary>
public class RetentionPolicy
{
	/// <summary>
	/// Maximum number of runs to keep per orchestration.
	/// When exceeded, the oldest runs are deleted first.
	/// Null or 0 means no limit on count.
	/// Default: null (no limit).
	/// </summary>
	public int? MaxRunsPerOrchestration { get; set; }

	/// <summary>
	/// Maximum age of runs in days. Runs older than this are deleted.
	/// Null or 0 means no age limit.
	/// Default: null (no limit).
	/// </summary>
	public int? MaxRunAgeDays { get; set; }

	/// <summary>
	/// Returns true if no retention limits are configured (runs are kept forever).
	/// </summary>
	public bool IsForever => (MaxRunsPerOrchestration is null or 0) && (MaxRunAgeDays is null or 0);
}
