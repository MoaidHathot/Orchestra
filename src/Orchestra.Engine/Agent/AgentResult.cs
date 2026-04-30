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

	/// <summary>
	/// Reasoning tokens spent for chain-of-thought / extended thinking models. SDK 0.3.0.
	/// </summary>
	public double? ReasoningTokens { get; init; }

	/// <summary>
	/// Total nano-AIU (Anthropic / OpenAI billable units) consumed; SDK 0.3.0 surfaces this
	/// alongside cost so the Portal can show actual platform billing units.
	/// </summary>
	public double? TotalNanoAiu { get; init; }

	/// <summary>
	/// Time-to-first-token in milliseconds (latency of the first response chunk). SDK 0.3.0.
	/// </summary>
	public double? TimeToFirstTokenMs { get; init; }

	/// <summary>
	/// Per-account / per-model quota snapshots reported by the SDK with usage events.
	/// Lets the Portal show entitlement vs used vs overage for each plan slot.
	/// Keyed by quota name (e.g. "premium-requests", "claude-sonnet-4.5").
	/// </summary>
	public IReadOnlyDictionary<string, AgentQuotaSnapshot>? QuotaSnapshots { get; init; }
}
