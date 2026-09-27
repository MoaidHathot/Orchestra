using System.Text.Json.Nodes;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orchestra.Engine;
using Orchestra.Watchers.Config;
using Orchestra.Watchers.Diagnostics;
using Orchestra.Watchers.Models;
using Orchestra.Watchers.State;

namespace Orchestra.Watchers.Runtime;

/// <summary>
/// What the host hands the manager when an orchestration with a watcher trigger is registered.
/// </summary>
/// <param name="TriggerId">Trigger registration id (the dispatcher launches through it).</param>
/// <param name="OrchestrationId">Target orchestration id (per-subscriber state key).</param>
/// <param name="Config">The trigger block.</param>
/// <param name="BaseDirectory">Folder of the orchestration file; inline fetch paths resolve against it.</param>
public sealed record WatcherSubscriptionRequest(string TriggerId, string OrchestrationId, WatcherTriggerConfig Config, string? BaseDirectory);

/// <summary>
/// The narrow surface the host's trigger manager uses to keep watcher subscriptions in step with
/// trigger registrations.
/// </summary>
public interface IWatcherSubscriptionSink
{
	/// <summary>
	/// Resolves and validates the trigger's watcher (library reference or inline definition) and
	/// registers the subscription, replacing any previous subscription for the same trigger id.
	/// </summary>
	/// <exception cref="WatcherInitializationException">Unknown reference, both/neither of
	/// <c>watcher</c>/<c>definition</c>, inline name collision, disabled watcher, or invalid inline definition.</exception>
	WatcherSubscription Subscribe(WatcherSubscriptionRequest request);

	/// <summary>Removes the subscription registered under <paramref name="triggerId"/>; no-op when none.</summary>
	bool Unsubscribe(string triggerId);
}

/// <summary>Point-in-time view of one watcher for diagnostics and tests.</summary>
public sealed record WatcherStatus(string Name, string Type, bool IsInline, int SubscriberCount, bool IsRunning, bool IsFaulted, string? FaultReason, long Ticks);

/// <summary>
/// Owns watcher lifecycles: holds the library, resolves trigger subscriptions, starts a
/// <see cref="WatcherLoop"/> when a watcher gains its first subscriber and stops it when the last
/// one leaves. Loops only run after <see cref="StartAsync"/>; subscriptions registered earlier
/// (host initialization runs before hosted services start) are picked up then.
/// </summary>
public sealed class WatcherManager : IHostedService, IWatcherSubscriptionSink, IAsyncDisposable, IDisposable
{
	private readonly WatcherSubscriptionRegistry _registry;
	private readonly IWatcherStateStore _store;
	private readonly IWatcherFetcherFactory _fetchers;
	private readonly WatcherOutputValidator _validator;
	private readonly IWatcherDispatcher _dispatcher;
	private readonly ILoggerFactory _loggerFactory;
	private readonly ILogger<WatcherManager> _logger;
	private readonly TimeProvider _time;

	private readonly object _gate = new();
	private readonly Dictionary<string, LoopHandle> _loops = new(StringComparer.Ordinal);
	private readonly Dictionary<string, Task> _draining = new(StringComparer.Ordinal); // watcher name -> stopping loop task
	private readonly Dictionary<string, WatcherSubscription> _byTrigger = new(StringComparer.Ordinal);
	private readonly Dictionary<string, WatcherEntry> _inlineEntries = new(StringComparer.Ordinal); // watcher name -> entry
	private readonly Dictionary<string, string> _inlineOwners = new(StringComparer.Ordinal); // watcher name -> trigger id
	private bool _started;

	public WatcherManager(
		WatcherSubscriptionRegistry registry,
		IWatcherStateStore store,
		IWatcherFetcherFactory fetchers,
		WatcherOutputValidator validator,
		IWatcherDispatcher dispatcher,
		ILoggerFactory loggerFactory,
		TimeProvider? timeProvider = null)
	{
		_registry = registry ?? throw new ArgumentNullException(nameof(registry));
		_store = store ?? throw new ArgumentNullException(nameof(store));
		_fetchers = fetchers ?? throw new ArgumentNullException(nameof(fetchers));
		_validator = validator ?? throw new ArgumentNullException(nameof(validator));
		_dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
		_loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
		_logger = loggerFactory.CreateLogger<WatcherManager>();
		_time = timeProvider ?? TimeProvider.System;
	}

