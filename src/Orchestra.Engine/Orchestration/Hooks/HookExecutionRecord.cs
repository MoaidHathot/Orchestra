namespace Orchestra.Engine;

public enum HookSource
{
	Orchestration,
	Global,
}

public class HookExecutionRecord
{
	public required string HookName { get; init; }

	public required HookEventType EventType { get; init; }

	public required HookSource Source { get; init; }

	public required ExecutionStatus Status { get; init; }

	public required DateTimeOffset StartedAt { get; init; }

	public required DateTimeOffset CompletedAt { get; init; }

	public TimeSpan Duration => CompletedAt - StartedAt;

	public string? StepName { get; init; }

	public string? ErrorMessage { get; init; }

	public string? Content { get; init; }

	public HookFailurePolicy FailurePolicy { get; init; }

	public HookActionType ActionType { get; init; } = HookActionType.Script;
}
