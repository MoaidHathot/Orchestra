using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Orchestra.Engine;
using Orchestra.Engine.Serialization;
using Orchestra.Host.Hosting;
using Orchestra.Host.Triggers;

namespace Orchestra.Host.Workspaces;

public sealed record SettingsSnapshot(string ConfigPath, string Source, string Revision, JsonObject Values,
	JsonObject Effective, IReadOnlyDictionary<string, string> Overrides, string[] RestartRequired, string? Error,
	IReadOnlyDictionary<string, string>? Sources = null, string? SuggestedModel = null);
public sealed record SettingsUpdate(string Revision, JsonObject Changes);
public sealed class WorkspaceConflictException(string message) : InvalidOperationException(message);

/// <summary>Edits the same configuration used by CLI/host discovery, never a competing settings database.</summary>
public sealed partial class WorkspaceSettingsStore
{
	private readonly PortalWorkspaceOptions _workspace;
	private readonly OrchestrationHostOptions _host;
	private readonly TriggerManager? _triggers;
	private readonly ILogger<WorkspaceSettingsStore> _logger;
	private readonly SemaphoreSlim _gate;
	private static readonly ConcurrentDictionary<string, SemaphoreSlim> FileGates = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
	private readonly Dictionary<string, string?> _startup = [];
	private static readonly string[] RestartFields = ["scan", "dataPath", "urls", "enableScheduler", "logLevel"];
	private static readonly string[] Fields = ["defaultProvider", "defaultModel", "defaultReasoningLevel", "defaultMaxConcurrency", "retention", "scan", "dataPath", "urls", "enableScheduler", "logLevel"];
	internal static readonly JsonDocumentOptions ReadOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

	public WorkspaceSettingsStore(PortalWorkspaceOptions workspace, OrchestrationHostOptions host, ILogger<WorkspaceSettingsStore> logger, TriggerManager? triggers = null)
	{
		_workspace = workspace;
		_host = host;
		_logger = logger;
		_triggers = triggers;
		_gate = GateFor(workspace.ConfigPath);
		try
		{ var values = ReadObject(ReadText()); foreach (var key in RestartFields) _startup[key] = values[key]?.ToJsonString(); }
		catch (JsonException) { }
	}

