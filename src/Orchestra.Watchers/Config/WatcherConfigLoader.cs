using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Orchestra.Engine.Serialization;
using Orchestra.Watchers.Models;
using Orchestra.Watchers.Runtime;

namespace Orchestra.Watchers.Config;

/// <summary>
/// Loads and validates <c>orchestra.watchers.json</c> (the watcher library) and inline
/// <c>trigger.definition</c> blocks. Parallels <c>OrchestraConfigLoader</c> for the other sidecars:
/// comments and trailing commas are allowed, <c>${VAR}</c> environment references are expanded,
/// and every structural rule the schema cannot express (mutual exclusions, cron syntax) is
/// enforced here with a dotted path in the error.
/// </summary>
public static class WatcherConfigLoader
{
	/// <summary>Sidecar file name, resolved next to <c>orchestra.json</c> like the MCP/services sidecars.</summary>
	public const string FileName = "orchestra.watchers.json";

	/// <summary>
	/// Serializer settings for watcher entries. Shared with the CLI so <c>watcher test</c> parses the
	/// file exactly as the host does.
	/// </summary>
	public static JsonSerializerOptions JsonOptions { get; } = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		PropertyNameCaseInsensitive = true,
		ReadCommentHandling = JsonCommentHandling.Skip,
		AllowTrailingCommas = true,
		AllowOutOfOrderMetadataProperties = true,
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
		Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
	};

	/// <summary>
	/// Loads the library from <paramref name="path"/>. A missing file yields
	/// <see cref="WatcherLibrary.Empty"/>; malformed JSON or an invalid entry throws
	/// <see cref="WatcherValidationException"/>.
	/// </summary>
	public static WatcherLibrary Load(string path)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(path);

		if (!File.Exists(path))
			return WatcherLibrary.Empty;

		var json = File.ReadAllText(path);
		return Parse(json, path);
	}

	/// <summary>
	/// Parses library JSON. <paramref name="sourcePath"/> is recorded on the result and used as the
	/// base directory for relative fetch paths (its containing folder).
	/// </summary>
	public static WatcherLibrary Parse(string json, string? sourcePath = null)
	{
		ArgumentNullException.ThrowIfNull(json);

		json = EnvironmentVariableExpander.Expand(json, sourcePath);

		JsonObject root;
		try
		{
			var node = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions
			{
				CommentHandling = JsonCommentHandling.Skip,
				AllowTrailingCommas = true,
			});
			root = node as JsonObject
				?? throw new WatcherValidationException("$", "the document must be a JSON object with a 'watchers' map.");
		}
		catch (JsonException ex)
		{
			throw new WatcherValidationException("$", $"not valid JSON - {ex.Message}", ex);
		}

		foreach (var (key, _) in root)
		{
			if (key is not ("watchers" or "$schema"))
				throw new WatcherValidationException(key, "unknown top-level property (expected only 'watchers' and '$schema').");
		}

		if (root["watchers"] is not JsonObject watchersNode)
			throw new WatcherValidationException("watchers", "required property missing or not an object (a map of watcher name to definition).");

		var baseDirectory = sourcePath is null ? null : Path.GetDirectoryName(Path.GetFullPath(sourcePath));
		var entries = new Dictionary<string, WatcherEntry>(StringComparer.Ordinal);
		foreach (var (name, entryNode) in watchersNode)
		{
			var path = $"watchers.{name}";
			if (string.IsNullOrWhiteSpace(name))
				throw new WatcherValidationException("watchers", "watcher names must not be empty.");

			if (entryNode is not JsonObject entryObject)
				throw new WatcherValidationException(path, "a watcher definition must be a JSON object.");

			var entry = DeserializeEntry(entryObject, path);
			entry.Name = name;
			entry.BaseDirectory = baseDirectory;
			Validate(entry, path);
			entries[name] = entry;
		}

		return new WatcherLibrary(entries, sourcePath);
	}

	/// <summary>
	/// Parses an inline <c>trigger.definition</c>. The block must carry a <c>name</c>; relative
	/// fetch paths resolve against <paramref name="baseDirectory"/> (the orchestration's folder).
	/// </summary>
	public static WatcherEntry ParseInlineDefinition(JsonObject definition, string? baseDirectory, string path = "trigger.definition")
	{
		ArgumentNullException.ThrowIfNull(definition);

		var nameNode = definition["name"];
		var name = nameNode is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
		if (string.IsNullOrWhiteSpace(name))
		{
			throw new WatcherValidationException(
				$"{path}.name",
				"inline watcher definitions require a `name` field to be referenced for state and logging.");
		}

		// 'name' is not part of the entry shape; strip it before polymorphic deserialization.
		var withoutName = new JsonObject();
		foreach (var (key, node) in definition)
		{
			if (key == "name")
				continue;
			withoutName[key] = node?.DeepClone();
		}

		var entry = DeserializeEntry(withoutName, path);
		entry.Name = name;
		entry.BaseDirectory = baseDirectory;
		Validate(entry, path);
		return entry;
	}

	private static WatcherEntry DeserializeEntry(JsonObject entryObject, string path)
	{
		var typeNode = entryObject["type"];
		var typeText = typeNode is JsonValue typeValue && typeValue.TryGetValue<string>(out var t) ? t : null;
		if (string.IsNullOrWhiteSpace(typeText))
			throw new WatcherValidationException($"{path}.type", "required property missing (expected 'poll-command' or 'poll-script').");

		if (typeText is not (PollCommandWatcher.TypeDiscriminator or PollScriptWatcher.TypeDiscriminator))
			throw new WatcherValidationException($"{path}.type", $"unknown watcher type '{typeText}' (expected 'poll-command' or 'poll-script').");

		try
		{
			return entryObject.Deserialize<WatcherEntry>(JsonOptions)
				?? throw new WatcherValidationException(path, "a watcher definition must be a JSON object.");
		}
		catch (JsonException ex)
		{
			throw new WatcherValidationException(path, DescribeJsonError(ex), ex);
		}
	}

	private static string DescribeJsonError(JsonException ex)
	{
		var message = ex.Message;
		// STJ appends " Path: $.x | LineNumber: n | ..." - the dotted path we already carry is clearer.
		var cut = message.IndexOf(" Path:", StringComparison.Ordinal);
		if (cut > 0)
			message = message[..cut].TrimEnd();
		if (ex.Path is { Length: > 0 } stjPath && stjPath != "$")
			message = $"{message} (at {stjPath})";
		return message;
	}

	/// <summary>
	/// Enforces the rules the JSON shape alone cannot: schedule and script-source mutual exclusion,
	/// dedup option/mode consistency, positive bounds, and cron syntax.
	/// </summary>
	public static void Validate(WatcherEntry entry, string path = "watcher")
	{
		ArgumentNullException.ThrowIfNull(entry);

		if (entry.Schedule is null)
			throw new WatcherValidationException($"{path}.schedule", "required property missing.");

		var hasInterval = entry.Schedule.IntervalSeconds.HasValue;
		var hasCron = !string.IsNullOrWhiteSpace(entry.Schedule.Cron);
		if (hasInterval == hasCron)
		{
			throw new WatcherValidationException(
				$"{path}.schedule",
				hasInterval ? "set either intervalSeconds or cron, not both." : "set exactly one of intervalSeconds or cron.");
		}

		if (hasInterval && entry.Schedule.IntervalSeconds!.Value < 1)
			throw new WatcherValidationException($"{path}.schedule.intervalSeconds", "must be a positive integer.");

		if (hasCron)
		{
			try
			{
				WatcherScheduleCalculator.ParseCron(entry.Schedule.Cron!);
			}
			catch (Exception ex) when (ex is FormatException or ArgumentException or Cronos.CronFormatException)
			{
				throw new WatcherValidationException($"{path}.schedule.cron", $"invalid cron expression '{entry.Schedule.Cron}': {ex.Message}", ex);
			}
		}

		var fetch = entry.FetchBase;
		if (fetch.TimeoutSeconds < 1)
			throw new WatcherValidationException($"{path}.fetch.timeoutSeconds", "must be a positive integer.");

		switch (entry)
		{
			case PollCommandWatcher command when string.IsNullOrWhiteSpace(command.Fetch.Command):
				throw new WatcherValidationException($"{path}.fetch.command", "must not be empty.");

			case PollScriptWatcher script:
				if (string.IsNullOrWhiteSpace(script.Fetch.Shell))
					throw new WatcherValidationException($"{path}.fetch.shell", "must not be empty.");

				var hasFile = !string.IsNullOrWhiteSpace(script.Fetch.ScriptFile);
				var hasInline = !string.IsNullOrWhiteSpace(script.Fetch.Script);
				if (hasFile == hasInline)
				{
					throw new WatcherValidationException(
						$"{path}.fetch",
						hasFile ? "set either scriptFile or script, not both." : "set exactly one of scriptFile or script.");
				}
				break;
		}

		if (entry.Items is not null && string.IsNullOrWhiteSpace(entry.Items.JsonPath))
			throw new WatcherValidationException($"{path}.items.jsonPath", "must not be empty.");

		if (entry.Items is not null)
		{
			try
			{
				WatcherItemPath.Parse(entry.Items.JsonPath);
			}
			catch (FormatException ex)
			{
				throw new WatcherValidationException($"{path}.items.jsonPath", ex.Message, ex);
			}
		}

		if (entry.Dedup is { } dedup)
		{
			if (dedup.Key is not null && string.IsNullOrWhiteSpace(dedup.Key))
				throw new WatcherValidationException($"{path}.dedup.key", "must not be empty when set.");

			if (dedup.Mode == WatcherDedupMode.Fifo && dedup.Presence is not null)
				throw new WatcherValidationException($"{path}.dedup.presence", "not allowed when mode is fifo.");

			if (dedup.Mode == WatcherDedupMode.Presence && dedup.Fifo is not null)
				throw new WatcherValidationException($"{path}.dedup.fifo", "not allowed when mode is presence.");

			if (dedup.EffectiveFifo.MaxEntries < 1)
				throw new WatcherValidationException($"{path}.dedup.fifo.maxEntries", "must be a positive integer.");

			if (dedup.EffectiveFifo.TtlDays < 1)
				throw new WatcherValidationException($"{path}.dedup.fifo.ttlDays", "must be a positive integer.");

			if (dedup.EffectivePresence.GraceTicks < 1)
				throw new WatcherValidationException($"{path}.dedup.presence.graceTicks", "must be a positive integer.");
		}
	}
}
