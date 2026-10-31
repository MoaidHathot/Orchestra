using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Orchestra.Engine;
using Orchestra.Host.Hosting;
using Orchestra.Host.Triggers;

namespace Orchestra.Host.Workspaces;

public sealed record SettingsSnapshot(string ConfigPath, string Source, string Revision, JsonObject Values,
	JsonObject Effective, IReadOnlyDictionary<string, string> Overrides, string[] RestartRequired, string? Error);
public sealed record SettingsUpdate(string Revision, JsonObject Changes);
public sealed class WorkspaceConflictException(string message) : InvalidOperationException(message);

/// <summary>Edits the same configuration used by CLI/host discovery, never a competing settings database.</summary>
public sealed partial class WorkspaceSettingsStore
{
	private readonly PortalWorkspaceOptions _workspace;
	private readonly OrchestrationHostOptions _host;
	private readonly TriggerManager? _triggers;
	private readonly ILogger<WorkspaceSettingsStore> _logger;
	private readonly SemaphoreSlim _gate = new(1, 1);
	private readonly Dictionary<string, string?> _startup = [];
	private static readonly string[] RestartFields = ["scan", "dataPath", "urls", "enableScheduler", "logLevel"];
	private static readonly string[] Fields = ["defaultProvider", "defaultModel", "defaultReasoningLevel", "defaultMaxConcurrency", "retention", "scan", "dataPath", "urls", "enableScheduler", "logLevel"];
	internal static readonly JsonDocumentOptions ReadOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

	public WorkspaceSettingsStore(PortalWorkspaceOptions workspace, OrchestrationHostOptions host, ILogger<WorkspaceSettingsStore> logger, TriggerManager? triggers = null)
	{
		_workspace = workspace; _host = host; _logger = logger; _triggers = triggers;
		try { var values = ReadObject(ReadText()); foreach (var key in RestartFields) _startup[key] = values[key]?.ToJsonString(); }
		catch (JsonException) { }
	}

