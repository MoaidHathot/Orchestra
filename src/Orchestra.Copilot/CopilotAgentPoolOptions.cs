namespace Orchestra.Copilot;

/// <summary>
/// Default Copilot provider pool settings used when an orchestration does not
/// request explicit agentPool values.
/// </summary>
public sealed class CopilotAgentPoolOptions
{
	public int DefaultMinInstances { get; set; } = 1;
	public int DefaultMaxInstancesPerRun { get; set; } = 4;
	public int DefaultMaxSessionsPerInstance { get; set; } = 1;
	public int DefaultIdleTimeoutSeconds { get; set; } = 120;
}
