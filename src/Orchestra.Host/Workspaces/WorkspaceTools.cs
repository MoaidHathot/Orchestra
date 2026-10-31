using System.Text.Json;
using System.Text.Json.Nodes;
using Orchestra.Engine;
using Orchestra.Host.Registry;

namespace Orchestra.Host.Workspaces;

public sealed class WorkspaceValidationTool(Func<PortalWorkspaceService> workspace) : IOptInEngineTool
{
	public string OptInName => "validate_orchestration";
	public string Name => "orchestra_validate_orchestration";
	public string Description => "Validate JSON or YAML with Orchestra's schema, parser and dependency checks. Returns errors without writing files.";
	public string ParametersSchema => """{"type":"object","properties":{"content":{"type":"string"}},"required":["content"]}""";
	public string Execute(string arguments, EngineToolContext context)
	{
		using var input = JsonDocument.Parse(arguments);
		var errors = workspace().Validate(input.RootElement.GetProperty("content").GetString()!);
		return JsonSerializer.Serialize(new { valid = errors.Count == 0, errors });
	}
}
public sealed class WorkspaceInspectionTool(Func<WorkspaceSettingsStore> settings, Func<OrchestrationRegistry> registry) : IOptInEngineTool
{
	public string OptInName => "inspect_configuration";
	public string Name => "orchestra_inspect_configuration";
	public string Description => "Read safe Orchestra settings, revision, existing workflow names and integration names. Credentials are not returned.";
	public string ParametersSchema => """{"type":"object","properties":{}}""";
	public string Execute(string arguments, EngineToolContext context) => JsonSerializer.Serialize(new
	{
		settings = settings().Get(), existingWorkflows = registry().GetAll().Select(e => e.Orchestration.Name).ToArray(),
		integrations = registry().GlobalMcps.Select(m => new { name = m.Name, type = m.Type.ToString() }).ToArray(),
	}, new JsonSerializerOptions(JsonSerializerDefaults.Web));
}
public sealed class WorkspaceSettingsValidationTool(Func<WorkspaceSettingsStore> settings) : IOptInEngineTool
{
	public string OptInName => "validate_configuration";
	public string Name => "orchestra_validate_configuration";
	public string Description => "Validate a proposed settings update (revision and changes) without applying it.";
	public string ParametersSchema => """{"type":"object","properties":{"content":{"type":"string"}},"required":["content"]}""";
	public string Execute(string arguments, EngineToolContext context)
	{
		try
		{
			var content = JsonNode.Parse(arguments)!["content"]!.GetValue<string>();
			var update = JsonSerializer.Deserialize<SettingsUpdate>(content, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
			settings().Preview(update);
			return "{\"valid\":true,\"errors\":[]}";
		}
		catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or JsonException)
		{ return JsonSerializer.Serialize(new { valid = false, errors = new[] { ex.Message } }); }
	}
}
