namespace Orchestra.Engine;

public class PromptOrchestrationStep : OrchestrationStep
{
	public required string SystemPrompt { get; init; }
	public required string UserPrompt { get; init; }
	public string? InputHandlerPrompt { get; init; }
	public string? OutputHandlerPrompt { get; init; }
	public required string Model { get; init; }
	public required string[] AllowedMcps { get; init; } = [];
}
