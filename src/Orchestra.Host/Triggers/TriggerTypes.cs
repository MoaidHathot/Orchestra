using Orchestra.Engine;

namespace Orchestra.Host.Triggers;

/// <summary>
/// Runtime state for a single registered trigger.
/// </summary>
public class TriggerRegistration
{
	public required string Id { get; init; }
	public required string OrchestrationPath { get; init; }
	public string? McpPath { get; init; }
	public required TriggerConfig Config { get; set; }
	public Dictionary<string, string>? Parameters { get; set; }

	// Runtime state
	public TriggerStatus Status { get; set; } = TriggerStatus.Idle;
	public DateTime? NextFireTime { get; set; }
	public DateTime? LastFireTime { get; set; }
	public int RunCount { get; set; }
	public string? LastError { get; set; }
	public string? ActiveExecutionId { get; set; }
	public string? LastExecutionId { get; set; }
	public string? OrchestrationName { get; set; }
	public string? OrchestrationDescription { get; set; }
	public string? OrchestrationVersion { get; set; }

	/// <summary>
	/// Whether this trigger was defined in the JSON file (vs. UI override).
	/// </summary>
	public TriggerSource Source { get; set; } = TriggerSource.User;
}

/// <summary>
/// Source of a trigger definition.
/// </summary>
public enum TriggerSource
{
	/// <summary>Trigger was defined in the orchestration JSON file.</summary>
	Json,

	/// <summary>Trigger was set or overridden by the user.</summary>
	User,
}

/// <summary>
/// Information about an actively running orchestration execution.
/// </summary>
public class ActiveExecutionInfo
{
	public required string ExecutionId { get; init; }
	public required string OrchestrationId { get; init; }
	public required string OrchestrationName { get; init; }
	public required DateTimeOffset StartedAt { get; init; }
	public required string TriggeredBy { get; init; } // "manual", "scheduler", "loop", "webhook"
	public required CancellationTokenSource CancellationTokenSource { get; init; }
	public required IOrchestrationReporter Reporter { get; init; }

	/// <summary>
	/// Parameters passed to the orchestration when it was started.
	/// </summary>
	public Dictionary<string, string>? Parameters { get; init; }

	/// <summary>
	/// Status: "Running", "Cancelling", "Cancelled", "Completed"
	/// </summary>
	public string Status { get; set; } = "Running";

	/// <summary>
	/// Total number of steps in the orchestration.
	/// </summary>
	public int TotalSteps { get; set; }

	/// <summary>
	/// Number of steps that have completed.
	/// </summary>
	public int CompletedSteps { get; set; }

	/// <summary>
	/// Name of the currently executing step.
	/// </summary>
	public string? CurrentStep { get; set; }

	/// <summary>
	/// Callback invoked when a step starts.
	/// </summary>
	public Action<string>? OnStepStarted { get; set; }

	/// <summary>
	/// Callback invoked when a step completes.
	/// </summary>
	public Action<string>? OnStepCompleted { get; set; }
}