	/// <summary>The watcher library in effect (<see cref="WatcherLibrary.Empty"/> until loaded).</summary>
	public WatcherLibrary Library { get; private set; } = WatcherLibrary.Empty;

	/// <summary>How long <see cref="StopAsync"/> waits for loops to drain.</summary>
	public TimeSpan ShutdownTimeout { get; set; } = TimeSpan.FromSeconds(15);

	/// <summary>Replaces the library. Running loops keep the entry they started with.</summary>
	public void SetLibrary(WatcherLibrary library)
	{
		ArgumentNullException.ThrowIfNull(library);

		lock (_gate)
		{
			Library = library;
		}

		if (library.SourcePath is not null)
			_logger.LogLibraryLoaded(library.Watchers.Count, library.SourcePath);
	}

	/// <summary>Loads <paramref name="path"/> with <see cref="WatcherConfigLoader"/> and installs it.</summary>
	public void LoadLibrary(string path)
	{
		SetLibrary(WatcherConfigLoader.Load(path));
	}

	public WatcherSubscription Subscribe(WatcherSubscriptionRequest request)
	{
		ArgumentNullException.ThrowIfNull(request);

		var config = request.Config;
		var hasReference = !string.IsNullOrWhiteSpace(config.Watcher);
		var hasDefinition = config.Definition is not null;
		if (hasReference && hasDefinition)
			throw new WatcherInitializationException($"Trigger '{request.TriggerId}': specify either 'watcher' (library reference) or 'definition' (inline), not both.");
		if (!hasReference && !hasDefinition)
			throw new WatcherInitializationException($"Trigger '{request.TriggerId}': a watcher trigger requires 'watcher' (a name from {WatcherConfigLoader.FileName}) or an inline 'definition'.");
		if (config.MaxConcurrent < 1)
			throw new WatcherInitializationException($"Trigger '{request.TriggerId}': maxConcurrent must be a positive integer.");

		lock (_gate)
		{
			// Replacing a subscription for the same trigger: tear the old one down first so a changed
			// watcher name or inline definition does not leak a stale subscription.
			UnsubscribeCore(request.TriggerId);

			WatcherEntry entry;
			var isInline = false;
			if (hasReference)
			{
				entry = ResolveLibraryEntry(config.Watcher!, request.TriggerId);
			}
			else
			{
				entry = ResolveInlineEntry(config.Definition!, request);
				isInline = true;
			}

			if (!entry.Enabled)
				throw new WatcherInitializationException($"Trigger '{request.TriggerId}': watcher '{entry.Name}' is disabled (enabled: false); enable it or point the trigger elsewhere.");

			var subscription = new WatcherSubscription(
				entry.Name,
				request.OrchestrationId,
				config.MaxConcurrent,
				config.Parameters is null ? null : new Dictionary<string, string>(config.Parameters, StringComparer.Ordinal),
				config.Catchup,
				config.InputHandlerPrompt,
				config.InputHandlerModel,
				request.TriggerId);

			var outcome = _registry.Register(subscription);
			_byTrigger[request.TriggerId] = subscription;
			if (isInline)
			{
				_inlineEntries[entry.Name] = entry;
				_inlineOwners[entry.Name] = request.TriggerId;
			}

			WatcherMetrics.SetSubscribers(entry.Name, _registry.Count(entry.Name));
			_logger.LogSubscriptionAdded(entry.Name, request.OrchestrationId, config.Catchup);

			if (_started && (outcome.IsFirstForWatcher || IsFaulted(entry.Name)))
				StartLoopCore(entry);

			return subscription;
		}
	}

	public bool Unsubscribe(string triggerId)
	{
		ArgumentNullException.ThrowIfNull(triggerId);

		lock (_gate)
		{
			return UnsubscribeCore(triggerId);
		}
	}

