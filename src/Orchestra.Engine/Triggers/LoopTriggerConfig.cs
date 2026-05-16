namespace Orchestra.Engine;

/// <summary>
/// Triggers orchestration to automatically re-run when it completes.
/// </summary>
public class LoopTriggerConfig : TriggerConfig
{
	/// <summary>
	/// Delay in seconds before re-running after completion. Defaults to 0.
	/// </summary>
	public int DelaySeconds { get; init; }

	/// <summary>
	/// Maximum number of loop iterations. Null means unlimited.
	/// </summary>
	public int? MaxIterations { get; init; }

	/// <summary>
	/// Whether to continue looping if the orchestration fails.
	/// Defaults to false (stop on failure).
	/// </summary>
	public bool ContinueOnFailure { get; init; }

	/// <summary>
	/// When true, the host re-fires this loop trigger on startup so the chain
	/// continues across host restarts. Defaults to false (the loop stops when
	/// the host stops and must be manually re-fired).
	/// <para>
	/// The first auto-resume fire respects <see cref="DelaySeconds"/> measured
	/// from the most recent persisted run's <c>StartedAt</c>; if the delay has
	/// already elapsed while the host was down, the trigger fires once on the
	/// next tick (exactly-once catch-up, the same semantics applied to interval
	/// schedulers).
	/// </para>
	/// </summary>
	public bool AutoResume { get; init; }
}
