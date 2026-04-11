using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Orchestra.Engine;
using Orchestra.Host.Hosting;
using Orchestra.Host.Mcp;
using Orchestra.Host.Persistence;
using Orchestra.Host.Registry;
using Orchestra.Host.Triggers;

namespace Orchestra.Host.Api;

/// <summary>
/// API endpoints for utility operations (MCPs, status).
/// </summary>
public static class UtilityApi
{
	/// <summary>
	/// Maps utility endpoints.
	/// </summary>
	public static IEndpointRouteBuilder MapUtilityApi(
		this IEndpointRouteBuilder endpoints,
		JsonSerializerOptions jsonOptions)
	{
		// GET /api/status - Server status
		endpoints.MapGet("/api/status", (
			OrchestrationRegistry registry,
			TriggerManager triggerManager,
			ConcurrentDictionary<string, CancellationTokenSource> activeExecutions,
			OrchestrationHostOptions options) =>
		{
			var triggers = triggerManager.GetAllTriggers();
			return Results.Json(new
			{
				status = "running",
				version = "1.0.0",
				orchestrationCount = registry.Count,
				activeTriggers = triggers.Count(t => t.Config.Enabled),
				runningExecutions = activeExecutions.Count,
				dataPath = options.DataPath
			}, jsonOptions);
		});

		// GET /api/health - Health check endpoint
		endpoints.MapGet("/api/health", () =>
		{
			return Results.Ok(new
			{
				status = "healthy",
				timestamp = DateTimeOffset.UtcNow
			});
		});

		// GET /api/mcps - List all globally managed MCPs
		endpoints.MapGet("/api/mcps", (Mcp.McpManager mcpManager) =>
		{
			var mcps = mcpManager.GlobalMcps.Select(m => new
			{
				name = m.Name,
				type = m.Type.ToString(),
				endpoint = (m as RemoteMcp)?.Endpoint,
				command = (m as LocalMcp)?.Command,
				arguments = (m as LocalMcp)?.Arguments,
				workingDirectory = (m as LocalMcp)?.WorkingDirectory,
			}).OrderBy(m => m.name).ToArray();

			return Results.Json(new
			{
				count = mcps.Length,
				proxyRunning = mcpManager.IsRunning,
				mcps
			}, jsonOptions);
		});

		// GET /api/config - Client-facing configuration (polling intervals, etc.)
		endpoints.MapGet("/api/config", (OrchestrationHostOptions options) =>
		{
			return Results.Json(new
			{
				polling = new
				{
					activeExecutionsMs = options.Polling.ActiveExecutionsMs,
					orchestrationsMs = options.Polling.OrchestrationsMs,
					historyMs = options.Polling.HistoryMs,
					serverStatusMs = options.Polling.ServerStatusMs,
				}
			}, jsonOptions);
		});

		return endpoints;
	}
}
