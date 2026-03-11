namespace Orchestra.Engine;

public class PromptOrchestrationStep : OrchestrationStep
{
	public required string SystemPrompt { get; init; }
	public required string UserPrompt { get; init; }
	public string? InputHandlerPrompt { get; init; }
	public string? OutputHandlerPrompt { get; init; }
	public required string Model { get; init; }
	public ReasoningLevel? ReasoningLevel { get; init; }
	public SystemPromptMode? SystemPromptMode { get; init; }
	public Mcp[] Mcps { get; internal set; } = [];

	/// <summary>
	/// Optional loop configuration for retry/check patterns.
	/// When set, after this step runs the executor checks if the output matches
	/// <see cref="LoopConfig.ExitPattern"/>. If not, it re-runs the target step
	/// with feedback and re-checks, up to <see cref="LoopConfig.MaxIterations"/> times.
	/// </summary>
	public LoopConfig? Loop { get; init; }

	/// <summary>
	/// Raw MCP names from JSON, used internally during parsing to resolve to <see cref="Mcps"/>.
	/// </summary>
	internal string[] McpNames { get; init; } = [];

	/// <summary>
	/// Optional list of subagents that the main step orchestrator can delegate to.
	/// When provided, the implementation will use multi-agent orchestration,
	/// allowing the runtime to automatically delegate to subagents based on user intent.
	/// </summary>
	public Subagent[] Subagents { get; init; } = [];
}
