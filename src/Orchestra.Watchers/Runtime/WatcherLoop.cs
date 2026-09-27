using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Orchestra.Engine;
using Orchestra.Watchers.Diagnostics;
using Orchestra.Watchers.Models;
using Orchestra.Watchers.State;

namespace Orchestra.Watchers.Runtime;

/// <summary>Outcome of one tick, mainly for tests and the CLI.</summary>
public sealed record WatcherTickResult(
	bool Succeeded,
	string? FailureReason,
	int ItemCount,
	int Dispatched,
	int DedupHits,
	bool StoppedByPolicy);

/// <summary>
/// The per-watcher poll loop: fetch once per tick, validate, deduplicate per subscriber, hand new
/// items to the dispatcher (bounded parallel launches, never awaiting run completion), persist
/// state atomically, then sleep until the next scheduled tick (pushed out by exponential back-off
/// after failures). Single-threaded; all state objects are owned by the loop.
/// </summary>
public sealed class WatcherLoop
{
	private readonly WatcherEntry _entry;
	private readonly WatcherSubscriptionRegistry _registry;
	private readonly IWatcherStateStore _store;
	private readonly IWatcherFetcher _fetcher;
	private readonly WatcherOutputValidator _validator;
	private readonly IWatcherDispatcher _dispatcher;
	private readonly ILogger _logger;
	private readonly TimeProvider _time;
	private readonly Dictionary<string, SubscriberWatcherState> _subscriberStates = new(StringComparer.Ordinal);
	private readonly Dictionary<string, TrimRateTracker> _trimRates = new(StringComparer.Ordinal);

	private SharedWatcherState? _shared;

	public WatcherLoop(
		WatcherEntry entry,
		WatcherSubscriptionRegistry registry,
		IWatcherStateStore store,
		IWatcherFetcher fetcher,
		WatcherOutputValidator validator,
		IWatcherDispatcher dispatcher,
		ILogger logger,
		TimeProvider? timeProvider = null)
	{
		_entry = entry ?? throw new ArgumentNullException(nameof(entry));
		_registry = registry ?? throw new ArgumentNullException(nameof(registry));
		_store = store ?? throw new ArgumentNullException(nameof(store));
		_fetcher = fetcher ?? throw new ArgumentNullException(nameof(fetcher));
		_validator = validator ?? throw new ArgumentNullException(nameof(validator));
		_dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
		_logger = logger ?? throw new ArgumentNullException(nameof(logger));
		_time = timeProvider ?? TimeProvider.System;
	}

	public string WatcherName => _entry.Name;

	/// <summary><see langword="true"/> once <c>failurePolicy: fail</c> or an unexpected error stopped the loop.</summary>
	public bool IsFaulted { get; private set; }

	/// <summary>Reason the loop stopped, when <see cref="IsFaulted"/>.</summary>
	public string? FaultReason { get; private set; }

	/// <summary>Ticks executed by this loop instance.</summary>
	public long TickCount { get; private set; }

	/// <summary>Shared state as last observed by the loop (for diagnostics/tests).</summary>
	public SharedWatcherState? SharedState => _shared;

	/// <summary>
	/// Runs ticks until cancelled or faulted. Cancellation propagates as
	/// <see cref="OperationCanceledException"/>; any other exception faults the loop and is logged.
	/// </summary>
	public async Task RunAsync(CancellationToken cancellationToken)
	{
		var shared = await EnsureSharedAsync(cancellationToken);
		DateTimeOffset? lastTickStart = null;

		while (!cancellationToken.IsCancellationRequested && !IsFaulted)
		{
			var now = _time.GetUtcNow();
			var next = WatcherScheduleCalculator.NextTickWithBackoff(_entry.Schedule, now, lastTickStart, shared.Stats.ConsecutiveFailures);
			_logger.LogNextTick(_entry.Name, next, shared.Stats.ConsecutiveFailures);

			var delay = next - now;
			if (delay > TimeSpan.Zero)
				await Task.Delay(delay, _time, cancellationToken);

			lastTickStart = _time.GetUtcNow();

			try
			{
				await RunTickAsync(cancellationToken);
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
				throw;
			}
			catch (Exception ex)
			{
				IsFaulted = true;
				FaultReason = ex.Message;
				_logger.LogLoopCrashed(ex, _entry.Name);
			}
		}
	}

