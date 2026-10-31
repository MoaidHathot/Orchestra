using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Configuration;
using Orchestra.Host.Hosting;
using Orchestra.Host.Workspaces;

namespace Orchestra.Host.Api;

public static class WorkspaceApi
{
	public static IServiceCollection AddPortalWorkspace(this IServiceCollection services)
	{
		services.TryAddSingleton(sp =>
		{
			var info = OrchestraConfigLoader.Describe();
			var config = sp.GetRequiredService<IConfiguration>();
			var overrides = new Dictionary<string, string>();
			if (config["data-path"] is not null) overrides["dataPath"] = "--data-path / host configuration";
			else if (Environment.GetEnvironmentVariable("ORCHESTRA_PORTAL_DATA_PATH") is not null) overrides["dataPath"] = "ORCHESTRA_PORTAL_DATA_PATH";
			if (Environment.GetEnvironmentVariable("ORCHESTRA_ORCHESTRATIONS_PATH") is not null) overrides["scan"] = "ORCHESTRA_ORCHESTRATIONS_PATH";
			else if (config["orchestrations-path"] is not null) overrides["scan"] = "--orchestrations-path / host configuration";
			return new PortalWorkspaceOptions { ConfigPath = info.Path ?? OrchestraConfigLoader.GetDefaultConfigPath(), ConfigSource = info.Source.ToString(), Overrides = overrides };
		});
		services.TryAddSingleton<WorkspaceSettingsStore>();
		services.TryAddSingleton<PortalWorkspaceService>();
		return services;
	}

	public static IEndpointRouteBuilder MapWorkspaceApi(this IEndpointRouteBuilder endpoints)
	{
		var group = endpoints.MapGroup("/api/workspace");
		group.MapGet("", (PortalWorkspaceService workspace) => Results.Ok(workspace.Status()));
		group.MapPost("/dismiss", async (PortalWorkspaceService workspace, CancellationToken token) => { await workspace.DismissAsync(token); return Results.Ok(); });
		group.MapGet("/settings", (WorkspaceSettingsStore settings) => Results.Ok(settings.Get()));
		group.MapPost("/settings", async (SettingsUpdate update, WorkspaceSettingsStore settings, CancellationToken token) =>
			await Guard(async () => Results.Ok(await settings.SaveAsync(update, token))));
		group.MapPost("/templates/{id}", async (string id, PortalWorkspaceService workspace, CancellationToken token) =>
			await Guard(async () => Entry(await workspace.InstallTemplateAsync(id, token))));
		group.MapPost("/validate", (DocumentContent request, PortalWorkspaceService workspace) =>
		{
			var errors = workspace.Validate(request.Content);
			return Results.Ok(new { valid = errors.Count == 0, errors });
		});
		group.MapPost("/orchestrations", async (JsonElement definition, PortalWorkspaceService workspace, CancellationToken token) =>
			await Guard(async () => Entry(await workspace.SaveAsync(definition.GetRawText(), token))));
		return endpoints;
	}

	internal static IResult Entry(Registry.OrchestrationEntry entry) => Results.Ok(new { id = entry.Id, name = entry.Orchestration.Name, path = entry.SourcePath ?? entry.Path });
	internal static async Task<IResult> Guard(Func<Task<IResult>> operation)
	{
		try { return await operation(); }
		catch (WorkspaceConflictException ex) { return Results.Problem(ex.Message, statusCode: 409); }
		catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or JsonException or IOException or UnauthorizedAccessException)
		{ return Results.Problem(ex.Message, statusCode: 400); }
	}
	public sealed record DocumentContent(string Content);
}
