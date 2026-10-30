namespace Orchestra.Engine;

/// <summary>Explicit provider-published work, independent of the orchestration DAG status.</summary>
public sealed record AgentPlanSnapshot
{
	public required string Provider { get; init; }
	public required string SessionId { get; init; }
	/// <summary>Provider-local source, such as sql-todos, todowrite, or plan-document.</summary>
	public required string Source { get; init; }
	public string? ActorId { get; init; }
	public string? ActorName { get; init; }
	/// <summary>Shared coordination state. An absent actor does not imply individual task ownership.</summary>
	public bool Shared { get; init; }
	public string? Title { get; init; }
	public IReadOnlyList<AgentPlanTask> Tasks { get; init; } = [];
	public IReadOnlyList<AgentPlanDependency> Dependencies { get; init; } = [];
	public string? Markdown { get; init; }
	public string? DocumentPath { get; init; }
	/// <summary>A failed refresh; the engine retains the previous contents for this stream.</summary>
	public string? CaptureError { get; init; }
}

public sealed record AgentPlanTask
{
	public string? Id { get; init; }
	public required string Title { get; init; }
	public string? Description { get; init; }
	public string Status { get; init; } = "unknown";
	public string? RawStatus { get; init; }
	public string? Priority { get; init; }
	public string? CreatedAt { get; init; }

	public static string NormalizeStatus(string? value) => value?.Trim().ToLowerInvariant().Replace('-', '_').Replace(' ', '_') switch
	{
		"pending" or "todo" or "open" or "not_started" => "pending",
		"in_progress" or "active" or "running" or "doing" => "in_progress",
		"completed" or "complete" or "done" => "completed",
		"cancelled" or "canceled" => "cancelled",
		"blocked" => "blocked",
		"failed" => "failed",
		_ => "unknown",
	};
}

public sealed record AgentPlanDependency(string TaskId, string DependsOn);

public sealed record PlanRunIdentity(string RunId, string OrchestrationName, string OrchestrationVersion, string TriggeredBy, DateTimeOffset StartedAt);

/// <summary>An immutable observed revision; revisions are sequential within a step execution.</summary>
public sealed record AgentPlanRevision
{
	public int SchemaVersion { get; init; } = 1;
	public required PlanRunIdentity Run { get; init; }
	public required string StepName { get; init; }
	/// <summary>Unique Prompt execution/attempt identity; a provider worker resume retains this identity.</summary>
	public required string StepExecutionId { get; init; }
	public int? LoopIteration { get; init; }
	/// <summary>Stable identity for a provider, session, source, and optional actor.</summary>
	public required string StreamId { get; init; }
	/// <summary>Monotonically increasing within StepExecutionId, across all of its plan streams.</summary>
	public long Revision { get; init; }
	/// <summary>Time the engine observed the snapshot, rather than an inferred provider mutation time.</summary>
	public DateTimeOffset CapturedAt { get; init; }
	public required AgentPlanSnapshot Plan { get; init; }
}
