namespace Orchestra.Engine;

/// <summary>
/// Runtime metadata for an orchestration execution.
/// Created once per run and available to all steps via template expressions
/// such as <c>{{orchestration.name}}</c>, <c>{{orchestration.runId}}</c>, etc.
/// </summary>
public record OrchestrationInfo(
	string Name,
	string Version,
	string RunId,
	DateTimeOffset StartedAt);
