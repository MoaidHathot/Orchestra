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
		services.TryAddSingleton<WorkspaceIntegrations>();
		return services;
	}

	public static IEndpointRouteBuilder MapWorkspaceApi(this IEndpointRouteBuilder endpoints)
	{
		var group = endpoints.MapGroup("/api/workspace");
		group.MapGet("", (PortalWorkspaceService workspace) => Results.Ok(workspace.Status()));
		group.MapPost("/dismiss", async (PortalWorkspaceService workspace, CancellationToken token) => { await workspace.DismissAsync(token); return Results.Ok(); });
		group.MapGet("/settings", (WorkspaceSettingsStore settings) => Results.Ok(settings.Get()));
		group.MapGet("/integrations", (WorkspaceIntegrations integrations) => integrations.Get());
		group.MapPost("/integrations", async (IntegrationUpdate update, WorkspaceIntegrations integrations, CancellationToken token) =>
			await Guard(async () => Results.Ok(await integrations.SaveAsync(update, token))));
		group.MapPost("/integrations/{name}/test", async (string name, WorkspaceIntegrations integrations, CancellationToken token) =>
			await Guard(async () => Results.Ok(await integrations.TestAsync(name, token))));
		group.MapGet("/providers", (ProviderSetupJobs jobs) => Results.Ok(jobs.Inspect()));
		group.MapPost("/providers/{provider}/operations", (string provider, ProviderOperation operation, ProviderSetupJobs jobs) =>
		{
			try { return Results.Ok(jobs.Start(provider, operation)); }
			catch (ArgumentException ex) { return Results.Problem(ex.Message, statusCode: 400); }
		});
		group.MapGet("/jobs/{id}", (string id, ProviderSetupJobs jobs) => jobs.Get(id) is { } job ? Results.Ok(job) : Results.NotFound());
		group.MapPost("/jobs/{id}/cancel", (string id, ProviderSetupJobs jobs) => jobs.Cancel(id) ? Results.Ok() : Results.NotFound());
		group.MapPost("/jobs/{id}/code", (string id, CodeSubmission code, ProviderSetupJobs jobs) => jobs.SubmitCode(id, code.Code) ? Results.Ok() : Results.Problem("This job is not waiting for a code.", statusCode: 409));
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
	public sealed record CodeSubmission(string Code);
}
