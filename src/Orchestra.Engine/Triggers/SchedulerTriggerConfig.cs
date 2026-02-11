namespace Orchestra.Engine;

/// <summary>
/// Triggers orchestration on a time-based schedule.
/// Supports either a cron expression or a simple interval.
/// </summary>
public class SchedulerTriggerConfig : TriggerConfig
{
	/// <summary>
	/// Cron expression for scheduling (e.g., "0 */5 * * *" for every 5 minutes).
	/// Takes precedence over <see cref="IntervalSeconds"/> if both are set.
	/// </summary>
	public string? Cron { get; init; }

	/// <summary>
	/// Simple interval in seconds between runs.
	/// Only used if <see cref="Cron"/> is not set.
	/// </summary>
	public int? IntervalSeconds { get; init; }

	/// <summary>
	/// Maximum number of scheduled runs. Null means unlimited.
	/// </summary>
	public int? MaxRuns { get; init; }
}
