namespace Orchestra.Engine;

public class PromptOrchestrationStep : OrchestrationStep
{
	public required string SystemPrompt { get; init; }
	public required string UserPrompt { get; init; }
	public string? InputHandlerPrompt { get; init; }
	public string? OutputHandlerPrompt { get; init; }
	public required string Model { get; init; }
	public Mcp[] AllowedMcps { get; internal set; } = [];

	/// <summary>
	/// Raw MCP names from JSON, used internally during parsing to resolve to <see cref="AllowedMcps"/>.
	/// </summary>
	internal string[] AllowedMcpNames { get; init; } = [];
}
