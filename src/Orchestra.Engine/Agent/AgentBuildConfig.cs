namespace Orchestra.Engine;

/// <summary>
/// Immutable configuration for a single agent build request.
/// Thread-safe — construct one per call site to avoid shared mutable state.
/// </summary>
public sealed record AgentBuildConfig
{
	public required string Model { get; init; }
	public string? SystemPrompt { get; init; }
	public Mcp[] Mcps { get; init; } = [];
	public Subagent[] Subagents { get; init; } = [];
	public ReasoningLevel? ReasoningLevel { get; init; }
	public SystemPromptMode? SystemPromptMode { get; init; }
	public IOrchestrationReporter Reporter { get; init; } = NullOrchestrationReporter.Instance;
	public IReadOnlyCollection<IEngineTool> EngineTools { get; init; } = [];
	public EngineToolContext? EngineToolCtx { get; init; }
}
