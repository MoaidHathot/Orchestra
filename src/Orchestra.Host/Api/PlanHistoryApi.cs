using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Orchestra.Engine;

namespace Orchestra.Host.Api;

public static class PlanHistoryApi
{
	public static IEndpointRouteBuilder MapPlanHistoryEndpoints(this IEndpointRouteBuilder endpoints, JsonSerializerOptions jsonOptions)
	{
		endpoints.MapGet("/api/execution/{executionId}/plans", async (
			string executionId, string? stepName, long? after, int? limit, HttpContext context) =>
		{
			if (after is < 0 || limit is < 1 or > 1000)
				return Results.Problem("Plan history requires after >= 0 and limit between 1 and 1000.", statusCode: 400);
			if (context.RequestServices.GetService<IRunStore>() is not IPlanHistoryStore plans)
				return Results.Problem("The configured run store does not provide durable plan history.", statusCode: 501);
			try
			{ return Results.Json(await plans.ReadPlansAsync(executionId, stepName, after ?? 0, limit ?? 100, context.RequestAborted), jsonOptions); }
			catch (ArgumentException ex) { return Results.Problem(ex.Message, statusCode: 400); }
			catch (InvalidOperationException ex) { return Results.Problem(ex.Message, statusCode: 409); }
		});
		return endpoints;
	}
}
