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
	/// SDK 1.0.0 added a structured shutdown payload (<c>SessionShutdownData</c>) that
	/// aggregates per-model usage, total billing units, code-change counters, and the
	/// overall API duration when a session ends. This property carries that summary so
	/// callers don't have to subscribe to the raw event stream just to get end-of-session
	/// roll-ups. <c>null</c> when the session ended without a structured shutdown
	/// (cancellation, error before any model call, or a runtime older than SDK 1.0.0).
	/// </summary>
	public AgentSessionShutdownSummary? FinalUsage { get; init; }

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

/// <summary>
/// End-of-session aggregate captured from SDK 1.0.0's <c>SessionShutdownEvent</c>.
/// Replaces the per-usage <c>QuotaSnapshots</c> / <c>TotalNanoAiu</c> that SDK 0.3.0
/// surfaced on every <c>AssistantUsageEvent</c>: in 1.0.0 they roll up into a single
/// terminal payload, so consumers that want billing totals subscribe here instead of
/// summing across streaming events.
/// </summary>
public sealed record AgentSessionShutdownSummary
{
	/// <summary>
	/// Total billable nano-AIU (Anthropic / OpenAI billing units, 10^-9 units) consumed
	/// across all model calls in the session. Sums up the per-model
	/// <see cref="ModelMetrics"/> entries when populated.
	/// </summary>
	public double? TotalNanoAiu { get; init; }

	/// <summary>
	/// Total conversation tokens at session end (history + prompts + responses, not
	/// counting tool-definition or system tokens which are tracked separately).
	/// </summary>
	public long? ConversationTokens { get; init; }

	/// <summary>
	/// Total tokens occupied by the session's tool definitions at shutdown. Useful
	/// when triaging "why is context full" — tool definitions can grow large with
	/// many MCPs.
	/// </summary>
	public long? ToolDefinitionsTokens { get; init; }

	/// <summary>
	/// Total tokens occupied by the system message at shutdown.
	/// </summary>
	public long? SystemTokens { get; init; }

	/// <summary>
	/// Total tokens currently in the context window at shutdown. The sum of
	/// <see cref="ConversationTokens"/> + <see cref="SystemTokens"/> +
	/// <see cref="ToolDefinitionsTokens"/> approximates this but the SDK reports the
	/// authoritative number here.
	/// </summary>
	public long? CurrentTokens { get; init; }

	/// <summary>
	/// Cumulative wall-clock duration spent in upstream model API calls. Aggregates
	/// across all model calls in the session, including sub-agent calls.
	/// </summary>
	public TimeSpan? TotalApiDuration { get; init; }

	/// <summary>
	/// Code-change counters when the session touched files via the SDK's edit tools.
	/// <c>null</c> when no file edits occurred. SDK 1.0.0 surfaces these in the
	/// shutdown envelope.
	/// </summary>
	public AgentShutdownCodeChanges? CodeChanges { get; init; }

	/// <summary>
	/// Per-model usage breakdown (input/output tokens, reasoning tokens, cost, nano-AIU,
	/// and request counts). Keyed by the model identifier the runtime reports. When the
	/// session ran a single model end-to-end this dictionary has one entry; sub-agent
	/// or auto-mode patterns produce one entry per distinct model.
	/// </summary>
	public IReadOnlyDictionary<string, AgentShutdownModelMetric>? ModelMetrics { get; init; }
}

/// <summary>
/// Code-change aggregate captured at session shutdown when the agent touched files.
/// </summary>
public sealed record AgentShutdownCodeChanges(
	IReadOnlyList<string> FilesModified,
	long LinesAdded,
	long LinesRemoved);

/// <summary>
/// Per-model usage breakdown captured at session shutdown.
/// </summary>
public sealed record AgentShutdownModelMetric
{
	/// <summary>Total billable nano-AIU charged to this model across the session.</summary>
	public double? TotalNanoAiu { get; init; }

	/// <summary>Request count + cost for this model.</summary>
	public AgentShutdownModelMetricRequests? Requests { get; init; }

	/// <summary>Aggregated token counts for this model.</summary>
	public AgentShutdownModelMetricUsage? Usage { get; init; }
}

/// <summary>
/// Per-model request count and aggregated cost as of session shutdown.
/// </summary>
public sealed record AgentShutdownModelMetricRequests(long? Count, double? Cost);

/// <summary>
/// Per-model token usage aggregated across all calls in the session.
/// </summary>
public sealed record AgentShutdownModelMetricUsage(
	long InputTokens,
	long OutputTokens,
	long CacheReadTokens,
	long CacheWriteTokens,
	long? ReasoningTokens);
