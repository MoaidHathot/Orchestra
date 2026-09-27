using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Orchestra.Watchers.Models;
using Orchestra.Watchers.Runtime;
using Orchestra.Watchers.State;

namespace Orchestra.Watchers.Tests;

/// <summary>In-process fetcher whose per-tick outputs are scripted by the test.</summary>
internal sealed class ScriptedFetcher : IWatcherFetcher, IWatcherFetcherFactory
{
	private readonly ConcurrentQueue<Func<WatcherFetchRequest, WatcherFetchResult>> _responses = new();
	private Func<WatcherFetchRequest, WatcherFetchResult>? _fallback;

	public List<WatcherFetchRequest> Requests { get; } = [];

	public int Calls => Requests.Count;

	/// <summary>Queues a successful response with the given items and cursor.</summary>
	public ScriptedFetcher Enqueue(params object[] items) => EnqueueWithCursor(cursor: null, items);

	/// <summary>Queues a successful response carrying a cursor. Kept as a distinct name so a lone JsonObject item is never mistaken for a cursor.</summary>
	public ScriptedFetcher EnqueueWithCursor(JsonNode? cursor, params object[] items)
	{
		_responses.Enqueue(_ => Success(cursor, items));
		return this;
	}

	public ScriptedFetcher EnqueueRaw(string stdoutJson, int exitCode = 0)
	{
		_responses.Enqueue(_ => exitCode == 0
			? new WatcherFetchResult(0, stdoutJson, "", JsonNode.Parse(stdoutJson), null, false, TimeSpan.FromMilliseconds(1))
			: WatcherFetchResult.Failure($"exit code {exitCode}.", exitCode, stdoutJson, "boom"));
		return this;
	}

	public ScriptedFetcher EnqueueFailure(string reason = "exit code 1.", int exitCode = 1)
	{
		_responses.Enqueue(_ => WatcherFetchResult.Failure(reason, exitCode, "", "stderr text"));
		return this;
	}

	/// <summary>Response used once the queue is empty (default: empty items).</summary>
	public ScriptedFetcher Fallback(Func<WatcherFetchRequest, WatcherFetchResult> fallback)
	{
		_fallback = fallback;
		return this;
	}

	public Task<WatcherFetchResult> RunAsync(WatcherEntry entry, WatcherFetchRequest request, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		lock (Requests)
		{
			Requests.Add(request);
		}

		if (_responses.TryDequeue(out var next))
			return Task.FromResult(next(request));

		return Task.FromResult(_fallback?.Invoke(request) ?? Success(cursor: null, []));
	}

	public IWatcherFetcher Create(WatcherEntry entry) => this;

	public static WatcherFetchResult Success(JsonNode? cursor, params object[] items)
	{
		var array = new JsonArray();
		foreach (var item in items)
			array.Add(item is JsonNode node ? node.DeepClone() : JsonValue.Create(item));

		var root = new JsonObject
		{
			["protocolVersion"] = "1",
			["items"] = array,
		};
		if (cursor is not null)
			root["cursor"] = cursor.DeepClone();

		var json = root.ToJsonString();
		return new WatcherFetchResult(0, json, "", root, null, false, TimeSpan.FromMilliseconds(1));
	}

	public static JsonObject Item(string id, params (string Key, object? Value)[] extra)
	{
		var obj = new JsonObject { ["id"] = id };
		foreach (var (key, value) in extra)
			obj[key] = value is JsonNode n ? n : JsonValue.Create(value);
		return obj;
	}
}

/// <summary>Records dispatches; can be told to fail or block.</summary>
internal sealed class RecordingDispatcher : IWatcherDispatcher
{
	private readonly ConcurrentQueue<WatcherDispatch> _dispatches = new();
	private int _counter;

	public IReadOnlyList<WatcherDispatch> Dispatches => _dispatches.ToArray();

	public int Count => _dispatches.Count;

	public Func<WatcherDispatch, Exception?>? FailWhen { get; set; }

	/// <summary>When set, every dispatch awaits this before returning (to observe MaxConcurrent).</summary>
	public SemaphoreSlim? Gate { get; set; }

	public int MaxObservedConcurrency { get; private set; }

	private int _inFlight;

	public async Task<string?> DispatchAsync(WatcherDispatch dispatch, CancellationToken cancellationToken)
	{
		var inFlight = Interlocked.Increment(ref _inFlight);
		lock (this)
		{
			MaxObservedConcurrency = Math.Max(MaxObservedConcurrency, inFlight);
		}

		try
		{
			if (Gate is not null)
				await Gate.WaitAsync(cancellationToken);

			if (FailWhen?.Invoke(dispatch) is { } ex)
				throw ex;

			_dispatches.Enqueue(dispatch);
			return $"exec-{Interlocked.Increment(ref _counter)}";
		}
		finally
		{
			Interlocked.Decrement(ref _inFlight);
		}
	}

	public async Task WaitForCountAsync(int count, TimeSpan? timeout = null)
	{
		var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
		while (Count < count)
		{
			if (DateTime.UtcNow > deadline)
				throw new TimeoutException($"Expected {count} dispatches, saw {Count}: [{string.Join(", ", Dispatches.Select(d => d.Subscription.OrchestrationId + ":" + d.DedupKey))}]");
			await Task.Delay(20);
		}
	}
}

/// <summary>In-memory state store that records every write (for cancellation / partial-write assertions).</summary>
internal sealed class InMemoryStateStore : IWatcherStateStore
{
	private readonly Dictionary<string, SharedWatcherState> _shared = new(StringComparer.Ordinal);
	private readonly Dictionary<string, SubscriberWatcherState> _subscribers = new(StringComparer.Ordinal);

