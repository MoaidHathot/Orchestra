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
/// API endpoints for utility operations (models, MCPs, status).
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
		// GET /api/models - List available AI models
		endpoints.MapGet("/api/models", () =>
		{
			var models = new[]
			{
				new { id = "gpt-4.1", name = "GPT-4.1", provider = "OpenAI" },
				new { id = "gpt-4.1-mini", name = "GPT-4.1 Mini", provider = "OpenAI" },
				new { id = "gpt-4.1-nano", name = "GPT-4.1 Nano", provider = "OpenAI" },
				new { id = "gpt-4o", name = "GPT-4o", provider = "OpenAI" },
				new { id = "gpt-4o-mini", name = "GPT-4o Mini", provider = "OpenAI" },
				new { id = "o3-mini", name = "o3-mini", provider = "OpenAI" },
				new { id = "o4-mini", name = "o4-mini", provider = "OpenAI" },
				new { id = "claude-sonnet-4", name = "Claude Sonnet 4", provider = "Anthropic" },
				new { id = "claude-opus-4", name = "Claude Opus 4", provider = "Anthropic" },
				new { id = "claude-3.5-sonnet", name = "Claude 3.5 Sonnet", provider = "Anthropic" },
				new { id = "claude-opus-4.5", name = "Claude Opus 4.5", provider = "Anthropic" },
				new { id = "gemini-2.0-flash", name = "Gemini 2.0 Flash", provider = "Google" }
			};
			return Results.Json(new { models }, jsonOptions);
		});

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
