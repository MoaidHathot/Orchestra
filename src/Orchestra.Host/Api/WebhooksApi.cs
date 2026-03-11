using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Orchestra.Engine;
using Orchestra.Host.Triggers;

namespace Orchestra.Host.Api;

/// <summary>
/// API endpoints for webhook triggers.
/// </summary>
public static class WebhooksApi
{
	/// <summary>
	/// Maps webhook endpoints.
	/// </summary>
	public static IEndpointRouteBuilder MapWebhooksApi(this IEndpointRouteBuilder endpoints, JsonSerializerOptions jsonOptions)
	{
		var group = endpoints.MapGroup("/api/webhooks");

		// POST /api/webhooks/{id} - Webhook receiver endpoint for external systems
		group.MapPost("/{id}", async (HttpContext httpContext, string id, TriggerManager triggerManager) =>
		{
			var t = triggerManager.GetTrigger(id);
			if (t == null)
				return Results.NotFound(new { error = $"Webhook trigger '{id}' not found." });

			if (t.Config is not WebhookTriggerConfig webhookConfig)
				return Results.BadRequest(new { error = $"Trigger '{id}' is not a webhook trigger." });

			// Validate webhook secret if configured
			if (!string.IsNullOrWhiteSpace(webhookConfig.Secret))
			{
				var providedSecret = httpContext.Request.Headers["X-Webhook-Secret"].FirstOrDefault()
					?? httpContext.Request.Query["secret"].FirstOrDefault();

				if (providedSecret != webhookConfig.Secret)
					return Results.Unauthorized();
			}

			// Parse parameters from webhook body
			Dictionary<string, string>? webhookParams = null;
			if (httpContext.Request.ContentLength > 0)
			{
				try
				{
					using var reader = new StreamReader(httpContext.Request.Body);
					var body = await reader.ReadToEndAsync();
					if (!string.IsNullOrWhiteSpace(body))
					{
						webhookParams = JsonSerializer.Deserialize<Dictionary<string, string>>(body, jsonOptions);
					}
				}
				catch
				{
					// Best-effort body parsing - continue without params
				}
			}

			var (found, executionId) = await triggerManager.FireWebhookTriggerAsync(id, webhookParams);
			if (!found)
				return Results.NotFound(new { error = $"Trigger '{id}' not found." });

			// If executionId is null, the trigger exists but is disabled or paused
			var accepted = executionId != null;
			return Results.Json(new
			{
				accepted,
				triggerId = id,
				executionId,
				message = accepted ? null : "Trigger is disabled or paused"
			}, jsonOptions);
		});

		// POST /api/webhooks/{id}/validate - Validate webhook secret without firing
		group.MapPost("/{id}/validate", (HttpContext httpContext, string id, TriggerManager triggerManager) =>
		{
			var t = triggerManager.GetTrigger(id);
			if (t == null)
				return Results.NotFound(new { error = $"Webhook trigger '{id}' not found." });

			if (t.Config is not WebhookTriggerConfig webhookConfig)
				return Results.BadRequest(new { error = $"Trigger '{id}' is not a webhook trigger." });

			// Validate webhook secret if configured
			if (!string.IsNullOrWhiteSpace(webhookConfig.Secret))
			{
				var providedSecret = httpContext.Request.Headers["X-Webhook-Secret"].FirstOrDefault()
					?? httpContext.Request.Query["secret"].FirstOrDefault();

				if (providedSecret != webhookConfig.Secret)
					return Results.Json(new { valid = false, message = "Invalid secret" }, jsonOptions);
			}

			return Results.Json(new
			{
				valid = true,
				triggerId = id,
				enabled = webhookConfig.Enabled,
				hasInputHandler = !string.IsNullOrWhiteSpace(webhookConfig.InputHandlerPrompt)
			}, jsonOptions);
		});

		return endpoints;
	}
}
