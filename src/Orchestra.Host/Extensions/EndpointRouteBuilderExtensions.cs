using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Orchestra.Host.Api;

namespace Orchestra.Host.Extensions;

/// <summary>
/// Extension methods for mapping Orchestra Host API endpoints.
/// </summary>
public static class EndpointRouteBuilderExtensions
{
	/// <summary>
	/// Default JSON serializer options for Orchestra Host APIs.
	/// </summary>
	public static readonly JsonSerializerOptions DefaultJsonOptions = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
		WriteIndented = false
	};

	/// <summary>
	/// Maps all Orchestra Host API endpoints to the application.
	/// This includes orchestration management, triggers, webhooks, execution, runs, and utility endpoints.
	/// </summary>
	/// <param name="endpoints">The endpoint route builder.</param>
	/// <param name="configureJsonOptions">Optional action to configure JSON serialization options.</param>
	/// <returns>The endpoint route builder for chaining.</returns>
	/// <remarks>
	/// This method maps the following endpoint groups:
	/// <list type="bullet">
	///   <item><description>/api/orchestrations - Orchestration CRUD, enable/disable, scan</description></item>
	///   <item><description>/api/triggers - Trigger management, fire</description></item>
	///   <item><description>/api/webhooks - Webhook receivers</description></item>
	///   <item><description>/api/history - Run history</description></item>
	///   <item><description>/api/active - Active executions</description></item>
	///   <item><description>/api/orchestrations/{id}/run - SSE execution streaming</description></item>
	///   <item><description>/api/execution/{id}/attach - SSE attach to running execution</description></item>
	///   <item><description>/api/checkpoints - Checkpoint management and resume</description></item>
	///   <item><description>/api/orchestrations/{id}/resume/{runId} - SSE resume from checkpoint</description></item>
	///   <item><description>/api/orchestrations/{id}/versions - Version history, snapshots, diffs</description></item>
	///   <item><description>/api/profiles - Profile CRUD, activate/deactivate, effective set</description></item>
	///   <item><description>/api/tags - Tag management, orchestration browse/search</description></item>
	///   <item><description>/api/mcps - MCP servers used by orchestrations</description></item>
	///   <item><description>/api/status - Server status</description></item>
	/// </list>
	/// </remarks>
	public static IEndpointRouteBuilder MapOrchestraHostEndpoints(
		this IEndpointRouteBuilder endpoints,
		Action<JsonSerializerOptions>? configureJsonOptions = null)
	{
		var jsonOptions = new JsonSerializerOptions(DefaultJsonOptions);
		configureJsonOptions?.Invoke(jsonOptions);

		// Map all API endpoints
		endpoints.MapOrchestrationsApi(jsonOptions);
		endpoints.MapTriggersApi(jsonOptions);
		endpoints.MapWebhooksApi(jsonOptions);
		endpoints.MapRunsApi(jsonOptions);
		endpoints.MapExecutionApi(jsonOptions);
		endpoints.MapRetryApi(jsonOptions);
		endpoints.MapCheckpointApi(jsonOptions);
		endpoints.MapVersionsApi(jsonOptions);
		endpoints.MapProfilesApi(jsonOptions);
		endpoints.MapTagsApi(jsonOptions);
		endpoints.MapUtilityApi(jsonOptions);
		endpoints.MapDashboardEventsApi(jsonOptions);

		return endpoints;
	}

	/// <summary>
	/// Maps only the orchestration management endpoints.
	/// </summary>
	public static IEndpointRouteBuilder MapOrchestrationsEndpoints(
		this IEndpointRouteBuilder endpoints,
		JsonSerializerOptions? jsonOptions = null)
	{
		return endpoints.MapOrchestrationsApi(jsonOptions ?? DefaultJsonOptions);
	}

	/// <summary>
	/// Maps only the trigger management endpoints.
	/// </summary>
	public static IEndpointRouteBuilder MapTriggersEndpoints(
		this IEndpointRouteBuilder endpoints,
		JsonSerializerOptions? jsonOptions = null)
	{
		return endpoints.MapTriggersApi(jsonOptions ?? DefaultJsonOptions);
	}

	/// <summary>
	/// Maps only the webhook receiver endpoints.
	/// </summary>
	public static IEndpointRouteBuilder MapWebhooksEndpoints(
		this IEndpointRouteBuilder endpoints,
		JsonSerializerOptions? jsonOptions = null)
	{
		return endpoints.MapWebhooksApi(jsonOptions ?? DefaultJsonOptions);
	}

	/// <summary>
	/// Maps only the runs (history and active) endpoints.
	/// </summary>
	public static IEndpointRouteBuilder MapRunsEndpoints(
		this IEndpointRouteBuilder endpoints,
		JsonSerializerOptions? jsonOptions = null)
	{
		return endpoints.MapRunsApi(jsonOptions ?? DefaultJsonOptions);
	}

	/// <summary>
	/// Maps only the execution streaming (SSE) endpoints.
	/// </summary>
	public static IEndpointRouteBuilder MapExecutionEndpoints(
		this IEndpointRouteBuilder endpoints,
		JsonSerializerOptions? jsonOptions = null)
	{
		return endpoints.MapExecutionApi(jsonOptions ?? DefaultJsonOptions);
	}

	/// <summary>
	/// Maps only the utility endpoints (mcps, status, health, config).
	/// </summary>
	public static IEndpointRouteBuilder MapUtilityEndpoints(
		this IEndpointRouteBuilder endpoints,
		JsonSerializerOptions? jsonOptions = null)
	{
		return endpoints.MapUtilityApi(jsonOptions ?? DefaultJsonOptions);
	}

	/// <summary>
	/// Maps only the checkpoint and resume endpoints.
	/// </summary>
	public static IEndpointRouteBuilder MapCheckpointEndpoints(
		this IEndpointRouteBuilder endpoints,
		JsonSerializerOptions? jsonOptions = null)
	{
		return endpoints.MapCheckpointApi(jsonOptions ?? DefaultJsonOptions);
	}

	/// <summary>
	/// Maps only the orchestration versioning endpoints (history, snapshots, diffs).
	/// </summary>
	public static IEndpointRouteBuilder MapVersionsEndpoints(
		this IEndpointRouteBuilder endpoints,
		JsonSerializerOptions? jsonOptions = null)
	{
		return endpoints.MapVersionsApi(jsonOptions ?? DefaultJsonOptions);
	}

	/// <summary>
	/// Maps only the profile management endpoints.
	/// </summary>
	public static IEndpointRouteBuilder MapProfilesEndpoints(
		this IEndpointRouteBuilder endpoints,
		JsonSerializerOptions? jsonOptions = null)
	{
		return endpoints.MapProfilesApi(jsonOptions ?? DefaultJsonOptions);
	}

	/// <summary>
	/// Maps only the tag management and orchestration browse endpoints.
	/// </summary>
	public static IEndpointRouteBuilder MapTagsEndpoints(
		this IEndpointRouteBuilder endpoints,
		JsonSerializerOptions? jsonOptions = null)
	{
		return endpoints.MapTagsApi(jsonOptions ?? DefaultJsonOptions);
	}
}
