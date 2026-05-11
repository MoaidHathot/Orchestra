namespace Orchestra.Host.Api;

/// <summary>
/// Stable, enum-style categorisation of an <c>OrchestrationRunRecord.TriggeredBy</c> string.
/// </summary>
/// <remarks>
/// <c>TriggeredBy</c> is persisted as a free-form string ("manual", "scheduler", "loop",
/// "webhook", "mcp", "retry", "resume", or "orchestration:&lt;name&gt;:&lt;id&gt;"). Surfaces that
/// need to filter by origin or render an icon do not want to re-implement the parsing logic;
/// the values here are the single source of truth.
/// </remarks>
public enum RunOriginKind
{
	/// <summary>Origin string is missing, empty, or did not match any known prefix.</summary>
	Unknown,

	/// <summary>Run launched manually by a human via the portal/API.</summary>
	Manual,

	/// <summary>Run launched by a scheduler trigger (cron-style).</summary>
	Scheduler,

	/// <summary>Run launched by a loop trigger.</summary>
	Loop,

	/// <summary>Run launched by an inbound webhook.</summary>
	Webhook,

	/// <summary>Run launched at the top level via the MCP <c>invoke_orchestration</c> tool.</summary>
	Mcp,

	/// <summary>Run launched by another orchestration (nested / child run).</summary>
	Orchestration,

	/// <summary>Run created by retrying a previous run.</summary>
	Retry,

	/// <summary>Run created by resuming from a checkpoint.</summary>
	Resume,
}

/// <summary>
/// Classifies the free-form <see cref="Orchestra.Engine.OrchestrationRunRecord.TriggeredBy"/>
/// string into a <see cref="RunOriginKind"/> and back, so APIs and the portal share a vocabulary.
/// </summary>
public static class RunOriginClassifier
{
	private const string OrchestrationPrefix = "orchestration:";

	/// <summary>
	/// Returns the origin kind for the given <paramref name="triggeredBy"/> value.
	/// Comparison is ordinal/case-insensitive. Unknown or empty values map to
	/// <see cref="RunOriginKind.Unknown"/>.
	/// </summary>
	public static RunOriginKind Classify(string? triggeredBy)
	{
		if (string.IsNullOrWhiteSpace(triggeredBy))
			return RunOriginKind.Unknown;

		// The "orchestration:" prefix carries the parent name + id after the colon, so
		// match by prefix rather than exact equality.
		if (triggeredBy.StartsWith(OrchestrationPrefix, StringComparison.OrdinalIgnoreCase))
			return RunOriginKind.Orchestration;

		return triggeredBy.ToLowerInvariant() switch
		{
			"manual" => RunOriginKind.Manual,
			"scheduler" => RunOriginKind.Scheduler,
			"loop" => RunOriginKind.Loop,
			"webhook" => RunOriginKind.Webhook,
			"mcp" => RunOriginKind.Mcp,
			"retry" => RunOriginKind.Retry,
			"resume" => RunOriginKind.Resume,
			_ => RunOriginKind.Unknown,
		};
	}

	/// <summary>
	/// Lowercase wire token corresponding to a <see cref="RunOriginKind"/>. This is the
	/// value the portal sends in the <c>?origins=</c> query parameter and the value the
	/// backend echoes back as <c>origin</c> on each row.
	/// </summary>
	public static string ToWireValue(RunOriginKind kind) => kind switch
	{
		RunOriginKind.Manual => "manual",
		RunOriginKind.Scheduler => "scheduler",
		RunOriginKind.Loop => "loop",
		RunOriginKind.Webhook => "webhook",
		RunOriginKind.Mcp => "mcp",
		RunOriginKind.Orchestration => "orchestration",
		RunOriginKind.Retry => "retry",
		RunOriginKind.Resume => "resume",
		_ => "unknown",
	};

	/// <summary>
	/// Parses one or more wire tokens (the values produced by <see cref="ToWireValue"/>) into
	/// a <see cref="HashSet{T}"/> of <see cref="RunOriginKind"/>. Unknown tokens are silently ignored.
	/// </summary>
	/// <remarks>
	/// Used by API endpoints that accept a comma-separated <c>?origins=</c> query parameter.
	/// </remarks>
	public static HashSet<RunOriginKind> ParseWireValues(IEnumerable<string> tokens)
	{
		var set = new HashSet<RunOriginKind>();
		foreach (var raw in tokens)
		{
			if (string.IsNullOrWhiteSpace(raw))
				continue;

			var token = raw.Trim().ToLowerInvariant();
			var kind = token switch
			{
				"manual" => RunOriginKind.Manual,
				"scheduler" => RunOriginKind.Scheduler,
				"loop" => RunOriginKind.Loop,
				"webhook" => RunOriginKind.Webhook,
				"mcp" => RunOriginKind.Mcp,
				"orchestration" => RunOriginKind.Orchestration,
				"retry" => RunOriginKind.Retry,
				"resume" => RunOriginKind.Resume,
				"unknown" => RunOriginKind.Unknown,
				_ => (RunOriginKind?)null,
			};

			if (kind is { } k)
				set.Add(k);
		}
		return set;
	}
}
