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
		templates = Templates.Select(t => new { t.Id, t.Title, t.Summary, requiresAgent = t.Id is not ("smoke-test" or "approval"), requiresPowerShell = t.Id == "generate" }),
		defaultProvider = host.Provider ?? "copilot",
		defaultModel = host.DefaultModel ?? ModelDefaults.Model,
	};
	private bool ReadWelcome()
	{
		try { return JsonNode.Parse(File.ReadAllText(Marker))?["showWelcome"]?.GetValue<bool>() ?? true; }
		catch (Exception ex) when (ex is IOException or JsonException) { return true; }
	}
	public Task DismissAsync(CancellationToken token) => WorkspaceSettingsStore.WriteAtomicAsync(Marker, "{\"showWelcome\":false}\n", token);

	public async Task InitializeAsync(CancellationToken token)
	{
		if (File.Exists(Marker)) return;
		var existing = registry.Count > 0 || File.Exists(settings.ConfigPath);
		if (!existing && Templates.Any(t => t.Id == "smoke-test")) await InstallTemplateAsync("smoke-test", token);
		await WorkspaceSettingsStore.WriteAtomicAsync(Marker, JsonSerializer.Serialize(new { showWelcome = !existing }), token);
	}

	public async Task<OrchestrationEntry> InstallTemplateAsync(string id, CancellationToken token)
	{
		var template = InitTemplateCatalog.Find(Templates, id) ?? throw new ArgumentException("Unknown starter template.");
		await _gate.WaitAsync(token);
		try
		{
			var existing = registry.GetByIdOrName(id);
			if (existing is not null) return existing;
			var result = InitScaffolder.Scaffold(new InitPlan(Root, template, null, null, false, true, false, true),
				Path.Combine(AssetsDirectory, "schemas"), Path.Combine(AssetsDirectory, "skills"));
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
			if (errors.Count > 0) return errors;
			var preview = OrchestrationDocuments.Preview(json);
			errors.AddRange(preview.Errors);
			var definition = OrchestrationParser.ParseOrchestration(json, registry.GlobalMcps);
			if (!InitScaffolder.OrchestrationNamePattern.IsMatch(definition.Name)) errors.Add("Use a kebab-case orchestration name.");
			return errors;
		}
		catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException or IOException or YamlDotNet.Core.YamlException)
		{ return [ex.Message]; }
	}

	public async Task<OrchestrationEntry> SaveAsync(string content, CancellationToken token)
	{
		var errors = Validate(content);
		if (errors.Count > 0) throw new ArgumentException(string.Join("\n", errors));
		var json = OrchestrationDocuments.Normalize(content);
		var name = JsonNode.Parse(json)!["name"]!.GetValue<string>();
		await _gate.WaitAsync(token);
		try
		{
			if (registry.GetByIdOrName(name) is not null) throw new WorkspaceConflictException($"An orchestration named '{name}' already exists. Choose another name.");
			var directory = Path.Combine(Root, "orchestrations");
			Directory.CreateDirectory(directory);
			var path = Path.Combine(directory, name + ".json");
			if (File.Exists(path) || File.Exists(Path.ChangeExtension(path, ".yaml")) || File.Exists(Path.ChangeExtension(path, ".yml")))
				throw new WorkspaceConflictException($"A workflow file named '{name}' already exists.");
			// CreateNew gives simultaneous saves a single winner, including other processes.
			await using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, true))
			{
				var bytes = System.Text.Encoding.UTF8.GetBytes(json.ReplaceLineEndings("\n"));
				try { await file.WriteAsync(bytes, token); }
				catch { await file.DisposeAsync(); File.Delete(path); throw; }
			}
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
}
