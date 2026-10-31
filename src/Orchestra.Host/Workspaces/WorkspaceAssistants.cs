using System.Text.Json.Nodes;
using Orchestra.Engine;
using Orchestra.Host.Hosting;
using Orchestra.Host.Registry;

namespace Orchestra.Host.Workspaces;

/// <summary>Materializes app-owned normal orchestrations; user-created files are never overwritten.</summary>
public sealed class WorkspaceAssistants(PortalWorkspaceService workspace, OrchestrationHostOptions options, OrchestrationRegistry registry)
{
	private readonly SemaphoreSlim _gate = new(1, 1);
	public async Task<OrchestrationEntry> PrepareAsync(string kind, string serverUrl, CancellationToken token)
	{
		if (kind is not ("create-orchestration" or "configure-orchestra")) throw new ArgumentException("Unknown assistant.");
		await _gate.WaitAsync(token);
		try
		{
			Directory.CreateDirectory(workspace.Root);
			var json = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(workspace.AssetsDirectory, "builtins", kind + ".json"), token))!.AsObject();
			json["defaultModel"] = options.DefaultModel ?? ModelDefaults.Model;
			json["defaultProvider"] = options.Provider ?? "copilot";
			foreach (var step in json["steps"]!.AsArray())
			{
				if (step!["type"]!.GetValue<string>() == "Prompt")
				{
					step["workingDirectory"] = workspace.Root;
					step["skillDirectories"] = new JsonArray(Path.Combine(workspace.AssetsDirectory, "skills", "orchestration-authoring"));
				}
				if (step["url"] is { } url) step["url"] = url.GetValue<string>().Replace("{{server.url}}", serverUrl.TrimEnd('/'));
			}
			var path = Path.Combine(options.DataPath, "builtins", kind + ".json");
			await WorkspaceSettingsStore.WriteAtomicAsync(path, json.ToJsonString(new() { WriteIndented = true }), token);
			return registry.Register(path);
		}
		finally { _gate.Release(); }
	}
}
