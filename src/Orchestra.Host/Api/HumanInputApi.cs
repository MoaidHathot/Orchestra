using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Orchestra.Engine;

namespace Orchestra.Host.Api;

/// <summary>
/// API endpoints that surface human-in-the-loop pending waits and accept user responses.
/// </summary>
public static class HumanInputApi
{
	private sealed class RespondRequest
	{
		[JsonPropertyName("choice")]
		public string? Choice { get; init; }

		[JsonPropertyName("reply")]
		public string? Reply { get; init; }

		[JsonPropertyName("respondedBy")]
		public string? RespondedBy { get; init; }
	}

	public static IEndpointRouteBuilder MapHumanInputApi(this IEndpointRouteBuilder endpoints, JsonSerializerOptions jsonOptions)
	{
		// GET /api/runs/pending — list all pending input records
		endpoints.MapGet("/api/runs/pending", async (
			IPendingInputStore store,
			string? orchestration) =>
		{
			var records = await store.ListAsync(orchestration);
			return Results.Ok(records.Select(r => new
			{
				orchestrationName = r.OrchestrationName,
				runId = r.RunId,
				stepName = r.StepName,
				kind = r.Kind.ToString(),
				prompt = r.Prompt,
				choices = r.Choices,
				createdAt = r.CreatedAt,
				expiresAt = r.ExpiresAt,
			}));
		});

		// GET /api/orchestrations/{orchestrationName}/runs/{runId}/pending/{stepName}
		// — fetch a single pending record (used by Portal/CLI to show details before responding)
		endpoints.MapGet("/api/orchestrations/{orchestrationName}/runs/{runId}/pending/{stepName}", async (
			string orchestrationName,
			string runId,
			string stepName,
			IPendingInputStore store) =>
		{
			var record = await store.GetAsync(orchestrationName, runId, stepName);
			if (record is null)
			{
				return ProblemDetailsHelpers.NotFound(
					$"No pending input record for orchestration '{orchestrationName}', run '{runId}', step '{stepName}'.");
			}

			return Results.Ok(new
			{
				orchestrationName = record.OrchestrationName,
				runId = record.RunId,
				stepName = record.StepName,
				kind = record.Kind.ToString(),
				prompt = record.Prompt,
				choices = record.Choices,
				createdAt = record.CreatedAt,
				expiresAt = record.ExpiresAt,
			});
		});

		// POST /api/orchestrations/{orchestrationName}/runs/{runId}/respond?step={stepName}
		// Body: { choice?: string, reply?: string, respondedBy?: string }
		endpoints.MapPost("/api/orchestrations/{orchestrationName}/runs/{runId}/respond", async (
			string orchestrationName,
			string runId,
			HttpContext httpContext,
			IPendingInputStore store,
			IHumanInputWaiter waiter,
			string? step) =>
		{
			if (string.IsNullOrEmpty(step))
			{
				return ProblemDetailsHelpers.BadRequest(
					"Missing required query parameter 'step'.");
			}

			RespondRequest? body;
			try
			{
				body = await httpContext.Request.ReadFromJsonAsync<RespondRequest>(jsonOptions);
			}
			catch (JsonException ex)
			{
				return ProblemDetailsHelpers.BadRequest($"Invalid JSON body: {ex.Message}");
			}

			body ??= new RespondRequest();

			if (body.Choice is null && body.Reply is null)
			{
				return ProblemDetailsHelpers.BadRequest(
					"Response must contain 'choice' or 'reply' (or both).");
			}

			var pending = await store.GetAsync(orchestrationName, runId, step);
			if (pending is null)
			{
				return ProblemDetailsHelpers.NotFound(
					$"No pending input record for orchestration '{orchestrationName}', run '{runId}', step '{step}'.");
			}

			// Validate choice when the wait declared a constrained set.
			if (pending.Choices.Length > 0 && body.Choice is not null
				&& !pending.Choices.Any(c => string.Equals(c, body.Choice, StringComparison.OrdinalIgnoreCase)))
			{
				return ProblemDetailsHelpers.BadRequest(
					$"Choice '{body.Choice}' is not one of the allowed values: [{string.Join(", ", pending.Choices)}].");
			}

			var response = new UserInputResponse
			{
				Choice = body.Choice,
				Reply = body.Reply,
				RespondedBy = body.RespondedBy,
				RespondedAt = DateTimeOffset.UtcNow,
			};

			var completed = waiter.TryComplete(orchestrationName, runId, step, response);
			if (!completed)
			{
				return ProblemDetailsHelpers.NotFound(
					$"No active wait found for run '{runId}' step '{step}'. The run may have moved on or the host may have restarted (engine-tool waits don't survive restarts).");
			}

			return Results.Ok(new
			{
				accepted = true,
				orchestrationName,
				runId,
				stepName = step,
				respondedAt = response.RespondedAt,
			});
		});

		return endpoints;
	}
}
