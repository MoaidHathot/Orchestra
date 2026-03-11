using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Orchestra.Engine;
using Orchestra.Host.Hosting;
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

		// GET /api/mcps - List all MCPs used across orchestrations
		endpoints.MapGet("/api/mcps", (OrchestrationRegistry registry) =>
		{
			// Collect all unique MCPs from all orchestrations
			var mcpUsage = new Dictionary<string, (Mcp Mcp, List<string> UsedBy)>(StringComparer.OrdinalIgnoreCase);

			foreach (var entry in registry.GetAll())
			{
				var orchestrationId = entry.Id;

				// Collect orchestration-level MCPs
				foreach (var mcp in entry.Orchestration.Mcps)
				{
					if (!mcpUsage.TryGetValue(mcp.Name, out var usage))
					{
						usage = (mcp, new List<string>());
						mcpUsage[mcp.Name] = usage;
					}
					if (!usage.UsedBy.Contains(orchestrationId))
						usage.UsedBy.Add(orchestrationId);
				}

				// Collect step-level MCPs
				foreach (var step in entry.Orchestration.Steps.OfType<PromptOrchestrationStep>())
				{
					foreach (var mcp in step.Mcps)
					{
						if (!mcpUsage.TryGetValue(mcp.Name, out var usage))
						{
							usage = (mcp, new List<string>());
							mcpUsage[mcp.Name] = usage;
						}
						if (!usage.UsedBy.Contains(orchestrationId))
							usage.UsedBy.Add(orchestrationId);
					}
				}
			}

			var mcps = mcpUsage.Values.Select(u => new
			{
				name = u.Mcp.Name,
				type = u.Mcp.Type.ToString(),
				endpoint = (u.Mcp as RemoteMcp)?.Endpoint,
				command = (u.Mcp as LocalMcp)?.Command,
				arguments = (u.Mcp as LocalMcp)?.Arguments,
				workingDirectory = (u.Mcp as LocalMcp)?.WorkingDirectory,
				usedByCount = u.UsedBy.Count,
				usedBy = u.UsedBy.ToArray()
			}).OrderBy(m => m.name).ToArray();

			return Results.Json(new { count = mcps.Length, mcps }, jsonOptions);
		});

		return endpoints;
	}
}
