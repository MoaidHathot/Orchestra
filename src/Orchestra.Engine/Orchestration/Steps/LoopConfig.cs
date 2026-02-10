namespace Orchestra.Engine;

/// <summary>
/// Configuration for a retry/check loop on a step.
/// After the step (checker) runs, the executor checks if its output contains <see cref="ExitPattern"/>.
/// If not, it re-runs the <see cref="Target"/> step with feedback, then re-runs the checker,
/// up to <see cref="MaxIterations"/> times.
/// </summary>
public class LoopConfig
{
	/// <summary>
	/// Name of the step to re-run when the exit condition is not met.
	/// Must be one of this step's dependencies.
	/// </summary>
	public required string Target { get; init; }

	/// <summary>
	/// Maximum number of loop iterations (1-10). After exhausting iterations,
	/// the step succeeds with the last output rather than failing.
	/// </summary>
	public required int MaxIterations { get; init; }

	/// <summary>
	/// String pattern to look for in the checker step's output (case-insensitive).
	/// When found, the loop exits successfully.
	/// </summary>
	public required string ExitPattern { get; init; }
}
