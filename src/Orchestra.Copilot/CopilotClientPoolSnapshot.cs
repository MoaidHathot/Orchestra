namespace Orchestra.Copilot;

internal sealed record CopilotClientPoolSnapshot(
	int CliInstances,
	int ActiveSessions);
