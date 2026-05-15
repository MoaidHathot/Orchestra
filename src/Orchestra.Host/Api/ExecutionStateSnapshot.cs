using System.Text.Json;
using System.Text.Json.Serialization;

namespace Orchestra.Host.Api;

/// <summary>
/// Authoritative per-step state maintained by <see cref="SseReporter"/> in parallel with
/// the circular event log. Snapshots survive replay-buffer eviction, so a Portal/UI
/// attaching to a long-running orchestration always sees correct DAG node colors and
/// per-step details even when the earliest <c>step-completed</c> / <c>step-trace</c>
/// events were dropped by the circular buffer.
/// </summary>
/// <remarks>
/// Field shape mirrors the union of properties the Portal currently reads from
/// individual SSE events (<c>step-started</c>, <c>step-completed</c>, <c>step-trace</c>,
/// <c>step-output</c>, <c>saved-file</c>, <c>audit-log</c>, <c>usage</c>,
/// <c>model-mismatch</c>) so the existing UI code can hydrate from a single payload.
/// </remarks>
public sealed record StepStateSnapshot
{
	public required string StepName { get; init; }

	/// <summary>
	/// Lifecycle status, lower-case strings matching what the Portal already maps in
	/// <c>App.tsx</c>: <c>pending</c>, <c>running</c>, <c>completed</c>, <c>failed</c>,
	/// <c>cancelled</c>, <c>skipped</c>, <c>noaction</c>, <c>completed_early</c>.
	/// </summary>
	public required string Status { get; init; }

	public DateTimeOffset? StartedAt { get; init; }
	public DateTimeOffset? CompletedAt { get; init; }
	public string? Error { get; init; }

	/// <summary>
	/// Final content of the step (from <c>step-output</c>) when available.
	/// Capped at <see cref="SseReporter.MaxSnapshotStepOutputLength"/> to keep the
	/// snapshot payload bounded — clients can still request full content via the
	/// existing step-output replay event.
	/// </summary>
	public string? Output { get; init; }

	/// <summary>
	/// Short content preview (first ~500 chars) from <c>step-completed</c>.
	/// </summary>
	public string? ContentPreview { get; init; }

	/// <summary>
	/// Latest trace payload (from <c>step-trace</c>) serialized as JSON. The reporter
	/// keeps the raw JSON to avoid re-serialization cost on each snapshot request.
	/// </summary>
	[JsonConverter(typeof(RawJsonElementConverter))]
	public JsonElement? Trace { get; init; }

	public IReadOnlyList<string> SavedFiles { get; init; } = [];

	/// <summary>
	/// Audit entries forwarded for this step, in arrival order (each is the raw
	/// JSON payload from <c>audit-log</c>).
	/// </summary>
	public IReadOnlyList<JsonElement> AuditEntries { get; init; } = [];

	public string? RequestedModel { get; init; }
	public string? SelectedModel { get; init; }
	public string? ActualModel { get; init; }

	/// <summary>
	/// Number of currently-running sub-agents (subagent-started minus subagent-completed/failed).
	/// </summary>
	public int ActiveSubagents { get; init; }

	/// <summary>
	/// Number of retry attempts observed (incremented on each <c>step-retry</c> event).
	/// </summary>
	public int RetryCount { get; init; }
}

/// <summary>
/// Authoritative orchestration-level state plus a dictionary of per-step states.
/// Emitted as a single <c>execution-snapshot</c> SSE frame ahead of replay and also
/// served by the REST <c>GET /api/execution/{id}/state</c> endpoint.
/// </summary>
public sealed record ExecutionStateSnapshot
{
	public string? ExecutionId { get; init; }
	public string? OrchestrationId { get; init; }
	public string? OrchestrationName { get; init; }
	public DateTimeOffset? StartedAt { get; init; }

	/// <summary>
	/// Run-level status string (e.g. <c>Running</c>, <c>Cancelling</c>).
	/// </summary>
	public string? Status { get; init; }

	public string? TriggeredBy { get; init; }
	public IReadOnlyDictionary<string, string>? Parameters { get; init; }

	/// <summary>
	/// Raw <c>run-context</c> payload (parameters, variables, env, data directory)
	/// if the engine has emitted one yet.
	/// </summary>
	[JsonConverter(typeof(RawJsonElementConverter))]
	public JsonElement? RunContext { get; init; }

	public IReadOnlyDictionary<string, StepStateSnapshot> Steps { get; init; }
		= new Dictionary<string, StepStateSnapshot>();

	/// <summary>
	/// Highest sequence number written to the reporter at the time the snapshot was
	/// taken. Clients can use this as the <c>Last-Event-Id</c> value when reconnecting
	/// after consuming the snapshot to receive only events emitted afterward.
	/// </summary>
	public long LastEventSequence { get; init; }

	/// <summary>
	/// True if the orchestration has reached a terminal state.
	/// </summary>
	public bool IsCompleted { get; init; }
}

/// <summary>
/// JSON converter that writes a <see cref="JsonElement"/> as raw JSON and reads it back
/// (we keep raw JSON for trace/run-context to avoid re-serializing on every snapshot).
/// </summary>
internal sealed class RawJsonElementConverter : JsonConverter<JsonElement?>
{
	public override JsonElement? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
	{
		if (reader.TokenType == JsonTokenType.Null) return null;
		using var doc = JsonDocument.ParseValue(ref reader);
		return doc.RootElement.Clone();
	}

	public override void Write(Utf8JsonWriter writer, JsonElement? value, JsonSerializerOptions options)
	{
		if (value is null)
		{
			writer.WriteNullValue();
			return;
		}
		value.Value.WriteTo(writer);
	}
}
