using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Orchestra.Engine;
using Orchestra.Host.Triggers;

namespace Orchestra.Host.Api;

/// <summary>
/// API endpoints for webhook triggers.
/// </summary>
public static partial class WebhooksApi
{
	/// <summary>
	/// Maps webhook endpoints.
	/// </summary>
	public static IEndpointRouteBuilder MapWebhooksApi(this IEndpointRouteBuilder endpoints, JsonSerializerOptions jsonOptions)
	{
		var group = endpoints.MapGroup("/api/webhooks");

		// POST /api/webhooks/{id} - Webhook receiver endpoint for external systems
		group.MapPost("/{id}", async (HttpContext httpContext, string id, TriggerManager triggerManager, ILogger<TriggerManager> logger) =>
		{
			var t = triggerManager.GetTrigger(id);
			if (t == null)
				return ProblemDetailsHelpers.NotFound($"Webhook trigger '{id}' not found.");

			if (t.Config is not WebhookTriggerConfig webhookConfig)
				return ProblemDetailsHelpers.BadRequest($"Trigger '{id}' is not a webhook trigger.");

			// Read raw body bytes (needed for both HMAC validation and JSON parsing)
			httpContext.Request.EnableBuffering();
			using var ms = new MemoryStream();
			await httpContext.Request.Body.CopyToAsync(ms);
			var bodyBytes = ms.ToArray();

			// Validate HMAC signature if a secret is configured
			if (!string.IsNullOrWhiteSpace(webhookConfig.Secret))
			{
				var signatureHeader = httpContext.Request.Headers[WebhookSignatureValidator.SignatureHeaderName].FirstOrDefault();
				if (!WebhookSignatureValidator.Validate(signatureHeader, webhookConfig.Secret, bodyBytes))
					return Results.Unauthorized();
			}

			// Parse parameters from webhook body
			Dictionary<string, string>? webhookParams = null;
			if (bodyBytes.Length > 0)
			{
				try
				{
					var body = System.Text.Encoding.UTF8.GetString(bodyBytes);
					if (!string.IsNullOrWhiteSpace(body))
					{
						webhookParams = JsonSerializer.Deserialize<Dictionary<string, string>>(body, jsonOptions);
					}
				}
				catch (Exception ex)
				{
					logger.LogError(ex, "Failed to parse webhook request body for trigger '{TriggerId}'", id);
				}
			}

			var (found, executionId, orchResult) = await triggerManager.FireWebhookTriggerAsync(id, webhookParams);
			if (!found)
				return ProblemDetailsHelpers.NotFound($"Trigger '{id}' not found.");

			// Synchronous webhook response: return the orchestration result inline
			if (orchResult is not null && webhookConfig.Response is { WaitForResult: true } responseConfig)
			{
				return FormatSyncResponse(orchResult, responseConfig, executionId, id, jsonOptions);
			}

			// Async (fire-and-forget) response
			var accepted = executionId != null;
			return Results.Json(new
			{
				accepted,
				triggerId = id,
				executionId,
				message = accepted ? null : "Trigger is disabled or paused"
			}, jsonOptions);
		});

		// POST /api/webhooks/{id}/validate - Validate webhook signature without firing
		group.MapPost("/{id}/validate", async (HttpContext httpContext, string id, TriggerManager triggerManager) =>
		{
			var t = triggerManager.GetTrigger(id);
			if (t == null)
				return ProblemDetailsHelpers.NotFound($"Webhook trigger '{id}' not found.");

			if (t.Config is not WebhookTriggerConfig webhookConfig)
				return ProblemDetailsHelpers.BadRequest($"Trigger '{id}' is not a webhook trigger.");

			// Validate HMAC signature if a secret is configured
			if (!string.IsNullOrWhiteSpace(webhookConfig.Secret))
			{
				httpContext.Request.EnableBuffering();
				using var ms = new MemoryStream();
				await httpContext.Request.Body.CopyToAsync(ms);
				var bodyBytes = ms.ToArray();

				var signatureHeader = httpContext.Request.Headers[WebhookSignatureValidator.SignatureHeaderName].FirstOrDefault();
				if (!WebhookSignatureValidator.Validate(signatureHeader, webhookConfig.Secret, bodyBytes))
					return Results.Json(new { valid = false, message = "Invalid signature" }, jsonOptions);
			}

			return Results.Json(new
			{
				valid = true,
				triggerId = id,
				enabled = webhookConfig.Enabled,
				hasInputHandler = !string.IsNullOrWhiteSpace(webhookConfig.InputHandlerPrompt),
				synchronous = webhookConfig.Response?.WaitForResult ?? false
			}, jsonOptions);
		});

		return endpoints;
	}

