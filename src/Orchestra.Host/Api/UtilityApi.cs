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
	/// The host's product version, taken from the informational version stamped at build time
	/// (<c>Version.props</c>), with any <c>+commit</c> build-metadata suffix removed. Shared by
	/// <c>/api/status</c> and the MCP server's <c>serverInfo</c> so both agree with
	/// <c>orchestra --version</c> instead of carrying their own hardcoded literal.
	/// </summary>
	public static string ProductVersion { get; } = ResolveProductVersion();

	private static string ResolveProductVersion()
	{
		var assembly = typeof(UtilityApi).Assembly;
		var informational = assembly
			.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), inherit: false)
			.OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
			.FirstOrDefault()?.InformationalVersion;

		if (!string.IsNullOrWhiteSpace(informational))
		{
			var plus = informational.IndexOf('+');
			return plus > 0 ? informational[..plus] : informational;
		}

		return assembly.GetName().Version?.ToString(3) ?? "0.0.0";
	}

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
			IAgentProviderRegistry providerRegistry,
			OrchestrationHostOptions options) =>
		{
			var triggers = triggerManager.GetAllTriggers();
			// Aggregate runtime status across every registered agent provider (copilot,
			// opencode, …); null statuses (providers with no live pools) are omitted.
			var agentRuntimes = providerRegistry.Builders
				.Select(b => b.GetRuntimeStatus())
				.Where(s => s is not null)
				.ToArray();
			return Results.Json(new
			{
				status = "running",
				version = ProductVersion,
				orchestrationCount = registry.Count,
				activeTriggers = triggers.Count(t => t.Config.Enabled),
				runningExecutions = activeExecutions.Count,
				defaultProvider = options.Provider ?? providerRegistry.DefaultProviderName,
				providers = providerRegistry.ProviderNames,
				// Back-compat: keep the singular `agentRuntime` (first provider) alongside the
				// new `agentRuntimes` array so existing clients keep working.
				agentRuntime = agentRuntimes.FirstOrDefault(),
				agentRuntimes,
				dataPath = options.DataPath
			}, jsonOptions);
		});

		// GET /api/health - Liveness + readiness. 200 while the host can do its job (possibly
		// "degraded", e.g. no agent provider), 503 when it cannot (data path unwritable).
		endpoints.MapGet("/api/health", (
			OrchestrationHostOptions options,
			IAgentProviderRegistry providerRegistry,
			OrchestrationRegistry registry,
			ConcurrentDictionary<string, CancellationTokenSource> activeExecutions,
			IServiceProvider services) =>
		{
			var report = HealthReport.Build(options, providerRegistry, registry, activeExecutions.Count, services.GetService(typeof(Extensions.TelemetryOptions)) as Extensions.TelemetryOptions);
			return Results.Json(report, jsonOptions, statusCode: report.Status == HealthReport.Unhealthy ? StatusCodes.Status503ServiceUnavailable : StatusCodes.Status200OK);
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

		// GET /api/config - Client-facing configuration (polling intervals, defaults, etc.)
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
				},
				// The model a client should pre-fill when the author has not chosen one: the
				// host's configured default, else the engine's single fallback constant.
				defaultModel = options.DefaultModel ?? ModelDefaults.Model,
				defaultProvider = options.Provider,
			}, jsonOptions);
		});

		return endpoints;
	}
}
