using System.Text.Json.Nodes;
using Orchestra.Engine;
using Orchestra.Watchers.Models;

namespace Orchestra.Watchers.Runtime;

/// <summary>
/// One orchestration's subscription to a watcher - the runtime projection of a
/// <see cref="WatcherTriggerConfig"/>.
/// </summary>
/// <param name="WatcherName">Library name or inline <c>definition.name</c>.</param>
/// <param name="OrchestrationId">Target orchestration id; also the per-subscriber state key.</param>
/// <param name="MaxConcurrent">Bound on parallel launches per tick.</param>
/// <param name="Parameters">Item-to-parameter templates.</param>
/// <param name="Catchup">Backlog policy applied on the subscriber's first tick.</param>
/// <param name="InputHandlerPrompt">Optional LLM shaping prompt (applied by the host dispatcher).</param>
/// <param name="InputHandlerModel">Model for the input handler prompt.</param>
/// <param name="TriggerId">The trigger registration id the dispatcher launches through.</param>
public sealed record WatcherSubscription(
	string WatcherName,
	string OrchestrationId,
	int MaxConcurrent,
	Dictionary<string, string>? Parameters,
	WatcherCatchupMode Catchup,
	string? InputHandlerPrompt,
	string? InputHandlerModel,
	string TriggerId);

/// <summary>Result of <see cref="WatcherSubscriptionRegistry.Register"/>.</summary>
/// <param name="IsFirstForWatcher">The watcher had no subscribers before this call.</param>
/// <param name="Replaced">A subscription with the same orchestration id was replaced.</param>
public readonly record struct WatcherRegistrationOutcome(bool IsFirstForWatcher, bool Replaced);

/// <summary>Result of <see cref="WatcherSubscriptionRegistry.Unregister"/>.</summary>
/// <param name="Removed">A subscription was actually removed.</param>
/// <param name="IsLastForWatcher">The watcher has no subscribers left.</param>
public readonly record struct WatcherUnregistrationOutcome(bool Removed, bool IsLastForWatcher);

/// <summary>
/// Thread-safe map of watcher name to its current subscriptions (one per orchestration id).
/// Writes take a lock; readers get immutable snapshots.
/// </summary>
public sealed class WatcherSubscriptionRegistry
{
	private readonly object _gate = new();
	private readonly Dictionary<string, Dictionary<string, WatcherSubscription>> _byWatcher = new(StringComparer.Ordinal);

	public WatcherRegistrationOutcome Register(WatcherSubscription subscription)
	{
		ArgumentNullException.ThrowIfNull(subscription);

		lock (_gate)
		{
			if (!_byWatcher.TryGetValue(subscription.WatcherName, out var subs))
			{
				subs = new Dictionary<string, WatcherSubscription>(StringComparer.Ordinal);
				_byWatcher[subscription.WatcherName] = subs;
			}

			var first = subs.Count == 0;
			var replaced = subs.ContainsKey(subscription.OrchestrationId);
			subs[subscription.OrchestrationId] = subscription;
			return new WatcherRegistrationOutcome(first, replaced);
		}
	}

	public WatcherUnregistrationOutcome Unregister(string watcherName, string orchestrationId)
	{
		ArgumentNullException.ThrowIfNull(watcherName);
		ArgumentNullException.ThrowIfNull(orchestrationId);

		lock (_gate)
		{
			if (!_byWatcher.TryGetValue(watcherName, out var subs))
				return new WatcherUnregistrationOutcome(Removed: false, IsLastForWatcher: true);

			var removed = subs.Remove(orchestrationId);
			var last = subs.Count == 0;
			if (last)
				_byWatcher.Remove(watcherName);

			return new WatcherUnregistrationOutcome(removed, last);
		}
	}

	/// <summary>Current subscriptions for a watcher, in registration order; empty when none.</summary>
	public IReadOnlyList<WatcherSubscription> Snapshot(string watcherName)
	{
		lock (_gate)
		{
			return _byWatcher.TryGetValue(watcherName, out var subs) ? subs.Values.ToArray() : [];
		}
	}

	public int Count(string watcherName)
	{
		lock (_gate)
		{
			return _byWatcher.TryGetValue(watcherName, out var subs) ? subs.Count : 0;
		}
	}

	/// <summary>Names of every watcher with at least one subscription.</summary>
	public IReadOnlyList<string> WatcherNames
	{
		get
		{
			lock (_gate)
			{
				return _byWatcher.Keys.ToArray();
			}
		}
	}
}

/// <summary>
/// Everything the dispatcher needs to launch one orchestration run for one item.
/// </summary>
public sealed record WatcherDispatch(
	WatcherSubscription Subscription,
	WatcherEntry Watcher,
	JsonNode? Item,
	string DedupKey,
	Dictionary<string, string> Parameters,
	JsonNode? CursorAtFetch);

/// <summary>
/// Launches the target orchestration for one item. The returned task completes once the run has
/// been <em>launched</em> (with its execution id, or <see langword="null"/> when the launch was
/// skipped, e.g. a paused trigger); it must never wait for the orchestration to finish.
/// </summary>
public interface IWatcherDispatcher
{
	Task<string?> DispatchAsync(WatcherDispatch dispatch, CancellationToken cancellationToken);
}

/// <summary>
/// Default dispatcher: goes straight to <see cref="IChildOrchestrationLauncher"/> in async mode.
/// The host replaces it with a trigger-aware dispatcher so runs show up on the trigger's history
/// and input-handler prompts apply; this one serves standalone use and tests.
/// </summary>
public sealed class LauncherWatcherDispatcher : IWatcherDispatcher
{
	private readonly IChildOrchestrationLauncher _launcher;

	public LauncherWatcherDispatcher(IChildOrchestrationLauncher launcher)
	{
		_launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
	}

	public async Task<string?> DispatchAsync(WatcherDispatch dispatch, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(dispatch);

		var handle = await _launcher.LaunchAsync(new ChildLaunchRequest
		{
			OrchestrationId = dispatch.Subscription.OrchestrationId,
			Parameters = dispatch.Parameters,
			Mode = ChildLaunchMode.Async,
			TriggeredBy = WatcherDispatchMetadata.TriggeredBy(dispatch.Watcher.Name),
			TriggerId = dispatch.Subscription.TriggerId,
			UserMetadata = WatcherDispatchMetadata.Build(dispatch),
		}, cancellationToken);

		return handle.ExecutionId;
	}
}

/// <summary>Shared vocabulary for how watcher dispatches identify themselves to the engine.</summary>
public static class WatcherDispatchMetadata
{
	/// <summary>The <c>triggeredBy</c> prefix; the run origin classifier maps it to the watcher origin.</summary>
	public const string TriggeredByPrefix = "watcher:";

	public static string TriggeredBy(string watcherName) => TriggeredByPrefix + watcherName;

	public static Dictionary<string, string> Build(WatcherDispatch dispatch) => new(StringComparer.Ordinal)
	{
		["watcherName"] = dispatch.Watcher.Name,
		["itemDedupKey"] = dispatch.DedupKey,
		["cursorAtFetch"] = dispatch.CursorAtFetch?.ToJsonString() ?? "null",
	};
}
