namespace Orchestra.Engine;

public abstract class OrchestrationStep
{
	public required string Name { get; init; }
	public required OrchestrationStepType Type { get; init; }
	public required string[] DependsOn { get; init; }
	public string[] Parameters { get; init; } = [];

	/// <summary>
	/// Whether this step is enabled. When false, the step is skipped immediately
	/// and returns an empty result. Downstream steps that depend on a disabled step
	/// receive an empty string as the dependency output. Defaults to true.
	/// </summary>
	public bool Enabled { get; init; } = true;

	/// <summary>
	/// Optional timeout in seconds for this step. When set, the step will be cancelled
	/// if it takes longer than this duration. If not set, the step runs with no timeout
	/// (only the global cancellation token applies).
	/// </summary>
	public int? TimeoutSeconds { get; init; }

	/// <summary>
	/// Optional retry policy for this step. Overrides the orchestration's
	/// <see cref="Orchestration.DefaultRetryPolicy"/> when set.
	/// When null, the orchestration-level default is used (if any).
	/// </summary>
	public RetryPolicy? Retry { get; init; }
}