	/// <summary>Subscriptions currently registered, keyed by trigger id.</summary>
	public IReadOnlyDictionary<string, WatcherSubscription> Subscriptions
	{
		get
		{
			lock (_gate)
			{
				return new Dictionary<string, WatcherSubscription>(_byTrigger, StringComparer.Ordinal);
			}
		}
	}

	/// <summary>Status of every watcher that currently has subscribers.</summary>
	public IReadOnlyList<WatcherStatus> GetStatus()
	{
		lock (_gate)
		{
			var result = new List<WatcherStatus>();
			foreach (var name in _registry.WatcherNames)
			{
				var entry = _inlineEntries.TryGetValue(name, out var inline) ? inline : Library.TryGet(name, out var lib) ? lib : null;
				_loops.TryGetValue(name, out var handle);
				result.Add(new WatcherStatus(
					name,
					entry?.TypeName ?? "unknown",
					_inlineEntries.ContainsKey(name),
					_registry.Count(name),
					handle is { Task.IsCompleted: false },
					handle?.Loop.IsFaulted ?? false,
					handle?.Loop.FaultReason,
					handle?.Loop.TickCount ?? 0));
			}

			return result;
		}
	}

	/// <summary>The loop currently running for <paramref name="watcherName"/>, if any (tests/diagnostics).</summary>
	public WatcherLoop? GetLoop(string watcherName)
	{
		lock (_gate)
		{
			return _loops.TryGetValue(watcherName, out var handle) ? handle.Loop : null;
		}
	}

	public Task StartAsync(CancellationToken cancellationToken)
	{
		lock (_gate)
		{
			_started = true;
			foreach (var name in _registry.WatcherNames)
			{
				var entry = _inlineEntries.TryGetValue(name, out var inline) ? inline : Library.TryGet(name, out var lib) ? lib : null;
				if (entry is not null && !_loops.ContainsKey(name))
					StartLoopCore(entry);
			}
		}

		return Task.CompletedTask;
	}

	public async Task StopAsync(CancellationToken cancellationToken)
	{
		List<LoopHandle> handles;
		lock (_gate)
		{
			_started = false;
			handles = _loops.Values.ToList();
			_loops.Clear();
			_draining.Clear();
		}

		foreach (var handle in handles)
			handle.Cts.Cancel();

		if (handles.Count == 0)
			return;

		var drain = Task.WhenAll(handles.Select(h => h.Task));
		using var timeout = new CancellationTokenSource(ShutdownTimeout);
		using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, cancellationToken);
		try
		{
			await drain.WaitAsync(linked.Token);
		}
		catch (OperationCanceledException)
		{
			// Loops that did not drain in time are abandoned; their next state write is skipped by cancellation.
		}

