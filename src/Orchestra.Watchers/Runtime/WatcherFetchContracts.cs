using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Orchestra.Watchers.Models;

namespace Orchestra.Watchers.Runtime;

/// <summary>
/// The v1 stdin payload Orchestra writes to a fetcher (<c>schemas/watchers/v1/fetcher-input.schema.json</c>).
/// </summary>
public sealed class WatcherFetchRequest
{
	private static readonly JsonSerializerOptions s_jsonOptions = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		DefaultIgnoreCondition = JsonIgnoreCondition.Never,
		WriteIndented = false,
	};

	public string ProtocolVersion { get; init; } = WatcherOutputValidator.SupportedProtocolVersion;

	public required string Watcher { get; init; }

	/// <summary>Cursor from the previous successful fetch; <see langword="null"/> on the first run.</summary>
	public JsonNode? Cursor { get; init; }

	/// <summary>Orchestration ids currently subscribed (informational).</summary>
	public string[] Subscribers { get; init; } = [];

	/// <summary>Verbatim copy of the watcher's <c>fetch.config</c>.</summary>
	public JsonObject? Config { get; init; }

	/// <summary>Compact JSON exactly as written to the fetcher's stdin (nulls are explicit).</summary>
	public string ToJson() => JsonSerializer.Serialize(this, s_jsonOptions);

	/// <summary>Indented JSON for reports.</summary>
	public string ToIndentedJson() => JsonSerializer.Serialize(this, new JsonSerializerOptions(s_jsonOptions) { WriteIndented = true });

	/// <summary>Builds the request for one tick of <paramref name="entry"/>.</summary>
	public static WatcherFetchRequest Create(WatcherEntry entry, JsonNode? cursor, IEnumerable<string> subscribers)
	{
		ArgumentNullException.ThrowIfNull(entry);

		return new WatcherFetchRequest
		{
			Watcher = entry.Name,
			Cursor = cursor?.DeepClone(),
			Subscribers = subscribers.ToArray(),
			Config = entry.FetchBase.Config?.DeepClone().AsObject(),
		};
	}
}

/// <summary>
/// The parsed, schema-valid v1 stdout document of a fetcher.
/// </summary>
public sealed class WatcherFetcherOutput
{
	public WatcherFetcherOutput(JsonObject root)
	{
		Root = root;
	}

	/// <summary>The whole document; <c>items.jsonPath</c> is evaluated against it.</summary>
	public JsonObject Root { get; }

	public string? ProtocolVersion => Root["protocolVersion"] is JsonValue v && v.TryGetValue<string>(out var text) ? text : null;

	/// <summary>Cursor to remember; <see langword="null"/> (or JSON null) leaves the stored cursor unchanged.</summary>
	public JsonNode? Cursor => Root["cursor"];

	public JsonObject? Diagnostics => Root["diagnostics"] as JsonObject;

	/// <summary>Items selected by <paramref name="itemsConfig"/> (default <c>$.items[*]</c>).</summary>
	public IReadOnlyList<JsonNode?> SelectItems(WatcherItemsConfig? itemsConfig)
	{
		var path = WatcherItemPath.Parse(itemsConfig?.JsonPath ?? WatcherItemsConfig.DefaultJsonPath);
		return path.Select(Root);
	}
}

/// <summary>
/// Outcome of one fetcher invocation. <see cref="Succeeded"/> means the process exited 0 within the
/// timeout and stdout parsed as JSON; schema validation is a separate step performed by the loop.
/// </summary>
public sealed record WatcherFetchResult(
	int ExitCode,
	string RawStdout,
	string RawStdErr,
	JsonNode? Output,
	string? FailureReason,
	bool TimedOut,
	TimeSpan Elapsed)
{
	public bool Succeeded => FailureReason is null && Output is not null;

	public static WatcherFetchResult Failure(string reason, int exitCode = -1, string stdout = "", string stderr = "", bool timedOut = false, TimeSpan elapsed = default)
		=> new(exitCode, stdout, stderr, Output: null, FailureReason: reason, TimedOut: timedOut, Elapsed: elapsed);
}

/// <summary>
/// Runs a watcher's fetcher once. Implementations never throw for fetcher-side problems; those are
/// reported through <see cref="WatcherFetchResult.FailureReason"/>. Only cancellation propagates.
/// </summary>
public interface IWatcherFetcher
{
	Task<WatcherFetchResult> RunAsync(WatcherEntry entry, WatcherFetchRequest request, CancellationToken cancellationToken);
}

/// <summary>Picks the fetcher implementation for an entry.</summary>
public interface IWatcherFetcherFactory
{
	IWatcherFetcher Create(WatcherEntry entry);
}

/// <summary>Default factory: <see cref="CommandWatcherFetcher"/> for poll-command, <see cref="ScriptWatcherFetcher"/> for poll-script.</summary>
public sealed class WatcherFetcherFactory : IWatcherFetcherFactory
{
	private readonly CommandWatcherFetcher _command = new();
	private readonly ScriptWatcherFetcher _script = new();

	public IWatcherFetcher Create(WatcherEntry entry)
	{
		ArgumentNullException.ThrowIfNull(entry);

		return entry switch
		{
			PollCommandWatcher => _command,
			PollScriptWatcher => _script,
			_ => throw new NotSupportedException($"No fetcher for watcher type '{entry.TypeName}'."),
		};
	}
}