	public string WorkspaceDirectory => _host.Scan?.Directory ?? Path.Combine(_host.DataPath, "workspace");
	private string ReadText() => File.Exists(_workspace.ConfigPath) ? File.ReadAllText(_workspace.ConfigPath) : "{}";
	internal static JsonObject ReadObject(string text) => JsonNode.Parse(text, new JsonNodeOptions { PropertyNameCaseInsensitive = true }, ReadOptions) as JsonObject ?? throw new JsonException("Expected a configuration object.");
	public static string Revision(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

	public SettingsSnapshot Get()
	{
		var text = ReadText();
		JsonObject values = []; string? error = null;
		try
		{
			var raw = ReadObject(text);
			foreach (var key in Fields) if (raw.ContainsKey(key)) values[key] = raw[key]?.DeepClone();
			if (raw["provider"] is { } alias) values["defaultProvider"] = alias.DeepClone();
		}
		catch (JsonException ex) { error = ex.Message; }
		var effective = new JsonObject
		{
			["defaultProvider"] = _host.Provider ?? "copilot", ["defaultModel"] = _host.DefaultModel ?? ModelDefaults.Model,
			["defaultReasoningLevel"] = _host.DefaultReasoningLevel?.ToString(), ["defaultMaxConcurrency"] = _host.DefaultMaxConcurrency,
			["dataPath"] = _host.DataPath, ["enableScheduler"] = _host.EnableScheduler, ["logLevel"] = _host.LogLevel,
			["scan"] = new JsonObject { ["directory"] = WorkspaceDirectory, ["recursive"] = _host.Scan?.Recursive ?? true, ["watch"] = _host.Scan?.Watch ?? false },
			["retention"] = new JsonObject { ["maxRunsPerOrchestration"] = _host.Retention.MaxRunsPerOrchestration, ["maxRunAgeDays"] = _host.Retention.MaxRunAgeDays },
		};
		return new(_workspace.ConfigPath, _workspace.ConfigSource, Revision(text), values, effective, _workspace.Overrides,
			RestartFields.Where(k => values[k]?.ToJsonString() != _startup.GetValueOrDefault(k)).ToArray(), error);
	}

	public async Task<SettingsSnapshot> SaveAsync(SettingsUpdate update, CancellationToken token)
	{
		ArgumentNullException.ThrowIfNull(update.Changes);
		Validate(update.Changes);
		await _gate.WaitAsync(token);
		try
		{
			var text = ReadText();
			if (Revision(text) != update.Revision) throw new WorkspaceConflictException("Configuration changed since it was opened. Reload settings before saving.");
			var raw = ReadObject(text);
			var patch = (JsonObject)update.Changes.DeepClone();
			if (patch.ContainsKey("defaultProvider") && raw.ContainsKey("provider")) patch["provider"] = patch["defaultProvider"]?.DeepClone();
			if (!File.Exists(_workspace.ConfigPath))
			{
				patch["dataPath"] ??= _host.DataPath;
				patch["scan"] ??= new JsonObject { ["directory"] = WorkspaceDirectory, ["recursive"] = true, ["watch"] = true };
			}
			var edited = JsoncEditor.Patch(text, patch);
			ReadObject(edited); // The original comments and unknown members are retained.
			await WriteAtomicAsync(_workspace.ConfigPath, edited, token);
			ApplyDefaults(update.Changes);
			LogSaved(_workspace.ConfigPath, string.Join(", ", update.Changes.Select(p => p.Key)));
			return Get();
		}
		finally { _gate.Release(); }
	}

	public static void Validate(JsonObject changes)
	{
		foreach (var (key, node) in changes)
		{
			if (!Fields.Contains(key, StringComparer.Ordinal)) throw new ArgumentException($"Unsupported settings field '{key}'.");
			if (node is null) continue;
			switch (key)
			{
				case "defaultProvider": if (node.GetValue<string>() is not ("copilot" or "opencode")) throw new ArgumentException("Choose copilot or opencode."); break;
				case "defaultMaxConcurrency": if (node.GetValue<int>() < 1) throw new ArgumentException("Concurrency must be a positive integer or null (unbounded)."); break;
				case "defaultReasoningLevel": if (!Enum.TryParse<ReasoningLevel>(node.GetValue<string>(), true, out _)) throw new ArgumentException("Unknown reasoning level."); break;
				case "logLevel": if (!Enum.TryParse<LogLevel>(node.GetValue<string>(), true, out _)) throw new ArgumentException("Unknown log level."); break;
				case "enableScheduler": _ = node.GetValue<bool>(); break;
				case "retention":
					foreach (var field in node.AsObject())
						if (field.Key is not ("maxRunsPerOrchestration" or "maxRunAgeDays") || field.Value?.GetValue<int>() < 0) throw new ArgumentException("Retention limits must be nonnegative integers.");
					break;
				case "scan":
					foreach (var field in node.AsObject())
					{
						if (field.Key == "directory") { if (string.IsNullOrWhiteSpace(field.Value?.GetValue<string>())) throw new ArgumentException("Workspace directory is required."); }
						else if (field.Key is "recursive" or "watch") _ = field.Value!.GetValue<bool>();
						else throw new ArgumentException($"Unknown scan field '{field.Key}'.");
					}
					break;
				default: if (string.IsNullOrWhiteSpace(node.GetValue<string>())) throw new ArgumentException($"'{key}' must not be empty; use null to inherit a default."); break;
			}
		}
	}

	private void ApplyDefaults(JsonObject changes)
	{
		if (changes.ContainsKey("defaultProvider")) _host.Provider = changes["defaultProvider"]?.GetValue<string>() ?? "copilot";
		if (changes.ContainsKey("defaultModel")) _host.DefaultModel = changes["defaultModel"]?.GetValue<string>();
		if (changes.ContainsKey("defaultMaxConcurrency")) _host.DefaultMaxConcurrency = changes["defaultMaxConcurrency"]?.GetValue<int>();
		if (changes.ContainsKey("defaultReasoningLevel")) _host.DefaultReasoningLevel = changes["defaultReasoningLevel"] is { } r ? Enum.Parse<ReasoningLevel>(r.GetValue<string>(), true) : null;
		if (changes["retention"] is JsonObject retention)
		{
			if (retention.ContainsKey("maxRunsPerOrchestration")) _host.Retention.MaxRunsPerOrchestration = retention["maxRunsPerOrchestration"]?.GetValue<int>();
			if (retention.ContainsKey("maxRunAgeDays")) _host.Retention.MaxRunAgeDays = retention["maxRunAgeDays"]?.GetValue<int>();
		}
		_triggers?.UpdateAgentDefaults(_host.ToHostAgentDefaults());
	}

	internal static async Task WriteAtomicAsync(string path, string text, CancellationToken token)
	{
		Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
		var temporary = path + $".{Guid.NewGuid():N}.tmp";
		try { await File.WriteAllTextAsync(temporary, text.ReplaceLineEndings("\n"), token); File.Move(temporary, path, overwrite: true); }
		finally { if (File.Exists(temporary)) File.Delete(temporary); }
	}

	[LoggerMessage(Level = LogLevel.Information, Message = "Updated settings in {Path}: {Fields}")]
	private partial void LogSaved(string path, string fields);
}
