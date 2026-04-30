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
	/// Version of the orchestration at the time of execution.
	/// </summary>
	public string OrchestrationVersion { get; init; } = "1.0.0";

	/// <summary>
	/// What triggered this execution (e.g., "manual", "scheduler", "webhook", "loop").
	/// </summary>
	public string TriggeredBy { get; init; } = "manual";

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
	public required IReadOnlyDictionary<string, StepRunRecord> StepRecords { get; init; }

	/// <summary>
	/// All step records including each loop iteration, keyed by "stepName" or "stepName:iteration-N".
	/// </summary>
	public required IReadOnlyDictionary<string, StepRunRecord> AllStepRecords { get; init; }

	/// <summary>
	/// The final output content from the terminal step(s).
	/// </summary>
	public required string FinalContent { get; init; }

	/// <summary>
	/// When set, indicates the orchestration was completed early by the orchestra_complete tool.
	/// Contains the reason provided by the LLM.
	/// </summary>
	public string? CompletionReason { get; init; }

	/// <summary>
	/// The name of the step that triggered early completion via orchestra_complete.
	/// </summary>
	public string? CompletedByStep { get; init; }

	/// <summary>
	/// When true, indicates the orchestration did not fully complete.
	/// This covers cases where all terminal steps had NoAction/Skipped status,
	/// or the orchestration was completed early via orchestra_complete.
	/// </summary>
	public bool IsIncomplete { get; init; }

	/// <summary>
	/// Runtime context of this run, including resolved variables, accessed env vars,
	/// orchestration metadata, and data directory path.
	/// </summary>
	public RunContext? Context { get; init; }

	/// <summary>
	/// Aggregate token usage across all steps in this run.
	/// </summary>
	public TokenUsage? TotalUsage { get; init; }

	/// <summary>
	/// Lifecycle hook executions that ran during this orchestration run.
	/// </summary>
	public IReadOnlyList<HookExecutionRecord> HookExecutions { get; init; } = [];

	/// <summary>
	/// When this run was started as a retry of another run, this is the source RunId.
	/// </summary>
	public string? RetriedFromRunId { get; init; }

	/// <summary>
	/// Indicates the retry mode for this run when it is a retry.
	/// Values: "failed" (skip succeeded steps), "all" (full re-run), "from-step:&lt;stepName&gt;" (re-run target step + downstream).
	/// </summary>
	public string? RetryMode { get; init; }
}
