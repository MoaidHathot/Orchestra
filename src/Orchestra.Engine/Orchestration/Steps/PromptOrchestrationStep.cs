namespace Orchestra.Engine;

public class PromptOrchestrationStep : OrchestrationStep
{
	public required string SystemPrompt { get; init; }
	public required string UserPrompt { get; init; }
	public required string InputHandlerPrompt { get; init; }
	public required string OutputHandlerPrompt { get; init; }
	public required string[] AllowedMcps { get; init; }
	public required string Model { get; init; }
	public required Mcp[] Mcps { get; init; } = [];
}
