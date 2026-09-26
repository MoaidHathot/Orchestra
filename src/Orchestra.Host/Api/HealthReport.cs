using Orchestra.Engine;
using Orchestra.Host.Extensions;
using Orchestra.Host.Hosting;
using Orchestra.Host.Registry;

namespace Orchestra.Host.Api;

/// <summary>
/// The body of <c>GET /api/health</c>: an overall status plus the individual checks behind it.
/// </summary>
/// <remarks>
/// <see cref="Healthy"/>: everything a run needs is in place. <see cref="Degraded"/>: the host
/// serves requests but cannot do all of its job (no agent provider registered, scheduler off).
/// <see cref="Unhealthy"/>: the data path is not writable, so runs cannot be persisted; the
/// endpoint answers 503 and the CLI's connect-or-spawn probe treats the host as unavailable.
/// </remarks>
public sealed record HealthReport(
	string Status,
	string Version,
	DateTimeOffset Timestamp,
	TimeSpan Uptime,
	int ActiveRuns,
	IReadOnlyList<HealthCheck> Checks)
{
	public const string Healthy = "healthy";
	public const string Degraded = "degraded";
	public const string Unhealthy = "unhealthy";

	private static readonly DateTimeOffset s_startedAt = DateTimeOffset.UtcNow;

	/// <summary>Runs every check and folds them into the overall status.</summary>
	public static HealthReport Build(
		OrchestrationHostOptions options,
		IAgentProviderRegistry providerRegistry,
		OrchestrationRegistry registry,
		int activeRuns,
		TelemetryOptions? telemetry)
	{
		var checks = new List<HealthCheck>
		{
			CheckDataPath(options.DataPath),
			CheckProviders(providerRegistry),
			new("orchestrations", Healthy, $"{registry.Count} registered"),
			new("scheduler", options.EnableScheduler ? Healthy : Degraded, options.EnableScheduler ? "running" : "disabled (enableScheduler=false); triggers will not fire"),
			new("fileLogging", Healthy, options.FileLogging.Enabled
				? $"on: {options.FileLogging.ResolveDirectory(options.DataPath)}"
				: "off"),
			new("telemetry", Healthy, telemetry is { Enabled: true }
				? $"exporting to {telemetry.OtlpEndpoint} ({telemetry.OtlpProtocol})"
				: "off (set telemetry.otlpEndpoint or OTEL_EXPORTER_OTLP_ENDPOINT)"),
		};

		var overall = checks.Any(c => c.Status == Unhealthy) ? Unhealthy
			: checks.Any(c => c.Status == Degraded) ? Degraded
			: Healthy;

		return new HealthReport(overall, UtilityApi.ProductVersion, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow - s_startedAt, activeRuns, checks);
	}

	/// <summary>The data path must exist and accept a write, or nothing about a run can be persisted.</summary>
	internal static HealthCheck CheckDataPath(string dataPath)
	{
		try
		{
			Directory.CreateDirectory(dataPath);
			var probe = Path.Combine(dataPath, $".health-{Guid.NewGuid():N}");
			File.WriteAllText(probe, "ok");
			File.Delete(probe);
			return new HealthCheck("dataPath", Healthy, dataPath);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
		{
			return new HealthCheck("dataPath", Unhealthy, $"{dataPath}: {ex.Message}");
		}
	}

	private static HealthCheck CheckProviders(IAgentProviderRegistry providerRegistry)
	{
		var names = providerRegistry.ProviderNames;
		return names.Count == 0
			? new HealthCheck("agentProviders", Degraded, "no agent provider registered; Prompt steps cannot run")
			: new HealthCheck("agentProviders", Healthy, $"{string.Join(", ", names)} (default: {providerRegistry.DefaultProviderName})");
	}
}

/// <summary>One line of a <see cref="HealthReport"/>.</summary>
public sealed record HealthCheck(string Name, string Status, string Detail);
