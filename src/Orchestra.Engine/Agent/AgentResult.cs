namespace Orchestra.Engine;

public class AgentResult
{
	public required string Content { get; init; }

	/// <summary>
	/// The model that actually generated the response (from the SDK's usage event).
	/// May differ from the requested model if the server silently fell back.
	/// </summary>
	public string? ActualModel { get; init; }

	/// <summary>
	/// The model initially selected by the server at session start.
	/// </summary>
	public string? SelectedModel { get; init; }

	/// <summary>
	/// Token usage statistics for the session.
	/// </summary>
	public AgentUsage? Usage { get; init; }

	/// <summary>
	/// Available models reported by the server. Populated when a model mismatch is detected.
	/// </summary>
	public IReadOnlyList<AvailableModelInfo>? AvailableModels { get; init; }

	/// <summary>
	/// SDK-reported metadata for the configured/requested model.
	/// </summary>
	public AvailableModelInfo? RequestedModelInfo { get; init; }

	/// <summary>
	/// SDK-reported metadata for the server-selected model.
	/// </summary>
	public AvailableModelInfo? SelectedModelInfo { get; init; }

	/// <summary>
	/// SDK-reported metadata for the actual model that produced the response.
	/// </summary>
	public AvailableModelInfo? ActualModelInfo { get; init; }
}

public class AgentUsage
{
	public double? InputTokens { get; init; }
	public double? OutputTokens { get; init; }
	public double? CacheReadTokens { get; init; }
	public double? CacheWriteTokens { get; init; }
	public double? Cost { get; init; }
	public double? Duration { get; init; }
}
