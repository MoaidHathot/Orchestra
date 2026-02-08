namespace Orchestra.Engine;

public class ModelMismatchInfo
{
	public required string ConfiguredModel { get; init; }
	public required string ActualModel { get; init; }
	public string? SystemPromptMode { get; init; }
	public string? ReasoningLevel { get; init; }
	public string? SystemPromptPreview { get; init; }
	public string[]? McpServers { get; init; }
	public IReadOnlyList<AvailableModelInfo>? AvailableModels { get; init; }
}