	/// <summary>
	/// Formats a synchronous webhook response, applying a response template if configured.
	/// </summary>
	private static IResult FormatSyncResponse(
		OrchestrationResult result,
		WebhookResponseConfig responseConfig,
		string? executionId,
		string triggerId,
		JsonSerializerOptions jsonOptions)
	{
		// If orchestration failed or was cancelled, return a 502 with error details
		if (result.Status is ExecutionStatus.Failed or ExecutionStatus.Cancelled)
		{
			var errors = result.StepResults
				.Where(kv => kv.Value.Status is ExecutionStatus.Failed or ExecutionStatus.Cancelled)
				.ToDictionary(kv => kv.Key, kv => kv.Value.ErrorMessage ?? "Unknown error");

			return Results.Json(new
			{
				status = result.Status.ToString().ToLowerInvariant(),
				executionId,
				triggerId,
				errors
			}, jsonOptions, statusCode: 502);
		}

		// Apply response template if provided
		if (!string.IsNullOrWhiteSpace(responseConfig.ResponseTemplate))
		{
			var body = ApplyResponseTemplate(responseConfig.ResponseTemplate, result);
			return Results.Content(body, "text/plain");
		}

		// Default: serialize the full result as JSON
		var response = new
		{
			status = result.Status.ToString().ToLowerInvariant(),
			executionId,
			triggerId,
			results = result.Results.ToDictionary(
				kv => kv.Key,
				kv => new
				{
					status = kv.Value.Status.ToString().ToLowerInvariant(),
					content = kv.Value.Content,
					error = kv.Value.ErrorMessage,
					model = kv.Value.ActualModel,
				})
		};

		return Results.Json(response, jsonOptions);
	}

	/// <summary>
	/// Applies a Handlebars-style response template to the orchestration result.
	/// Supports <c>{{stepName.Content}}</c>, <c>{{stepName.Status}}</c>, and <c>{{stepName.Error}}</c> placeholders.
	/// </summary>
	public static string ApplyResponseTemplate(string template, OrchestrationResult result)
	{
		return ResponseTemplatePlaceholder().Replace(template, match =>
		{
			var stepName = match.Groups[1].Value;
			var property = match.Groups[2].Value;

			// Look up step results (all steps), then terminal results
			ExecutionResult? stepResult = null;
			if (result.StepResults.TryGetValue(stepName, out var sr))
				stepResult = sr;
			else if (result.Results.TryGetValue(stepName, out var tr))
				stepResult = tr;

			if (stepResult is null)
				return match.Value; // Leave placeholder as-is if step not found

			return property.ToLowerInvariant() switch
			{
				"content" => stepResult.Content ?? "",
				"status" => stepResult.Status.ToString().ToLowerInvariant(),
				"error" => stepResult.ErrorMessage ?? "",
				"model" => stepResult.ActualModel ?? "",
				_ => match.Value, // Unknown property — leave as-is
			};
		});
	}

	/// <summary>
	/// Matches <c>{{stepName.Property}}</c> placeholders in response templates.
	/// </summary>
	[GeneratedRegex(@"\{\{(\w+)\.(\w+)\}\}")]
	private static partial Regex ResponseTemplatePlaceholder();
}
