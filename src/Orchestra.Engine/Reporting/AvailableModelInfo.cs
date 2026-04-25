namespace Orchestra.Engine;

public class AvailableModelInfo
{
	public required string Id { get; init; }
	public string? Name { get; init; }
	public string? DefaultReasoningEffort { get; init; }
	public double? BillingMultiplier { get; init; }
	public string[]? ReasoningEfforts { get; init; }
	public string? PolicyState { get; init; }
	public string? PolicyTerms { get; init; }
	public bool? SupportsReasoningEffort { get; init; }

	/// <summary>
	/// Whether the model supports vision (image) input.
	/// </summary>
	public bool? SupportsVision { get; init; }
	public int? MaxContextWindowTokens { get; init; }
	public int? MaxPromptTokens { get; init; }
	public string[]? VisionSupportedMediaTypes { get; init; }
	public int? MaxPromptImages { get; init; }
	public int? MaxPromptImageSize { get; init; }
}
