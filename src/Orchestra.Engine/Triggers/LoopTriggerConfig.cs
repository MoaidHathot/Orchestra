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
}