	public string WorkspaceDirectory => Path.GetFullPath(_host.Scan?.Directory ?? Path.Combine(_host.DataPath, "workspace"));
	internal static SemaphoreSlim GateFor(string path) => FileGates.GetOrAdd(Path.GetFullPath(path), _ => new(1, 1));
	private string ReadText() => File.Exists(_workspace.ConfigPath) ? File.ReadAllText(_workspace.ConfigPath) : "{}";
	internal static JsonObject ReadObject(string text) => JsonNode.Parse(text, new JsonNodeOptions { PropertyNameCaseInsensitive = true }, ReadOptions) as JsonObject ?? throw new JsonException("Expected a configuration object.");
	public static string Revision(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

	public SettingsSnapshot Get()
	{
		var text = ReadText();
		JsonObject values = [];
		string? error = null;
		try
		{
			var raw = ReadObject(text);
			foreach (var key in Fields)
			if (raw.ContainsKey(key))
				values[key] = raw[key]?.DeepClone();
			if (raw["provider"] is { } alias)
				values["defaultProvider"] = alias.DeepClone();
		}
		catch (JsonException ex) { error = ex.Message; }
		var defaults = _host.ToHostAgentDefaults();
		var effective = new JsonObject
		{
			["defaultProvider"] = defaults.Provider ?? "copilot",
			["defaultModel"] = defaults.Model,
			["defaultReasoningLevel"] = defaults.ReasoningLevel?.ToString(),
			["defaultMaxConcurrency"] = defaults.MaxConcurrency,
			["dataPath"] = _host.DataPath,
			["urls"] = _host.HostBaseUrl,
			["enableScheduler"] = _host.EnableScheduler,
			["logLevel"] = _host.LogLevel,
			["scan"] = new JsonObject { ["directory"] = WorkspaceDirectory, ["recursive"] = _host.Scan?.Recursive ?? true, ["watch"] = _host.Scan?.Watch ?? false },
			["retention"] = new JsonObject { ["maxRunsPerOrchestration"] = _host.Retention.MaxRunsPerOrchestration, ["maxRunAgeDays"] = _host.Retention.MaxRunAgeDays },
		};
		var restart = RestartFields.Where(k => values[k]?.ToJsonString() != _startup.GetValueOrDefault(k)).ToArray();
		var sources = Fields.ToDictionary(k => k, k => _workspace.Overrides.TryGetValue(k, out var overridden) ? overridden
			: values[k] is null ? "Built-in default"
			: restart.Contains(k) ? "Configuration file (pending restart)"
			: values[k]!.ToJsonString().Contains("${", StringComparison.Ordinal) || values[k]!.ToJsonString().Contains("env:", StringComparison.Ordinal)
				? "Configuration file (environment reference)" : "Configuration file");
		return new(_workspace.ConfigPath, _workspace.ConfigSource == "None" && File.Exists(_workspace.ConfigPath) ? "User" : _workspace.ConfigSource,
			Revision(text), values, effective, _workspace.Overrides, restart, error, sources, ModelDefaults.Model);
	}

	public async Task<SettingsSnapshot> SaveAsync(SettingsUpdate update, CancellationToken token)
	{
		ArgumentNullException.ThrowIfNull(update.Changes);
		var resolvedChanges = ResolveChanges(update.Changes);
		await _gate.WaitAsync(token);
		try
		{
			var text = ReadText();
			if (Revision(text) != update.Revision)
				throw new WorkspaceConflictException("Configuration changed since it was opened. Reload settings before saving.");
			var raw = ReadObject(text);
			var patch = (JsonObject)update.Changes.DeepClone();
			if (patch.ContainsKey("defaultProvider") && raw.ContainsKey("provider"))
				patch["provider"] = patch["defaultProvider"]?.DeepClone();
			if (patch["scan"] is JsonObject scan && !scan.ContainsKey("directory") && raw["scan"]?["directory"] is null)
				scan["directory"] = WorkspaceDirectory;
			if (!File.Exists(_workspace.ConfigPath))
			{
				patch["dataPath"] ??= _host.DataPath;
				patch["scan"] ??= new JsonObject { ["directory"] = WorkspaceDirectory, ["recursive"] = true, ["watch"] = true };
			}
			var edited = JsoncEditor.Patch(text, patch);
			ReadObject(edited); // The original comments and unknown members are retained.
			await WriteAtomicAsync(_workspace.ConfigPath, edited, token, update.Revision);
			ApplyDefaults(resolvedChanges);
			LogSaved(_workspace.ConfigPath, string.Join(", ", update.Changes.Select(p => p.Key)));
			return Get();
		}
		finally { _gate.Release(); }
	}

	public object Preview(SettingsUpdate update)
	{
		ArgumentNullException.ThrowIfNull(update.Changes);
		_ = ResolveChanges(update.Changes);
		if (Get().Revision != update.Revision)
			throw new WorkspaceConflictException("Configuration changed. Inspect it again before proposing changes.");
		var restart = update.Changes.Select(p => p.Key).Where(RestartFields.Contains).ToArray();
		return new
		{
			message = update.Changes.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) +
			(restart.Length > 0 ? "\nRestart required for: " + string.Join(", ", restart) : "\nApplies to new runs."),
			restartRequired = restart
		};
	}

	private JsonObject ResolveChanges(JsonObject changes)
	{
		var resolved = ReadObject(EnvironmentVariableExpander.Expand(changes.ToJsonString(), _workspace.ConfigPath));
		Validate(resolved);
		if (resolved["dataPath"] is { } dataPath)
			_ = Path.GetFullPath(dataPath.GetValue<string>(), Path.GetDirectoryName(Path.GetFullPath(_workspace.ConfigPath))!);
		if (resolved["scan"]?["directory"] is { } directory)
			_ = Path.GetFullPath(directory.GetValue<string>(), Path.GetDirectoryName(Path.GetFullPath(_workspace.ConfigPath))!);
		return resolved;
	}

