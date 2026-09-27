using System.Diagnostics;
using System.Diagnostics.Metrics;
using Orchestra.Engine.Diagnostics;

namespace Orchestra.Watchers.Diagnostics;

/// <summary>
/// Watcher instruments, registered on <see cref="OrchestraDiagnostics.Meter"/> so the host's
/// existing <c>AddMeter(OrchestraDiagnostics.SourceName)</c> exports them without extra wiring.
/// Every instrument carries a <c>watcher</c> tag; per-subscription ones add <c>orchestrationId</c>.
/// </summary>
public static class WatcherMetrics
{
	private static readonly Meter s_meter = OrchestraDiagnostics.Meter;

	// Declared before the observable gauges so textual static-initialization order can never
	// hand a callback an uninitialized dictionary.
	private static readonly object s_gaugeLock = new();
	private static readonly Dictionary<string, int> s_consecutiveFailures = new(StringComparer.Ordinal);
	private static readonly Dictionary<string, int> s_subscribers = new(StringComparer.Ordinal);

	public static Counter<long> Ticks { get; } = s_meter.CreateCounter<long>("orchestra.watcher.ticks", unit: "{tick}", description: "Watcher ticks (fetch attempts).");

	public static Counter<long> FetchFailures { get; } = s_meter.CreateCounter<long>("orchestra.watcher.fetch_failures", unit: "{tick}", description: "Watcher ticks whose fetch failed or produced invalid output.");

	public static Counter<long> ItemsSeen { get; } = s_meter.CreateCounter<long>("orchestra.watcher.items_seen", unit: "{item}", description: "Items returned by watcher fetchers before dedup.");

	public static Counter<long> Dispatches { get; } = s_meter.CreateCounter<long>("orchestra.watcher.dispatches", unit: "{run}", description: "Orchestration launches dispatched by watchers.");

	public static Counter<long> DispatchFailures { get; } = s_meter.CreateCounter<long>("orchestra.watcher.dispatch_failures", unit: "{run}", description: "Watcher dispatches whose launch failed.");

	public static Counter<long> DedupHits { get; } = s_meter.CreateCounter<long>("orchestra.watcher.dedup_hits", unit: "{item}", description: "Items filtered by dedup (already seen).");

	public static Counter<long> DedupEvictions { get; } = s_meter.CreateCounter<long>("orchestra.watcher.dedup_evictions", unit: "{key}", description: "Dedup keys evicted by FIFO trimming.");

	/// <summary>
	/// Current consecutive-failure count per watcher, reported as a gauge from the loop's state.
	/// </summary>
	public static ObservableGauge<int> ConsecutiveFailures { get; } = s_meter.CreateObservableGauge(
		"orchestra.watcher.consecutive_failures",
		ObserveConsecutiveFailures,
		unit: "{failure}",
		description: "Consecutive failed ticks per watcher (resets on success).");

	/// <summary>Active subscriptions per watcher.</summary>
	public static ObservableGauge<int> Subscribers { get; } = s_meter.CreateObservableGauge(
		"orchestra.watcher.subscribers",
		ObserveSubscribers,
		unit: "{subscription}",
		description: "Orchestrations currently subscribed per watcher.");

	public static TagList WatcherTags(string watcher) => new() { { "watcher", watcher } };

	public static TagList SubscriptionTags(string watcher, string orchestrationId) => new() { { "watcher", watcher }, { "orchestrationId", orchestrationId } };

	public static void SetConsecutiveFailures(string watcher, int value)
	{
		lock (s_gaugeLock)
		{
			if (value == 0)
				s_consecutiveFailures.Remove(watcher);
			else
				s_consecutiveFailures[watcher] = value;
		}
	}

	public static void SetSubscribers(string watcher, int value)
	{
		lock (s_gaugeLock)
		{
			if (value == 0)
				s_subscribers.Remove(watcher);
			else
				s_subscribers[watcher] = value;
		}
	}

	private static IEnumerable<Measurement<int>> ObserveConsecutiveFailures()
	{
		lock (s_gaugeLock)
		{
			return s_consecutiveFailures.Select(kv => new Measurement<int>(kv.Value, WatcherTags(kv.Key))).ToArray();
		}
	}

	private static IEnumerable<Measurement<int>> ObserveSubscribers()
	{
		lock (s_gaugeLock)
		{
			return s_subscribers.Select(kv => new Measurement<int>(kv.Value, WatcherTags(kv.Key))).ToArray();
		}
	}
}
