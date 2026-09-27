using System.Text.Json.Nodes;

namespace Orchestra.Engine;

/// <summary>
/// Subscribes the orchestration to an in-process <em>watcher</em>: a poller (defined in
/// <c>orchestra.watchers.json</c> or inline under <see cref="Definition"/>) that fetches items
/// from a source on a schedule, deduplicates them per subscriber and launches this orchestration
/// once per genuinely new item. The trigger <em>is</em> the subscription - there is no separate
/// event bus.
/// </summary>
/// <remarks>
/// Exactly one of <see cref="Watcher"/> (library reference) or <see cref="Definition"/> (inline
/// definition) must be set; the host rejects the registration otherwise. The inline definition
/// is kept as raw JSON here because the watcher model types live in the <c>Orchestra.Watchers</c>
/// assembly, which depends on the engine (the reverse reference would be circular). The host
/// parses and validates it when the subscription is registered.
/// </remarks>
public class WatcherTriggerConfig : TriggerConfig
{
	/// <summary>
	/// Name of a watcher declared in <c>orchestra.watchers.json</c>. Mutually exclusive with
	/// <see cref="Definition"/>.
	/// </summary>
	public string? Watcher { get; init; }

	/// <summary>
	/// Inline watcher definition (same shape as a library entry plus a required <c>name</c>).
	/// Mutually exclusive with <see cref="Watcher"/>.
	/// </summary>
	public JsonObject? Definition { get; init; }

	/// <summary>
	/// Upper bound on <em>parallel launches</em> per tick for this subscription. It bounds how many
	/// items are handed to the launcher at once, not how many target runs may be in flight
	/// (orchestrations have their own concurrency controls). Defaults to 1.
	/// </summary>
	public int MaxConcurrent { get; init; } = 1;

	/// <summary>
	/// Maps a fetched item to orchestration parameters. Values are templates that may reference
	/// <c>{{item}}</c> (the whole item as JSON) or <c>{{item.path.to.field}}</c>.
	/// </summary>
	public Dictionary<string, string>? Parameters { get; init; }

	/// <summary>
	/// What a brand-new subscriber does about the source's existing backlog. Defaults to
	/// <see cref="WatcherCatchupMode.FromNow"/>.
	/// </summary>
	public WatcherCatchupMode Catchup { get; init; } = WatcherCatchupMode.FromNow;

	/// <summary>
	/// The effective watcher name: <see cref="Watcher"/> for library references, otherwise the
	/// inline definition's <c>name</c> (or <see langword="null"/> when neither is present).
	/// </summary>
	public string? ResolveWatcherName()
	{
		if (!string.IsNullOrWhiteSpace(Watcher))
			return Watcher;

		return Definition?["name"]?.GetValue<string>();
	}
}

/// <summary>
/// Backlog behaviour applied the first time an orchestration subscribes to a watcher.
/// </summary>
public enum WatcherCatchupMode
{
	/// <summary>
	/// Seed the new subscriber with every key the watcher has already seen, so the existing
	/// backlog is not dispatched. This is the default.
	/// </summary>
	FromNow,

	/// <summary>
	/// Start with an empty seen-set; the first tick dispatches every item currently visible.
	/// </summary>
	All,
}
