namespace Orchestra.Engine;

/// <summary>
/// Optional metadata supplied when starting an execution as a retry of a previous run.
/// Used to seed lineage fields on the resulting <see cref="OrchestrationRunRecord"/> and,
/// optionally, to override the generated run ID.
/// </summary>
public sealed class RetryMetadata
{
	/// <summary>
	/// The RunId of the original execution that this run is retrying.
	/// </summary>
	public required string RetriedFromRunId { get; init; }

	/// <summary>
	/// Describes the retry mode. Conventional values:
	/// <list type="bullet">
	///   <item><description><c>"failed"</c> — re-run only failed/skipped/cancelled steps (succeeded steps are restored).</description></item>
	///   <item><description><c>"all"</c> — re-run every step from scratch.</description></item>
	///   <item><description><c>"from-step:&lt;stepName&gt;"</c> — re-run the named step and all of its downstream dependents.</description></item>
	/// </list>
	/// </summary>
	public required string RetryMode { get; init; }

	/// <summary>
	/// Optional override for the new run's <see cref="OrchestrationRunRecord.RunId"/>.
	/// When null, the executor generates a fresh ID.
	/// </summary>
	public string? OverrideRunId { get; init; }

	/// <summary>
	/// Value to set on <see cref="OrchestrationRunRecord.TriggeredBy"/> for the retried run.
	/// Defaults to <c>"retry"</c>.
	/// </summary>
	public string TriggeredBy { get; init; } = "retry";
}
