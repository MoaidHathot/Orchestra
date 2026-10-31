using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Hosting;
using Orchestra.Engine;
using Orchestra.Host.Hosting;
using Orchestra.Host.Registry;

namespace Orchestra.Host.Workspaces;

/// <summary>Materializes app-owned normal orchestrations; user-created files are never overwritten.</summary>
public sealed class WorkspaceAssistants(PortalWorkspaceService workspace, OrchestrationHostOptions options,
	OrchestrationRegistry registry, IServer server) : IHostedLifecycleService
{
	private readonly SemaphoreSlim _gate = new(1, 1);
	private static readonly string[] Kinds = ["create-orchestration", "configure-orchestra"];

	public async Task<OrchestrationEntry> PrepareAsync(string kind, CancellationToken token)
	{
		if (!Kinds.Contains(kind, StringComparer.Ordinal))
			throw new ArgumentException("Unknown assistant.");
		await _gate.WaitAsync(token);
		try
		{ return await PrepareCoreAsync(kind, token); }
		finally { _gate.Release(); }
	}

	/// <summary>Refresh app-owned registrations after a restart, package upgrade or defaults change.</summary>
	public async Task RefreshRegisteredAsync(CancellationToken token)
	{
		await _gate.WaitAsync(token);
		try
		{
			foreach (var kind in Kinds)
			{
				var path = SourcePath(kind);
				if (registry.GetAll().Any(entry => string.Equals(Path.GetFullPath(entry.SourcePath ?? entry.Path), path,
					OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)))
					await PrepareCoreAsync(kind, token);
			}
		}
		finally { _gate.Release(); }
	}

	private string SourcePath(string kind) => Path.GetFullPath(Path.Combine(options.DataPath, "builtins", kind + ".json"));

	private async Task<OrchestrationEntry> PrepareCoreAsync(string kind, CancellationToken token)
	{
		Directory.CreateDirectory(workspace.Root);
		var json = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(workspace.AssetsDirectory, "builtins", kind + ".json"), token))!.AsObject();
		var defaults = options.ToHostAgentDefaults();
		json["defaultModel"] = defaults.Model ?? ModelDefaults.Model;
		json["defaultProvider"] = defaults.Provider ?? "copilot";
		foreach (var step in json["steps"]!.AsArray())
		{
			if (step!["type"]!.GetValue<string>() == "Prompt")
			{
				step["workingDirectory"] = workspace.Root;
				step["skillDirectories"] = new JsonArray(Path.Combine(workspace.AssetsDirectory, "skills", "orchestration-authoring"));
			}
			if (step["url"] is { } url)
				step["url"] = url.GetValue<string>().Replace("{{server.url}}", LocalAddress(server));
		}
		var path = SourcePath(kind);
		var text = json.ToJsonString(new() { WriteIndented = true });
		if (!File.Exists(path) || await File.ReadAllTextAsync(path, token) != text)
			await WorkspaceSettingsStore.WriteAtomicAsync(path, text, token);
		return registry.Register(path);
	}

	internal static string LocalAddress(IServer server)
	{
		// Resolve from the bound listener, never from the request's Host header.
		var addresses = server.Features.Get<IServerAddressesFeature>()?.Addresses;
		var address = addresses?.FirstOrDefault(a => a.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) ?? addresses?.FirstOrDefault();
		if (address is null)
			return "http://localhost"; // In-process TestServer.
		var uri = new UriBuilder(address.Replace("+", "localhost").Replace("*", "localhost"));
		if (uri.Host is "0.0.0.0" or "[::]" or "::")
			uri.Host = "localhost";
		return uri.Uri.ToString().TrimEnd('/');
	}

	public Task StartingAsync(CancellationToken token) => Task.CompletedTask;
	public Task StartAsync(CancellationToken token) => Task.CompletedTask;
	public Task StartedAsync(CancellationToken token) => RefreshRegisteredAsync(token);
	public Task StoppingAsync(CancellationToken token) => Task.CompletedTask;
	public Task StopAsync(CancellationToken token) => Task.CompletedTask;
	public Task StoppedAsync(CancellationToken token) => Task.CompletedTask;
}
