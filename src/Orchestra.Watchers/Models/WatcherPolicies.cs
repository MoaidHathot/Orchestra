using System.Text.Json.Serialization;

namespace Orchestra.Watchers.Models;

/// <summary>
/// When a watcher polls. Exactly one of <see cref="IntervalSeconds"/> or <see cref="Cron"/> must
/// be set; the config loader enforces this.
/// </summary>
public sealed class WatcherSchedule
{
	/// <summary>Fixed cadence between ticks, measured from the start of the previous tick.</summary>
	public int? IntervalSeconds { get; init; }

	/// <summary>
	/// Cron expression evaluated in UTC. Standard 5-field, or 6-field with a leading seconds field.
	/// Parsed with the same Cronos library the scheduler trigger uses.
	/// </summary>
	public string? Cron { get; init; }

	/// <summary>Human-readable summary for reports and logs.</summary>
	[JsonIgnore]
	public string Description => Cron is not null ? $"cron {Cron}" : $"every {IntervalSeconds}s";
}

/// <summary>
/// Where items live inside the fetcher's output document.
/// </summary>
public sealed class WatcherItemsConfig
{
	/// <summary>Default path when the fetcher follows the standard protocol shape.</summary>
	public const string DefaultJsonPath = "$.items[*]";

	/// <summary>
	/// Simple JSONPath: <c>$</c> root, <c>.name</c> / <c>['name']</c> member access, <c>[n]</c>
	/// index and a trailing <c>[*]</c> to enumerate an array. Filters and recursive descent are
	/// not supported.
	/// </summary>
	public string JsonPath { get; init; } = DefaultJsonPath;
}

/// <summary>
/// How items are recognised across ticks so each subscriber dispatches once per new item.
/// </summary>
public sealed class WatcherDedup
{
	/// <summary>Defaults used when an entry omits the <c>dedup</c> block.</summary>
	public static WatcherDedup Default { get; } = new();

	/// <summary>
	/// Template producing the dedup key (<c>{{item.id}}</c>, <c>{{item}}</c>, ...). When
	/// <see langword="null"/> the key is a SHA-256 prefix of the item's canonical JSON.
	/// </summary>
	public string? Key { get; init; }

	public WatcherDedupMode Mode { get; init; } = WatcherDedupMode.Fifo;

	/// <summary>Options for <see cref="WatcherDedupMode.Fifo"/>. Rejected when the mode is presence.</summary>
	public WatcherDedupFifoOptions? Fifo { get; init; }

	/// <summary>Options for <see cref="WatcherDedupMode.Presence"/>. Rejected when the mode is fifo.</summary>
	public WatcherDedupPresenceOptions? Presence { get; init; }

	[JsonIgnore]
	public WatcherDedupFifoOptions EffectiveFifo => Fifo ?? WatcherDedupFifoOptions.Default;

	[JsonIgnore]
	public WatcherDedupPresenceOptions EffectivePresence => Presence ?? WatcherDedupPresenceOptions.Default;
}

public enum WatcherDedupMode
{
	/// <summary>Remember the most recent keys, bounded by <see cref="WatcherDedupFifoOptions"/>.</summary>
	Fifo,

	/// <summary>Track the keys currently visible at the source; forget a key after it has been absent for a number of ticks.</summary>
	Presence,
}

public sealed class WatcherDedupFifoOptions
{
	public static WatcherDedupFifoOptions Default { get; } = new();

	/// <summary>Maximum remembered keys per subscriber; the oldest are evicted first.</summary>
	public int MaxEntries { get; init; } = 10_000;

	/// <summary>Keys older than this are forgotten.</summary>
	public int TtlDays { get; init; } = 14;
}

public sealed class WatcherDedupPresenceOptions
{
	public static WatcherDedupPresenceOptions Default { get; } = new();

	/// <summary>Consecutive ticks a key may be missing before it is forgotten.</summary>
	public int GraceTicks { get; init; } = 3;
}

/// <summary>
/// What a failed fetch does to the watcher loop.
/// </summary>
public enum WatcherFailurePolicy
{
	/// <summary>Log a warning and keep polling (with back-off).</summary>
	Warn,

	/// <summary>Log at debug level and keep polling (with back-off).</summary>
	Ignore,

	/// <summary>Log an error and stop this watcher's loop; the host stays up.</summary>
	Fail,
}

/// <summary>
/// The parsed contents of <c>orchestra.watchers.json</c>.
/// </summary>
public sealed class WatcherLibrary
{
	/// <summary>An empty library (no file present).</summary>
	public static WatcherLibrary Empty { get; } = new(new Dictionary<string, WatcherEntry>(StringComparer.Ordinal), sourcePath: null);

	public WatcherLibrary(IReadOnlyDictionary<string, WatcherEntry> watchers, string? sourcePath)
	{
		Watchers = watchers;
		SourcePath = sourcePath;
	}

	/// <summary>Entries keyed by name (ordinal, case-sensitive - names are identifiers).</summary>
	public IReadOnlyDictionary<string, WatcherEntry> Watchers { get; }

	/// <summary>The file the library was loaded from, if any.</summary>
	public string? SourcePath { get; }

	public bool TryGet(string name, out WatcherEntry entry) => Watchers.TryGetValue(name, out entry!);
}
