using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Orchestra.Engine;
using Orchestra.Host.Hosting;
using Orchestra.Host.Registry;
using Orchestra.Host.Services;
using Orchestra.Host.Triggers;

namespace Orchestra.Host.Workspaces;

public sealed partial class PortalWorkspaceService(PortalWorkspaceOptions settings, WorkspaceSettingsStore configuration,
	OrchestrationHostOptions host, OrchestrationRegistry registry, TriggerManager triggers, ILogger<PortalWorkspaceService> logger)
{
	private readonly SemaphoreSlim _gate = new(1, 1);
	private string? _initializationError;
	public string Root => configuration.WorkspaceDirectory;
	public string AssetsDirectory => settings.AssetsDirectory;
	private string Marker => Path.Combine(host.DataPath, "portal-onboarding.json");
	public IReadOnlyList<InitTemplate> Templates => InitTemplateCatalog.Discover(Path.Combine(AssetsDirectory, "templates"));

	public object Status() => new
	{
		workspaceDirectory = Root,
		dataPath = host.DataPath,
		workflowDirectory = Path.Combine(Root, "orchestrations"),
		hasOrchestrations = registry.Count > 0,
		showWelcome = !File.Exists(Marker) || ReadWelcome(),
		initializationError = _initializationError,
		templates = Templates.Select(t => new { t.Id, t.Title, t.Summary, requiresAgent = t.Id != "smoke-test", requiresPowerShell = t.Id == "generate" }),
		defaultProvider = host.Provider ?? "copilot",
		defaultModel = host.DefaultModel ?? ModelDefaults.Model,
	};
	private bool ReadWelcome()
	{
		try
		{ return JsonNode.Parse(File.ReadAllText(Marker))?["showWelcome"]?.GetValue<bool>() ?? true; }
		catch (Exception ex) when (ex is IOException or JsonException) { return true; }
	}
	public Task DismissAsync(CancellationToken token) => WorkspaceSettingsStore.WriteAtomicAsync(Marker, "{\"showWelcome\":false}\n", token);

	public async Task InitializeAsync(CancellationToken token)
	{
		if (File.Exists(Marker))
			return;
		var existing = registry.Count > 0;
		if (!existing && Templates.Any(t => t.Id == "smoke-test"))
		{
			try
			{ await InstallTemplateAsync("smoke-test", token); }
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				_initializationError = $"Could not install the demo in '{Root}': {ex.Message}. Choose a writable workspace in Settings, then try the demo again.";
				LogInitializationFailed(Root, ex);
			}
		}
		await WorkspaceSettingsStore.WriteAtomicAsync(Marker, JsonSerializer.Serialize(new { showWelcome = !existing }), token);
	}

	public async Task<OrchestrationEntry> InstallTemplateAsync(string id, CancellationToken token)
	{
		var template = InitTemplateCatalog.Find(Templates, id) ?? throw new ArgumentException("Unknown starter template.");
		await _gate.WaitAsync(token);
		try
		{
			var existing = registry.GetByIdOrName(id);
			if (existing is not null)
				return existing;
			var result = InitScaffolder.Scaffold(new InitPlan(Root, template, null, null, false, true, false, true),
				Path.Combine(AssetsDirectory, "schemas"), Path.Combine(AssetsDirectory, "skills"));
			if (result.Files.Any(f => f.Path == result.OrchestrationPath && f.Outcome == InitFileOutcome.Written))
			{
				var body = await File.ReadAllTextAsync(result.OrchestrationPath, token);
				if (body.Contains("defaultModel:"))
				{
					body = System.Text.RegularExpressions.Regex.Replace(body, @"(?m)^defaultModel:[^\r\n]*", "defaultModel: " + JsonSerializer.Serialize(host.DefaultModel ?? ModelDefaults.Model));
					if (host.Provider is { } provider)
						body += "\ndefaultProvider: " + JsonSerializer.Serialize(provider) + "\n";
					await WorkspaceSettingsStore.WriteAtomicAsync(result.OrchestrationPath, body, token);
				}
			}
			return Register(result.OrchestrationPath);
		}
		finally { _gate.Release(); }
	}

	public IReadOnlyList<string> Validate(string content)
	{
		try
		{
			var json = OrchestrationDocuments.Normalize(content);
			var errors = OrchestrationSchemaValidator.ValidateJson(json, Path.Combine(AssetsDirectory, "schemas", "orchestration.schema.json")).ToList();
			if (errors.Count > 0)
				return errors;
			var name = JsonNode.Parse(json)!["name"]!.GetValue<string>();
			if (!InitScaffolder.OrchestrationNamePattern.IsMatch(name))
				return ["Use a kebab-case orchestration name."];
			var definition = OrchestrationParser.ParseOrchestrationDocument(json, Path.Combine(Root, "orchestrations", name + ".json"), registry.GlobalMcps);
			new OrchestrationScheduler().Schedule(definition);
			errors.AddRange(TemplateExpressionValidator.ValidateOrchestration(definition).Errors.Select(e => e.Message));
			return errors;
		}
		catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException or IOException or YamlDotNet.Core.YamlException)
		{ return [ex.Message]; }
	}

	public async Task<OrchestrationEntry> SaveAsync(string content, CancellationToken token)
	{
		var errors = Validate(content);
		if (errors.Count > 0)
			throw new ArgumentException(string.Join("\n", errors));
		var json = OrchestrationDocuments.Normalize(content);
		var name = JsonNode.Parse(json)!["name"]!.GetValue<string>();
		await _gate.WaitAsync(token);
		try
		{
			if (registry.GetByIdOrName(name) is not null)
				throw new WorkspaceConflictException($"An orchestration named '{name}' already exists. Choose another name.");
			var directory = Path.Combine(Root, "orchestrations");
			Directory.CreateDirectory(directory);
			var path = Path.Combine(directory, name + ".json");
			if (File.Exists(path) || File.Exists(Path.ChangeExtension(path, ".yaml")) || File.Exists(Path.ChangeExtension(path, ".yml")))
				throw new WorkspaceConflictException($"A workflow file named '{name}' already exists.");
			var temporary = path + $".{Guid.NewGuid():N}.tmp";
			try
			{
				await File.WriteAllTextAsync(temporary, json.ReplaceLineEndings("\n"), token);
				File.Move(temporary, path, overwrite: false); // Watchers see only the complete artifact.
			}
			finally { if (File.Exists(temporary)) File.Delete(temporary); }
			var entry = Register(path);
			LogCreated(entry.Id, path);
			return entry;
		}
		finally { _gate.Release(); }
	}

	public OrchestrationEntry Register(string path)
	{
		var entry = registry.Register(path);
		if (entry.Orchestration.Trigger.Enabled)
			triggers.RegisterTrigger(entry.Path, entry.Orchestration.Trigger, null, TriggerSource.Json, entry.Id, entry.Orchestration, entry.SourcePath);
		return entry;
	}

	[LoggerMessage(Level = LogLevel.Information, Message = "Created orchestration {Id} at {Path}")]
	private partial void LogCreated(string id, string path);

	[LoggerMessage(Level = LogLevel.Warning, Message = "Could not initialize Portal workspace at {Path}")]
	private partial void LogInitializationFailed(string path, Exception exception);
}