	/// <summary>
	/// Executes exactly one tick (fetch, validate, dedup, dispatch, persist). Exposed so tests and
	/// the CLI can drive the loop deterministically without the scheduler.
	/// </summary>
	public async Task<WatcherTickResult> RunTickAsync(CancellationToken cancellationToken)
	{
		var shared = await EnsureSharedAsync(cancellationToken);
		var now = _time.GetUtcNow();
		TickCount++;
		shared.Stats.Ticks++;
		shared.Stats.LastTickAt = now;
		WatcherMetrics.Ticks.Add(1, WatcherMetrics.WatcherTags(_entry.Name));

		var subscribers = _registry.Snapshot(_entry.Name);
		var request = WatcherFetchRequest.Create(_entry, shared.Cursor, subscribers.Select(s => s.OrchestrationId));

		var stopwatch = Stopwatch.StartNew();
		var fetch = await _fetcher.RunAsync(_entry, request, cancellationToken);
		cancellationToken.ThrowIfCancellationRequested();

		if (!fetch.Succeeded)
		{
			if (!string.IsNullOrWhiteSpace(fetch.RawStdErr))
				_logger.LogFetcherStdErr(_entry.Name, Truncate(fetch.RawStdErr, 2000));

			return await FailTickAsync(shared, fetch.ExitCode, fetch.FailureReason ?? "fetch failed.", cancellationToken);
		}

		if (!_validator.TryValidate(fetch.Output, out var errors))
		{
			foreach (var error in errors)
				_logger.LogValidationFailed(_entry.Name, error.JsonPointer, error.Message);

			var summary = string.Join("; ", errors.Select(e => e.ToString()));
			return await FailTickAsync(shared, fetch.ExitCode, $"output failed schema validation: {summary}", cancellationToken);
		}

		var output = new WatcherFetcherOutput(fetch.Output!.AsObject());
		IReadOnlyList<JsonNode?> items;
		try
		{
			items = output.SelectItems(_entry.Items);
		}
		catch (FormatException ex)
		{
			return await FailTickAsync(shared, fetch.ExitCode, $"items.jsonPath is invalid: {ex.Message}", cancellationToken);
		}

		if (output.Diagnostics?["message"] is JsonValue diagMessage && diagMessage.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text))
			_logger.LogFetcherDiagnostics(_entry.Name, text);

		_logger.LogTickFetched(_entry.Name, items.Count, stopwatch.ElapsedMilliseconds);
		WatcherMetrics.ItemsSeen.Add(items.Count, WatcherMetrics.WatcherTags(_entry.Name));

		shared.Stats.ConsecutiveFailures = 0;
		shared.Stats.LastSuccessAt = now;
		WatcherMetrics.SetConsecutiveFailures(_entry.Name, 0);

		var dedup = _entry.EffectiveDedup;
		var keyed = KeyItems(items, dedup);
		var currentKeys = new HashSet<string>(keyed.Select(k => k.Key), StringComparer.Ordinal);