		foreach (var handle in handles)
			handle.Cts.Dispose();
	}

	public async ValueTask DisposeAsync()
	{
		await StopAsync(CancellationToken.None);
	}

	/// <summary>
	/// Synchronous disposal (containers disposed with <c>Dispose()</c>): cancels every loop without
	/// waiting for it to drain. Prefer <see cref="DisposeAsync"/> / <see cref="StopAsync"/>.
	/// </summary>
	public void Dispose()
	{
		List<LoopHandle> handles;
		lock (_gate)
		{
			_started = false;
			handles = _loops.Values.ToList();
			_loops.Clear();
			_draining.Clear();
		}

		foreach (var handle in handles)
		{
			handle.Cts.Cancel();
			_ = handle.Task.ContinueWith(static (t, state) => ((CancellationTokenSource)state!).Dispose(), handle.Cts, TaskScheduler.Default);
		}
	}

	private bool UnsubscribeCore(string triggerId)
	{
		if (!_byTrigger.Remove(triggerId, out var subscription))
			return false;

		var outcome = _registry.Unregister(subscription.WatcherName, subscription.OrchestrationId);
		WatcherMetrics.SetSubscribers(subscription.WatcherName, _registry.Count(subscription.WatcherName));
		_logger.LogSubscriptionRemoved(subscription.WatcherName, subscription.OrchestrationId);

		if (_inlineOwners.TryGetValue(subscription.WatcherName, out var owner) && owner == triggerId)
		{
			_inlineOwners.Remove(subscription.WatcherName);
			_inlineEntries.Remove(subscription.WatcherName);
		}

		if (outcome.IsLastForWatcher)
			StopLoopCore(subscription.WatcherName);

		return outcome.Removed;
	}

	private WatcherEntry ResolveLibraryEntry(string name, string triggerId)
	{
		if (Library.TryGet(name, out var entry))
			return entry;

		var known = Library.Watchers.Count == 0
			? Library.SourcePath is null
				? $"no {WatcherConfigLoader.FileName} was loaded"
				: $"{Library.SourcePath} declares no watchers"
			: $"known watchers: {string.Join(", ", Library.Watchers.Keys.OrderBy(k => k, StringComparer.Ordinal))}";
		throw new WatcherInitializationException($"Trigger '{triggerId}': unknown watcher '{name}' ({known}).");
	}

	private WatcherEntry ResolveInlineEntry(JsonObject definition, WatcherSubscriptionRequest request)
	{
		WatcherEntry entry;
		try
		{
			entry = WatcherConfigLoader.ParseInlineDefinition(definition, request.BaseDirectory);
		}
		catch (WatcherValidationException ex)
		{
			throw new WatcherInitializationException($"Trigger '{request.TriggerId}': invalid inline watcher definition - {ex.Message}", ex);
		}

		if (Library.TryGet(entry.Name, out _))
		{
			var source = Library.SourcePath ?? WatcherConfigLoader.FileName;
			throw new WatcherInitializationException(
				$"Trigger '{request.TriggerId}': inline watcher '{entry.Name}' collides with the library watcher '{entry.Name}' declared in {source}. Rename the inline definition or reference the library watcher with 'watcher: {entry.Name}'.");
		}

		if (_inlineOwners.TryGetValue(entry.Name, out var owner) && owner != request.TriggerId)
		{
			throw new WatcherInitializationException(
				$"Trigger '{request.TriggerId}': inline watcher '{entry.Name}' collides with the inline watcher of the same name declared by orchestration '{owner}'. Inline names must be unique; move the definition to {WatcherConfigLoader.FileName} to share it.");
		}

		return entry;
	}

	private bool IsFaulted(string watcherName)
		=> _loops.TryGetValue(watcherName, out var handle) && (handle.Loop.IsFaulted || handle.Task.IsCompleted);

	private void StartLoopCore(WatcherEntry entry)
	{
		Task? previous = null;
		if (_loops.Remove(entry.Name, out var existing))
		{
			existing.Cts.Cancel();
			previous = existing.Task;
		}
		else if (_draining.Remove(entry.Name, out var draining))
		{
			previous = draining;
		}

		var loop = new WatcherLoop(
			entry,
			_registry,
			_store,
			_fetchers.Create(entry),
			_validator,
			_dispatcher,
			_loggerFactory.CreateLogger($"Orchestra.Watchers.Loop.{entry.Name}"),
			_time);

		var cts = new CancellationTokenSource();
		var task = RunLoopAsync(loop, previous, cts.Token);
		_loops[entry.Name] = new LoopHandle(loop, cts, task);
		_logger.LogWatcherStarted(entry.Name);
	}

	private async Task RunLoopAsync(WatcherLoop loop, Task? previous, CancellationToken cancellationToken)
	{
		if (previous is not null)
		{
			// A restarted watcher waits for its predecessor to finish its in-flight tick so two loops
			// never do read-modify-write on the same state files at once.
			try
			{
				await previous;
			}
			catch (Exception)
			{
			}
		}

		await Task.Yield();

		try
		{
			await loop.RunAsync(cancellationToken);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
		}
	}

	private void StopLoopCore(string watcherName)
	{
		if (!_loops.Remove(watcherName, out var handle))
			return;

		handle.Cts.Cancel();
		_logger.LogWatcherStopped(watcherName);
		// Remember the draining task so a quick re-subscribe chains behind it instead of racing it
		// on the state files.
		_draining[watcherName] = handle.Task.ContinueWith(static (t, state) => ((CancellationTokenSource)state!).Dispose(), handle.Cts, TaskScheduler.Default);
	}

	private sealed record LoopHandle(WatcherLoop Loop, CancellationTokenSource Cts, Task Task);
}