	public int SharedWrites { get; private set; }

	public int SubscriberWrites { get; private set; }

	public Task<SharedWatcherState> ReadSharedAsync(string watcherName, CancellationToken cancellationToken)
	{
		lock (_shared)
		{
			return Task.FromResult(_shared.TryGetValue(watcherName, out var s) ? Clone(s) : new SharedWatcherState { Watcher = watcherName });
		}
	}

	public Task WriteSharedAsync(SharedWatcherState state, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		lock (_shared)
		{
			_shared[state.Watcher] = Clone(state);
			SharedWrites++;
		}

		return Task.CompletedTask;
	}

	public Task<SubscriberWatcherState> ReadSubscriberAsync(string watcherName, string orchestrationId, CancellationToken cancellationToken)
	{
		lock (_subscribers)
		{
			return Task.FromResult(_subscribers.TryGetValue(Key(watcherName, orchestrationId), out var s)
				? Clone(s)
				: new SubscriberWatcherState { Watcher = watcherName, OrchestrationId = orchestrationId, IsNew = true });
		}
	}

	public Task WriteSubscriberAsync(SubscriberWatcherState state, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		lock (_subscribers)
		{
			_subscribers[Key(state.Watcher, state.OrchestrationId)] = Clone(state);
			SubscriberWrites++;
		}

		return Task.CompletedTask;
	}

	public SharedWatcherState? PeekShared(string watcherName)
	{
		lock (_shared)
		{
			return _shared.TryGetValue(watcherName, out var s) ? Clone(s) : null;
		}
	}

	public SubscriberWatcherState? PeekSubscriber(string watcherName, string orchestrationId)
	{
		lock (_subscribers)
		{
			return _subscribers.TryGetValue(Key(watcherName, orchestrationId), out var s) ? Clone(s) : null;
		}
	}

	private static string Key(string watcher, string orchestration) => watcher + "|" + orchestration;

	private static SharedWatcherState Clone(SharedWatcherState s) => new()
	{
		Version = s.Version,
		Watcher = s.Watcher,
		Cursor = s.Cursor?.DeepClone(),
		SharedSeenKeys = s.SharedSeenKeys.ToList(),
		Stats = new SharedWatcherStats
		{
			Ticks = s.Stats.Ticks,
			FetchFailures = s.Stats.FetchFailures,
			ConsecutiveFailures = s.Stats.ConsecutiveFailures,
			LastTickAt = s.Stats.LastTickAt,
			LastSuccessAt = s.Stats.LastSuccessAt,
		},
	};

	private static SubscriberWatcherState Clone(SubscriberWatcherState s) => new()
	{
		Version = s.Version,
		Watcher = s.Watcher,
		OrchestrationId = s.OrchestrationId,
		Mode = s.Mode,
		SeenKeys = s.SeenKeys.ToList(),
		AbsenceTickCounts = new Dictionary<string, int>(s.AbsenceTickCounts, StringComparer.Ordinal),
		Stats = new SubscriberWatcherStats
		{
			LifetimeDispatched = s.Stats.LifetimeDispatched,
			Evictions = s.Stats.Evictions,
			LastDispatchAt = s.Stats.LastDispatchAt,
		},
		IsNew = false,
	};
}

/// <summary>Captures log entries so tests can assert on warnings (e.g. the trim-rate warning).</summary>
internal sealed class CapturingLogger : ILogger
{
	public ConcurrentQueue<(LogLevel Level, EventId EventId, string Message, Exception? Exception)> Entries { get; } = new();

	public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

	public bool IsEnabled(LogLevel logLevel) => true;

	public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
		=> Entries.Enqueue((logLevel, eventId, formatter(state, exception), exception));

	public IEnumerable<string> Messages(LogLevel level) => Entries.Where(e => e.Level == level).Select(e => e.Message);
}

internal sealed class CapturingLoggerFactory : ILoggerFactory
{
	public CapturingLogger Logger { get; } = new();

	public void AddProvider(ILoggerProvider provider)
	{
	}

	public ILogger CreateLogger(string categoryName) => Logger;

	public void Dispose()
	{
	}
}

internal static class TestEntries
{
	public static PollCommandWatcher Command(
		string name = "w",
		WatcherDedup? dedup = null,
		WatcherFailurePolicy failurePolicy = WatcherFailurePolicy.Warn,
		int intervalSeconds = 3600,
		WatcherItemsConfig? items = null,
		bool enabled = true) => new()
	{
		Name = name,
		Enabled = enabled,
		Schedule = new WatcherSchedule { IntervalSeconds = intervalSeconds },
		Fetch = new WatcherFetchCommandConfig { Command = "noop" },
		Dedup = dedup ?? new WatcherDedup { Key = "{{item.id}}" },
		FailurePolicy = failurePolicy,
		Items = items,
	};

	public static WatcherSubscription Subscription(string orchestrationId, string watcher = "w", int maxConcurrent = 1, Orchestra.Engine.WatcherCatchupMode catchup = Orchestra.Engine.WatcherCatchupMode.FromNow, Dictionary<string, string>? parameters = null)
		=> new(watcher, orchestrationId, maxConcurrent, parameters ?? new Dictionary<string, string> { ["id"] = "{{item.id}}" }, catchup, null, null, orchestrationId);

	public static string RepoRoot()
	{
		var current = new DirectoryInfo(AppContext.BaseDirectory);
		while (current is not null)
		{
			if (File.Exists(Path.Combine(current.FullName, "OrchestrationEngine.slnx")))
				return current.FullName;
			current = current.Parent;
		}

		throw new InvalidOperationException("tests must run inside the Orchestra repository");
	}

	public static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "fixtures", name);
}
