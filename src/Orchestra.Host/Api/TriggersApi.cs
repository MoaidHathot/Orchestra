using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Orchestra.Engine;
using Orchestra.Host.Registry;
using Orchestra.Host.Triggers;

namespace Orchestra.Host.Api;

/// <summary>
/// API endpoints for trigger management.
/// </summary>
public static class TriggersApi
{
	/// <summary>
	/// Maps trigger management endpoints.
	/// </summary>
	public static IEndpointRouteBuilder MapTriggersApi(this IEndpointRouteBuilder endpoints, JsonSerializerOptions jsonOptions)
	{
		var group = endpoints.MapGroup("/api/triggers");

		// GET /api/triggers - List all triggers
		group.MapGet("", (TriggerManager triggerManager) =>
		{
			var triggers = triggerManager.GetAllTriggers().Select(t => new
			{
				id = t.Id,
				orchestrationPath = t.OrchestrationPath,
				orchestrationName = t.OrchestrationName,
				triggerType = t.Config.Type.ToString().ToLowerInvariant(),
				enabled = t.Config.Enabled,
				status = t.Status.ToString(),
				nextFireTime = t.NextFireTime?.ToString("o"),
				lastFireTime = t.LastFireTime?.ToString("o"),
				runCount = t.RunCount,
				lastError = t.LastError,
				webhookUrl = t.Config is WebhookTriggerConfig ? $"/api/webhooks/{t.Id}" : null
			}).ToArray();

			return Results.Json(new { count = triggers.Length, triggers }, jsonOptions);
		});

		// GET /api/triggers/{id} - Get a specific trigger
		group.MapGet("/{id}", (string id, TriggerManager triggerManager) =>
		{
			var trigger = triggerManager.GetTrigger(id);
			if (trigger is null)
				return ProblemDetailsHelpers.NotFound($"Trigger '{id}' not found.");

			return Results.Json(new
			{
				id = trigger.Id,
				orchestrationPath = trigger.OrchestrationPath,
				orchestrationName = trigger.OrchestrationName,
				triggerType = trigger.Config.Type.ToString().ToLowerInvariant(),
				enabled = trigger.Config.Enabled,
				status = trigger.Status.ToString(),
				nextFireTime = trigger.NextFireTime?.ToString("o"),
				lastFireTime = trigger.LastFireTime?.ToString("o"),
				runCount = trigger.RunCount,
				lastError = trigger.LastError,
				lastExecutionId = trigger.LastExecutionId,
				activeExecutionId = trigger.ActiveExecutionId,
				webhookUrl = trigger.Config is WebhookTriggerConfig ? $"/api/webhooks/{trigger.Id}" : null,
				config = FormatTriggerConfig(trigger.Config)
			}, jsonOptions);
		});

		// POST /api/triggers/{id}/enable - Enable a trigger
		group.MapPost("/{id}/enable", (string id, TriggerManager triggerManager) =>
		{
			if (triggerManager.SetTriggerEnabled(id, true))
				return Results.Ok(new { id, enabled = true });
			return ProblemDetailsHelpers.NotFound($"Trigger '{id}' not found.");
		});

		// POST /api/triggers/{id}/disable - Disable a trigger
		group.MapPost("/{id}/disable", (string id, TriggerManager triggerManager) =>
		{
			if (triggerManager.SetTriggerEnabled(id, false))
				return Results.Ok(new { id, enabled = false });
			return ProblemDetailsHelpers.NotFound($"Trigger '{id}' not found.");
		});

		// POST /api/triggers/{id}/fire - Manually fire a trigger
		group.MapPost("/{id}/fire", async (HttpContext httpContext, string id, TriggerManager triggerManager) =>
		{
			// Parse optional parameters
			Dictionary<string, string>? parameters = null;
			try
			{
				if (httpContext.Request.ContentLength > 0)
				{
					var body = await JsonSerializer.DeserializeAsync<JsonElement>(httpContext.Request.Body, jsonOptions);
					if (body.TryGetProperty("parameters", out var paramsEl) && paramsEl.ValueKind == JsonValueKind.Object)
					{
						parameters = new Dictionary<string, string>();
						foreach (var prop in paramsEl.EnumerateObject())
							parameters[prop.Name] = prop.Value.GetString() ?? "";
					}
				}
			}
			catch (JsonException)
			{
				return ProblemDetailsHelpers.BadRequest("Invalid JSON in request body. Expected { \"parameters\": { ... } }.");
			}

			var (found, executionId) = await triggerManager.FireTriggerAsync(id, parameters);
			if (!found)
				return ProblemDetailsHelpers.NotFound($"Trigger '{id}' not found.");

			return Results.Json(new { fired = true, id, executionId }, jsonOptions);
		});

		// DELETE /api/triggers/{id} - Remove a trigger
		group.MapDelete("/{id}", (string id, TriggerManager triggerManager) =>
		{
			if (triggerManager.RemoveTrigger(id))
				return Results.Ok(new { removed = true, id });
			return ProblemDetailsHelpers.NotFound($"Trigger '{id}' not found.");
		});

		return endpoints;
	}

	private static object? FormatTriggerConfig(TriggerConfig config)
	{
		return config switch
		{
			SchedulerTriggerConfig s => new
			{
				type = "scheduler",
				enabled = s.Enabled,
				cron = s.Cron,
				intervalSeconds = s.IntervalSeconds,
				maxRuns = s.MaxRuns,
				inputHandlerPrompt = s.InputHandlerPrompt
			},
			LoopTriggerConfig l => new
			{
				type = "loop",
				enabled = l.Enabled,
				delaySeconds = l.DelaySeconds,
				maxIterations = l.MaxIterations,
				continueOnFailure = l.ContinueOnFailure,
				inputHandlerPrompt = l.InputHandlerPrompt
			},
		WebhookTriggerConfig w => new
		{
			type = "webhook",
			enabled = w.Enabled,
			maxConcurrent = w.MaxConcurrent,
			hasSecret = !string.IsNullOrWhiteSpace(w.Secret),
			hasInputHandler = !string.IsNullOrWhiteSpace(w.InputHandlerPrompt),
			response = w.Response is not null ? new
			{
				waitForResult = w.Response.WaitForResult,
				hasResponseTemplate = !string.IsNullOrWhiteSpace(w.Response.ResponseTemplate),
				timeoutSeconds = w.Response.TimeoutSeconds,
			} : null
		},
			_ => new { type = config.Type.ToString().ToLowerInvariant(), enabled = config.Enabled }
		};
	}
}
