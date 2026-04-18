namespace Orchestra.Engine;

public class AvailableModelInfo
{
	public required string Id { get; init; }
	public string? Name { get; init; }
	public double? BillingMultiplier { get; init; }
	public string[]? ReasoningEfforts { get; init; }

	/// <summary>
	/// Whether the model supports vision (image) input.
	/// </summary>
	public bool? SupportsVision { get; init; }
}