	public static void Validate(JsonObject changes)
	{
		ArgumentNullException.ThrowIfNull(changes);
		foreach (var (key, node) in changes)
		{
			if (!Fields.Contains(key, StringComparer.Ordinal))
				throw new ArgumentException($"Unsupported settings field '{key}'.");
			if (node is null)
				continue;
			try
			{
				switch (key)
				{
					case "defaultProvider":
						if (node.GetValue<string>() is not ("copilot" or "opencode"))
							throw new ArgumentException("Choose copilot or opencode.");
						break;
					case "defaultMaxConcurrency":
						if (node.GetValue<int>() < 1)
							throw new ArgumentException("Concurrency must be a positive integer or null (unbounded).");
						break;
					case "defaultReasoningLevel":
						if (!Enum.TryParse<ReasoningLevel>(node.GetValue<string>(), true, out var reasoning) || !Enum.IsDefined(reasoning))
							throw new ArgumentException("Unknown reasoning level.");
						break;
					case "logLevel":
						if (!Enum.TryParse<LogLevel>(node.GetValue<string>(), true, out var level) || !Enum.IsDefined(level))
							throw new ArgumentException("Unknown log level.");
						break;
					case "enableScheduler":
						_ = node.GetValue<bool>();
						break;
					case "urls":
						foreach (var address in node.GetValue<string>().Split(';', StringSplitOptions.TrimEntries))
						{
							if (!Uri.TryCreate(address.Replace("*", "localhost").Replace("+", "localhost"), UriKind.Absolute, out var uri) ||
								uri.Scheme is not ("http" or "https") || uri.AbsolutePath != "/" || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
								throw new ArgumentException("Listening URLs must be HTTP(S) addresses, separated by semicolons (for example http://localhost:5000).");
						}
						break;
					case "retention":
						foreach (var field in node.AsObject())
							if (field.Key is not ("maxRunsPerOrchestration" or "maxRunAgeDays") || field.Value?.GetValue<int>() < 0)
								throw new ArgumentException("Retention limits must be nonnegative integers.");
						break;
					case "scan":
						foreach (var field in node.AsObject())
						{
							if (field.Key == "directory")
							{ if (string.IsNullOrWhiteSpace(field.Value?.GetValue<string>())) throw new ArgumentException("Workspace directory is required."); }
							else if (field.Key is "recursive" or "watch")
							{ if (field.Value is not null) _ = field.Value.GetValue<bool>(); }
							else
								throw new ArgumentException($"Unknown scan field '{field.Key}'.");
						}
						break;
					default:
						if (string.IsNullOrWhiteSpace(node.GetValue<string>()))
							throw new ArgumentException($"'{key}' must not be empty; use null to inherit a default.");
						break;
				}
			}
			catch (Exception ex) when (ex is InvalidOperationException or FormatException or OverflowException)
			{ throw new ArgumentException($"Invalid value for '{key}'. Check its type and allowed values.", ex); }
		}
	}

	private void ApplyDefaults(JsonObject changes)
	{
		var defaults = _host.ToHostAgentDefaults();
		if (changes.ContainsKey("defaultProvider"))
			defaults = defaults with { Provider = changes["defaultProvider"]?.GetValue<string>() ?? "copilot" };
		if (changes.ContainsKey("defaultModel"))
			defaults = defaults with { Model = changes["defaultModel"]?.GetValue<string>() };
		if (changes.ContainsKey("defaultMaxConcurrency"))
			defaults = defaults with { MaxConcurrency = changes["defaultMaxConcurrency"]?.GetValue<int>() };
		if (changes.ContainsKey("defaultReasoningLevel"))
			defaults = defaults with { ReasoningLevel = changes["defaultReasoningLevel"] is { } r ? Enum.Parse<ReasoningLevel>(r.GetValue<string>(), true) : null };
		_host.UpdateAgentDefaults(defaults);
		if (changes["retention"] is JsonObject retention)
		{
			if (retention.ContainsKey("maxRunsPerOrchestration"))
				_host.Retention.MaxRunsPerOrchestration = retention["maxRunsPerOrchestration"]?.GetValue<int>();
			if (retention.ContainsKey("maxRunAgeDays"))
				_host.Retention.MaxRunAgeDays = retention["maxRunAgeDays"]?.GetValue<int>();
		}
		else if (changes.ContainsKey("retention"))
		{
			_host.Retention.MaxRunsPerOrchestration = null;
			_host.Retention.MaxRunAgeDays = null;
		}
		_triggers?.UpdateAgentDefaults(defaults);
	}

	internal static async Task WriteAtomicAsync(string path, string text, CancellationToken token, string? expectedRevision = null)
	{
		Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
		var temporary = path + $".{Guid.NewGuid():N}.tmp";
		try
		{
			await File.WriteAllTextAsync(temporary, text.ReplaceLineEndings("\n"), token);
			token.ThrowIfCancellationRequested();
			if (expectedRevision is not null && Revision(File.Exists(path) ? await File.ReadAllTextAsync(path, token) : "{}") != expectedRevision)
				throw new WorkspaceConflictException("Configuration changed while saving. Reload before retrying.");
			File.Move(temporary, path, overwrite: true);
		}
		finally { if (File.Exists(temporary)) File.Delete(temporary); }
	}

	[LoggerMessage(Level = LogLevel.Information, Message = "Updated settings in {Path}: {Fields}")]
	private partial void LogSaved(string path, string fields);
}
