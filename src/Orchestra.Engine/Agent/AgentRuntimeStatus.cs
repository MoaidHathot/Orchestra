namespace Orchestra.Engine;

/// <summary>
/// Provider-neutral snapshot of agent runtime resource usage.
/// </summary>
public sealed record AgentRuntimeStatus(
	string Provider,
	int ActivePools,
	int CliInstances,
	int ActiveSessions);
