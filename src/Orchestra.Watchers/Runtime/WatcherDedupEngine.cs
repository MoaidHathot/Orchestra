using System.Text.Json.Nodes;
using Orchestra.Engine;
using Orchestra.Watchers.Models;
using Orchestra.Watchers.State;

namespace Orchestra.Watchers.Runtime;

/// <summary>
/// Pure state transitions for both dedup modes (see the plan's "State management" section).
/// The loop owns the state objects; nothing here touches storage.
/// </summary>
public static class WatcherDedupEngine
{
	/// <summary>Dedup key for an item: the rendered <c>dedup.key</c> template, or the canonical hash when no template is set.</summary>
	public static string KeyFor(JsonNode? item, WatcherDedup dedup)
	{
		ArgumentNullException.ThrowIfNull(dedup);

		return string.IsNullOrEmpty(dedup.Key)
			? WatcherTemplateRenderer.CanonicalHash(item)
			: WatcherTemplateRenderer.Render(dedup.Key, item);
	}

	/// <summary>Set view of a seen-key list.</summary>
	public static HashSet<string> SeenSet(IEnumerable<SeenKeyEntry> entries) => new(entries.Select(e => e.Key), StringComparer.Ordinal);

	/// <summary>
	/// Applies the catchup policy to a brand-new subscriber state: <c>FromNow</c> copies the watcher's
	/// shared seen keys so the existing backlog is skipped; <c>All</c> leaves the seen-set empty.
	/// </summary>
	public static void ApplyCatchup(SubscriberWatcherState state, WatcherCatchupMode catchup, IReadOnlyList<SeenKeyEntry> sharedSeenKeys, WatcherDedupMode mode)
	{
		ArgumentNullException.ThrowIfNull(state);
		ArgumentNullException.ThrowIfNull(sharedSeenKeys);

		state.Mode = mode;
		state.SeenKeys = catchup == WatcherCatchupMode.FromNow ? sharedSeenKeys.ToList() : [];
		state.AbsenceTickCounts.Clear();
	}

	/// <summary>
	/// Inserts <paramref name="keys"/> at the head of <paramref name="seenKeys"/> (most recent first),
	/// skipping keys already present. Within one batch the last key listed ends up first, so a
	/// same-tick trim evicts the items the fetcher listed earliest.
	/// </summary>
	public static void AddSeen(List<SeenKeyEntry> seenKeys, IEnumerable<string> keys, DateTimeOffset now)
	{
		ArgumentNullException.ThrowIfNull(seenKeys);

		var existing = SeenSet(seenKeys);
		var fresh = new List<SeenKeyEntry>();
		foreach (var key in keys)
		{
			if (existing.Add(key))
				fresh.Add(new SeenKeyEntry(key, now));
		}

		if (fresh.Count > 0)
		{
			fresh.Reverse();
			seenKeys.InsertRange(0, fresh);
		}
	}

	/// <summary>
	/// FIFO trim: drops entries beyond <see cref="WatcherDedupFifoOptions.MaxEntries"/> (oldest first;
	/// the list is most-recent-first) and entries older than <see cref="WatcherDedupFifoOptions.TtlDays"/>.
	/// Returns the number of evicted entries.
	/// </summary>
	public static int TrimFifo(List<SeenKeyEntry> seenKeys, WatcherDedupFifoOptions options, DateTimeOffset now)
	{
		ArgumentNullException.ThrowIfNull(seenKeys);
		ArgumentNullException.ThrowIfNull(options);

		var before = seenKeys.Count;
		var cutoff = now - TimeSpan.FromDays(options.TtlDays);

		// Keep the ordering invariant (most recent first) while removing expired entries anywhere.
		seenKeys.RemoveAll(e => e.SeenAt < cutoff);

		if (seenKeys.Count > options.MaxEntries)
			seenKeys.RemoveRange(options.MaxEntries, seenKeys.Count - options.MaxEntries);

		return before - seenKeys.Count;
	}

	/// <summary>
	/// Presence bookkeeping after a tick: keys visible now reset their absence counter (and join the
	/// seen-set); seen keys missing this tick accrue absence and are forgotten once they reach
	/// <see cref="WatcherDedupPresenceOptions.GraceTicks"/>. Returns the number of forgotten keys.
	/// </summary>
	public static int UpdatePresence(SubscriberWatcherState state, IReadOnlySet<string> currentKeys, WatcherDedupPresenceOptions options, DateTimeOffset now)
	{
		ArgumentNullException.ThrowIfNull(state);
		ArgumentNullException.ThrowIfNull(currentKeys);
		ArgumentNullException.ThrowIfNull(options);

		var seen = SeenSet(state.SeenKeys);
		foreach (var key in currentKeys)
		{
			state.AbsenceTickCounts.Remove(key);
			if (!seen.Contains(key))
				state.SeenKeys.Insert(0, new SeenKeyEntry(key, now));
		}

		var forgotten = 0;
		var stillSeen = new List<SeenKeyEntry>(state.SeenKeys.Count);
		foreach (var entry in state.SeenKeys)
		{
			if (currentKeys.Contains(entry.Key))
			{
				stillSeen.Add(entry);
				continue;
			}

			var absent = state.AbsenceTickCounts.TryGetValue(entry.Key, out var count) ? count + 1 : 1;
			if (absent >= options.GraceTicks)
			{
				state.AbsenceTickCounts.Remove(entry.Key);
				forgotten++;
			}
			else
			{
				state.AbsenceTickCounts[entry.Key] = absent;
				stillSeen.Add(entry);
			}
		}

		state.SeenKeys = stillSeen;
		return forgotten;
	}
}

/// <summary>
/// Sliding one-hour window of FIFO evictions per (watcher, subscriber) used for the trim-rate
/// warning. Warns at most once per hour once evictions-per-hour exceed 10% of <c>maxEntries</c>.
/// </summary>
public sealed class TrimRateTracker
{
	/// <summary>Fraction of <c>maxEntries</c> evicted per hour that triggers the warning.</summary>
	public const double WarnFraction = 0.10;

	private static readonly TimeSpan s_window = TimeSpan.FromHours(1);

	private readonly Queue<(DateTimeOffset At, int Count)> _events = new();
	private DateTimeOffset? _lastWarningAt;

	/// <summary>Records <paramref name="evicted"/> evictions at <paramref name="now"/> and reports whether a warning should be emitted.</summary>
	public bool Record(int evicted, int maxEntries, DateTimeOffset now, out double evictionsPerHour)
	{
		if (evicted > 0)
			_events.Enqueue((now, evicted));

		while (_events.Count > 0 && now - _events.Peek().At > s_window)
			_events.Dequeue();

		evictionsPerHour = _events.Sum(e => e.Count);
		if (evictionsPerHour <= maxEntries * WarnFraction)
			return false;

		if (_lastWarningAt is { } last && now - last < s_window)
			return false;

		_lastWarningAt = now;
		return true;
	}
}
