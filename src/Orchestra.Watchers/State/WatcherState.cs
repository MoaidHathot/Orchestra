using System.Text.Json.Nodes;
using Orchestra.Watchers.Models;

namespace Orchestra.Watchers.State;

/// <summary>Current on-disk format version for both state file kinds.</summary>
public static class WatcherStateVersion
{
	public const int Current = 1;
}

/// <summary>One remembered dedup key.</summary>
public sealed record SeenKeyEntry(string Key, DateTimeOffset SeenAt);

/// <summary>
/// Per-watcher state shared by every subscriber: the source cursor, the keys the watcher has ever
/// seen (used to seed <c>catchup: from-now</c> subscribers) and fetch statistics.
/// </summary>
public sealed class SharedWatcherState
{
	public int Version { get; set; } = WatcherStateVersion.Current;

	public string Watcher { get; set; } = string.Empty;

	/// <summary>Opaque cursor returned by the last successful fetch; <see langword="null"/> before the first.</summary>
	public JsonNode? Cursor { get; set; }

	/// <summary>Most-recent-first list of keys seen by the watcher, trimmed with FIFO rules.</summary>
	public List<SeenKeyEntry> SharedSeenKeys { get; set; } = [];

	public SharedWatcherStats Stats { get; set; } = new();
}

/// <summary>
/// Per-(watcher, orchestration) state: the subscriber's own seen-set, presence bookkeeping and
/// dispatch statistics.
/// </summary>
public sealed class SubscriberWatcherState
{
	public int Version { get; set; } = WatcherStateVersion.Current;

	public string Watcher { get; set; } = string.Empty;

	public string OrchestrationId { get; set; } = string.Empty;

	public WatcherDedupMode Mode { get; set; }

	/// <summary>Most-recent-first list of keys this subscriber has dispatched (fifo) or currently sees (presence).</summary>
	public List<SeenKeyEntry> SeenKeys { get; set; } = [];

	/// <summary>Presence mode: consecutive ticks each seen key has been missing from the source.</summary>
	public Dictionary<string, int> AbsenceTickCounts { get; set; } = new(StringComparer.Ordinal);

	public SubscriberWatcherStats Stats { get; set; } = new();

	/// <summary>
	/// <see langword="true"/> when this object was created by the store because no file existed
	/// yet - the loop applies the catchup policy exactly once, on first sight. Not persisted.
	/// </summary>
	[System.Text.Json.Serialization.JsonIgnore]
	public bool IsNew { get; set; }
}

public sealed class SharedWatcherStats
{
	public long Ticks { get; set; }

	public long FetchFailures { get; set; }

	public int ConsecutiveFailures { get; set; }

	public DateTimeOffset? LastTickAt { get; set; }

	public DateTimeOffset? LastSuccessAt { get; set; }
}

public sealed class SubscriberWatcherStats
{
	public long LifetimeDispatched { get; set; }

	public long Evictions { get; set; }

	public DateTimeOffset? LastDispatchAt { get; set; }
}

/// <summary>
/// Persistence for watcher state. Implementations serialize writes per file; callers own the
/// read-modify-write cycle on the in-memory objects.
/// </summary>
public interface IWatcherStateStore
{
	Task<SharedWatcherState> ReadSharedAsync(string watcherName, CancellationToken cancellationToken);

	Task WriteSharedAsync(SharedWatcherState state, CancellationToken cancellationToken);

	Task<SubscriberWatcherState> ReadSubscriberAsync(string watcherName, string orchestrationId, CancellationToken cancellationToken);

	Task WriteSubscriberAsync(SubscriberWatcherState state, CancellationToken cancellationToken);
}
