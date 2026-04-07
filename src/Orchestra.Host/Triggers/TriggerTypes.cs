using System.Text.Json.Serialization;
using Orchestra.Engine;

namespace Orchestra.Host.Triggers;

/// <summary>
/// Runtime state for a single registered trigger.
/// </summary>
public class TriggerRegistration
{
	public required string Id { get; init; }
	public required string OrchestrationPath { get; init; }
	public required TriggerConfig Config { get; set; }
	public Dictionary<string, string>? Parameters { get; set; }

	// Runtime state (volatile/interlocked for lock-free thread safety)
	private int _status = (int)TriggerStatus.Idle;
	public TriggerStatus Status
	{
		get => (TriggerStatus)Volatile.Read(ref _status);
		set => Volatile.Write(ref _status, (int)value);
	}
	/// <summary>
	/// Atomically transitions Status from <paramref name="expected"/> to <paramref name="desired"/>.
	/// Returns true if the transition succeeded (i.e., the current value was <paramref name="expected"/>).
	/// </summary>
	public bool TryTransitionStatus(TriggerStatus expected, TriggerStatus desired)
		=> Interlocked.CompareExchange(ref _status, (int)desired, (int)expected) == (int)expected;

	public DateTime? NextFireTime { get; set; }
	public DateTime? LastFireTime { get; set; }
	private int _runCount;
	public int RunCount
	{
		get => Volatile.Read(ref _runCount);
		set => Volatile.Write(ref _runCount, value);
	}
	public int IncrementRunCount() => Interlocked.Increment(ref _runCount);
	public string? LastError { get; set; }
	public volatile string? ActiveExecutionId;
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
/// Status of an orchestration execution in the Host layer.
/// Serializes to PascalCase strings (e.g., "Running", "Cancelling") for backward
/// compatibility with frontend clients and SSE event payloads.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum HostExecutionStatus
{
	/// <summary>Execution is actively running.</summary>
	Running,

	/// <summary>Cancel has been requested; awaiting engine completion.</summary>
	Cancelling,

	/// <summary>Execution completed successfully.</summary>
	Completed,

	/// <summary>Execution was cancelled.</summary>
	Cancelled,

	/// <summary>Execution failed with an error.</summary>
	Failed,
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
	/// Current execution status.
	/// </summary>
	public HostExecutionStatus Status { get; set; } = HostExecutionStatus.Running;

	/// <summary>
	/// Total number of steps in the orchestration.
	/// </summary>
	public int TotalSteps { get; set; }

	/// <summary>
	/// Number of steps that have completed (atomically incremented from reporter callbacks).
	/// </summary>
	private int _completedSteps;
	public int CompletedSteps
	{
		get => Volatile.Read(ref _completedSteps);
		set => Volatile.Write(ref _completedSteps, value);
	}
	public int IncrementCompletedSteps() => Interlocked.Increment(ref _completedSteps);

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

	/// <summary>
	/// Execution nesting metadata for parent-child tracking.
	/// Null for legacy executions that predate nesting support.
	/// </summary>
	public McpServer.ExecutionMetadata? NestingMetadata { get; init; }
}
