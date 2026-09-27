using System.Text.Json.Serialization;

namespace Orchestra.Watchers.Models;

/// <summary>
/// A watcher definition: how to fetch items from a source, when to poll, how to recognise items
/// across ticks and what to do when a fetch fails. Library entries come from
/// <c>orchestra.watchers.json</c> (keyed by name); inline entries come from an orchestration's
/// <c>trigger.definition</c>.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type", IgnoreUnrecognizedTypeDiscriminators = false)]
[JsonDerivedType(typeof(PollCommandWatcher), PollCommandWatcher.TypeDiscriminator)]
[JsonDerivedType(typeof(PollScriptWatcher), PollScriptWatcher.TypeDiscriminator)]
public abstract class WatcherEntry
{
	/// <summary>
	/// Watcher name. For library entries the config loader sets it from the map key; for inline
	/// entries it comes from <c>definition.name</c>. Retains the author's spelling - file names use
	/// <see cref="WatcherNames.Sanitize"/>.
	/// </summary>
	public string Name { get; set; } = string.Empty;

	/// <summary>
	/// Directory that relative <c>fetch.workingDirectory</c> / <c>fetch.scriptFile</c> paths resolve
	/// against: the folder containing <c>orchestra.watchers.json</c> for library entries, the
	/// orchestration file's folder for inline entries. Not part of the JSON shape.
	/// </summary>
	[JsonIgnore]
	public string? BaseDirectory { get; set; }

	/// <summary>Disabled watchers are never started; subscriptions to them are rejected.</summary>
	public bool Enabled { get; init; } = true;

	/// <summary>When the watcher polls.</summary>
	public required WatcherSchedule Schedule { get; init; }

	/// <summary>Where items live in the fetcher output. <see langword="null"/> means the standard <c>items</c> array.</summary>
	public WatcherItemsConfig? Items { get; init; }

	/// <summary>Dedup policy. <see langword="null"/> means fifo mode keyed by a hash of the whole item.</summary>
	public WatcherDedup? Dedup { get; init; }

	/// <summary>What a failed fetch does to the loop.</summary>
	public WatcherFailurePolicy FailurePolicy { get; init; } = WatcherFailurePolicy.Warn;

	/// <summary>The wire discriminator (<c>poll-command</c> / <c>poll-script</c>).</summary>
	[JsonIgnore]
	public abstract string TypeName { get; }

	/// <summary>Shared fetch settings (timeout, environment, config payload) regardless of fetcher kind.</summary>
	[JsonIgnore]
	public abstract WatcherFetchConfigBase FetchBase { get; }

	/// <summary>Effective dedup policy (defaults applied).</summary>
	[JsonIgnore]
	public WatcherDedup EffectiveDedup => Dedup ?? WatcherDedup.Default;
}

/// <summary>
/// Runs an executable on every tick (<c>type: poll-command</c>).
/// </summary>
public sealed class PollCommandWatcher : WatcherEntry
{
	public const string TypeDiscriminator = "poll-command";

	public required WatcherFetchCommandConfig Fetch { get; init; }

	public override string TypeName => TypeDiscriminator;

	public override WatcherFetchConfigBase FetchBase => Fetch;
}

/// <summary>
/// Runs a script through a shell interpreter on every tick (<c>type: poll-script</c>).
/// </summary>
public sealed class PollScriptWatcher : WatcherEntry
{
	public const string TypeDiscriminator = "poll-script";

	public required WatcherFetchScriptConfig Fetch { get; init; }

	public override string TypeName => TypeDiscriminator;

	public override WatcherFetchConfigBase FetchBase => Fetch;
}
