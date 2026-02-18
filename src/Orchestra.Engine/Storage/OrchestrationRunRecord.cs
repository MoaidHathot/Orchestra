namespace Orchestra.Engine;

/// <summary>
/// Detailed record of a complete orchestration run, including all step records.
/// </summary>
public class OrchestrationRunRecord
{
	public required string RunId { get; init; }
	public required string OrchestrationName { get; init; }
	public required DateTimeOffset StartedAt { get; init; }
	public required DateTimeOffset CompletedAt { get; init; }
	public TimeSpan Duration => CompletedAt - StartedAt;
	public required ExecutionStatus Status { get; init; }

	/// <summary>
	/// Parameters provided for this run.
	/// </summary>
	public Dictionary<string, string> Parameters { get; init; } = [];

	/// <summary>
	/// Optional trigger ID that initiated this run (null for manual executions).
	/// </summary>
	public string? TriggerId { get; init; }

	/// <summary>
	/// All step records for this run, keyed by step name.
	/// For looped steps, contains the final iteration's record.
	/// </summary>
	public required Dictionary<string, StepRunRecord> StepRecords { get; init; }

	/// <summary>
	/// All step records including each loop iteration, keyed by "stepName" or "stepName:iteration-N".
	/// </summary>
	public required Dictionary<string, StepRunRecord> AllStepRecords { get; init; }

	/// <summary>
	/// The final output content from the terminal step(s).
	/// </summary>
	public required string FinalContent { get; init; }
}