		var totalDispatched = 0;
		var totalDedupHits = 0;
		foreach (var subscription in subscribers)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var (dispatched, hits) = await ProcessSubscriberAsync(subscription, shared, keyed, currentKeys, dedup, now, cancellationToken);
			totalDispatched += dispatched;
			totalDedupHits += hits;
		}

		if (output.Cursor is { } cursor && cursor.GetValueKind() != System.Text.Json.JsonValueKind.Null)
			shared.Cursor = cursor.DeepClone();

		WatcherDedupEngine.AddSeen(shared.SharedSeenKeys, currentKeys, now);
		WatcherDedupEngine.TrimFifo(shared.SharedSeenKeys, dedup.EffectiveFifo, now);

		cancellationToken.ThrowIfCancellationRequested();
		await WriteSharedAsync(shared, cancellationToken);

		return new WatcherTickResult(Succeeded: true, FailureReason: null, items.Count, totalDispatched, totalDedupHits, StoppedByPolicy: false);
	}

	private async Task<(int Dispatched, int DedupHits)> ProcessSubscriberAsync(
		WatcherSubscription subscription,
		SharedWatcherState shared,
		IReadOnlyList<(string Key, JsonNode? Item)> keyed,
		HashSet<string> currentKeys,
		WatcherDedup dedup,
		DateTimeOffset now,
		CancellationToken cancellationToken)
	{
		var state = await GetSubscriberStateAsync(subscription, shared, dedup, cancellationToken);
		var seen = WatcherDedupEngine.SeenSet(state.SeenKeys);

		var newItems = new List<(string Key, JsonNode? Item)>();
		var hits = 0;
		foreach (var (key, item) in keyed)
		{
			if (seen.Contains(key))
				hits++;
			else
				newItems.Add((key, item));
		}

		if (hits > 0)
			WatcherMetrics.DedupHits.Add(hits, WatcherMetrics.SubscriptionTags(_entry.Name, subscription.OrchestrationId));

		var dispatched = 0;
		if (newItems.Count > 0)
		{
			dispatched = await DispatchAsync(subscription, newItems, shared.Cursor, cancellationToken);
			state.Stats.LifetimeDispatched += dispatched;
			state.Stats.LastDispatchAt = now;
			_logger.LogDispatched(_entry.Name, subscription.OrchestrationId, dispatched);
		}

		// Every new key counts as seen even if its launch failed: retrying a launch that failed for a
		// configuration reason every tick would only spam the log. Failures are logged and counted.
		if (dedup.Mode == WatcherDedupMode.Presence)
		{
			WatcherDedupEngine.UpdatePresence(state, currentKeys, dedup.EffectivePresence, now);
		}
		else
		{
			WatcherDedupEngine.AddSeen(state.SeenKeys, newItems.Select(n => n.Key), now);
			var evicted = WatcherDedupEngine.TrimFifo(state.SeenKeys, dedup.EffectiveFifo, now);
			if (evicted > 0)
			{
				state.Stats.Evictions += evicted;
				WatcherMetrics.DedupEvictions.Add(evicted, WatcherMetrics.SubscriptionTags(_entry.Name, subscription.OrchestrationId));
			}

			var tracker = GetTrimRateTracker(subscription.OrchestrationId);
			if (tracker.Record(evicted, dedup.EffectiveFifo.MaxEntries, now, out var perHour))
				_logger.LogHighTrimRate(_entry.Name, subscription.OrchestrationId, perHour, dedup.EffectiveFifo.MaxEntries);
		}

		cancellationToken.ThrowIfCancellationRequested();
		try
		{
			await _store.WriteSubscriberAsync(state, cancellationToken);
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception ex)
		{
			_logger.LogStateWriteFailed(ex, _entry.Name);
		}

		return (dispatched, hits);
	}

	private async Task<int> DispatchAsync(
		WatcherSubscription subscription,
		List<(string Key, JsonNode? Item)> newItems,
		JsonNode? cursorAtFetch,
		CancellationToken cancellationToken)
	{
		using var gate = new SemaphoreSlim(Math.Max(1, subscription.MaxConcurrent));
		var launches = new List<Task<bool>>(newItems.Count);

		foreach (var (key, item) in newItems)
		{
			await gate.WaitAsync(cancellationToken);
			var dispatch = new WatcherDispatch(
				subscription,
				_entry,
				item,
				key,
				WatcherTemplateRenderer.RenderAll(subscription.Parameters, item),
				cursorAtFetch);

			launches.Add(LaunchOneAsync(dispatch, gate, cancellationToken));
		}

		// Wait for the launches themselves (never for run completion).
		var results = await Task.WhenAll(launches);
		return results.Count(static ok => ok);
	}

	private async Task<bool> LaunchOneAsync(WatcherDispatch dispatch, SemaphoreSlim gate, CancellationToken cancellationToken)
	{
		try
		{
			await _dispatcher.DispatchAsync(dispatch, cancellationToken);
			WatcherMetrics.Dispatches.Add(1, WatcherMetrics.SubscriptionTags(_entry.Name, dispatch.Subscription.OrchestrationId));
			return true;
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			return false;
		}
		catch (Exception ex)
		{
			WatcherMetrics.DispatchFailures.Add(1, WatcherMetrics.SubscriptionTags(_entry.Name, dispatch.Subscription.OrchestrationId));
			_logger.LogDispatchFailed(ex, _entry.Name, dispatch.Subscription.OrchestrationId, dispatch.DedupKey);
			return false;
		}
		finally
		{
			gate.Release();
		}
	}

	private async Task<WatcherTickResult> FailTickAsync(SharedWatcherState shared, int exitCode, string reason, CancellationToken cancellationToken)
	{
		shared.Stats.FetchFailures++;
		shared.Stats.ConsecutiveFailures++;
		WatcherMetrics.FetchFailures.Add(1, WatcherMetrics.WatcherTags(_entry.Name));
		WatcherMetrics.SetConsecutiveFailures(_entry.Name, shared.Stats.ConsecutiveFailures);

		var stopped = false;
		switch (_entry.FailurePolicy)
		{
			case WatcherFailurePolicy.Ignore:
				_logger.LogFetchFailedIgnored(_entry.Name, exitCode, reason);
				break;

			case WatcherFailurePolicy.Fail:
				_logger.LogFetchFailedStopping(_entry.Name, exitCode, reason);
				IsFaulted = true;
				FaultReason = reason;
				stopped = true;
				break;

			default:
				_logger.LogFetchFailed(_entry.Name, exitCode, reason);
				break;
		}

		// The cursor is untouched on failure; only statistics change.
		await WriteSharedAsync(shared, cancellationToken);
		return new WatcherTickResult(Succeeded: false, reason, ItemCount: 0, Dispatched: 0, DedupHits: 0, StoppedByPolicy: stopped);
	}

	private async Task<SharedWatcherState> EnsureSharedAsync(CancellationToken cancellationToken)
	{
		if (_shared is not null)
			return _shared;

		try
		{
			_shared = await _store.ReadSharedAsync(_entry.Name, cancellationToken);
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception ex)
		{
			_logger.LogStateReadFailed(_entry.Name, ex.Message);
			_shared = new SharedWatcherState { Watcher = _entry.Name };
		}

		WatcherMetrics.SetConsecutiveFailures(_entry.Name, _shared.Stats.ConsecutiveFailures);
		return _shared;
	}

	private async Task<SubscriberWatcherState> GetSubscriberStateAsync(WatcherSubscription subscription, SharedWatcherState shared, WatcherDedup dedup, CancellationToken cancellationToken)
	{
		if (_subscriberStates.TryGetValue(subscription.OrchestrationId, out var cached))
			return cached;

		SubscriberWatcherState state;
		try
		{
			state = await _store.ReadSubscriberAsync(_entry.Name, subscription.OrchestrationId, cancellationToken);
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception ex)
		{
			_logger.LogStateReadFailed(_entry.Name, ex.Message);
			state = new SubscriberWatcherState { Watcher = _entry.Name, OrchestrationId = subscription.OrchestrationId, IsNew = true };
		}

		if (state.IsNew)
		{
			WatcherDedupEngine.ApplyCatchup(state, subscription.Catchup, shared.SharedSeenKeys, dedup.Mode);
			state.IsNew = false;
		}
		else if (state.Mode != dedup.Mode)
		{
			// The watcher's dedup mode changed since the state was written; presence bookkeeping is
			// meaningless for fifo and vice versa, so start the mode-specific parts fresh.
			state.Mode = dedup.Mode;
			state.AbsenceTickCounts.Clear();
		}

		_subscriberStates[subscription.OrchestrationId] = state;
		return state;
	}

	private async Task WriteSharedAsync(SharedWatcherState shared, CancellationToken cancellationToken)
	{
		try
		{
			await _store.WriteSharedAsync(shared, cancellationToken);
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception ex)
		{
			_logger.LogStateWriteFailed(ex, _entry.Name);
		}
	}

	private TrimRateTracker GetTrimRateTracker(string orchestrationId)
	{
		if (!_trimRates.TryGetValue(orchestrationId, out var tracker))
		{
			tracker = new TrimRateTracker();
			_trimRates[orchestrationId] = tracker;
		}

		return tracker;
	}

	private static List<(string Key, JsonNode? Item)> KeyItems(IReadOnlyList<JsonNode?> items, WatcherDedup dedup)
	{
		// Two items with the same key inside one tick dispatch once (the first wins).
		var keyed = new List<(string Key, JsonNode? Item)>(items.Count);
		var seenThisTick = new HashSet<string>(StringComparer.Ordinal);
		foreach (var item in items)
		{
			var key = WatcherDedupEngine.KeyFor(item, dedup);
			if (seenThisTick.Add(key))
				keyed.Add((key, item));
		}

		return keyed;
	}

	private static string Truncate(string text, int max)
	{
		text = text.Trim();
		return text.Length <= max ? text : text[..max] + "...";
	}
}
