using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Orchestra.Engine.Diagnostics;
using Orchestra.Host.Hosting;

namespace Orchestra.Host.Extensions;

/// <summary>
/// Resolved telemetry settings: <c>orchestra.json</c>'s <c>telemetry</c> block layered over the
/// standard <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> / <c>OTEL_SERVICE_NAME</c> environment variables.
/// Registered as a singleton so <c>/api/health</c> can report whether export is on.
/// </summary>
public sealed class TelemetryOptions
{
	public const string DefaultServiceName = "orchestra";

	/// <summary>Whether traces/metrics are exported. Off unless an OTLP endpoint is known.</summary>
	public bool Enabled { get; set; }

	/// <summary>OTLP collector endpoint, e.g. <c>http://localhost:4317</c>.</summary>
	public string? OtlpEndpoint { get; set; }

	/// <summary><c>grpc</c> (default) or <c>http/protobuf</c>.</summary>
	public string OtlpProtocol { get; set; } = "grpc";

	/// <summary><c>service.name</c> resource attribute.</summary>
	public string ServiceName { get; set; } = DefaultServiceName;

	/// <summary>Export <c>orchestra.run</c>/<c>orchestra.step</c> spans plus incoming HTTP requests.</summary>
	public bool Traces { get; set; } = true;

	/// <summary>Export the <c>orchestra.*</c> meter plus ASP.NET Core request metrics.</summary>
	public bool Metrics { get; set; } = true;

	/// <summary>
	/// Builds the effective options: config values win, the OTEL_* environment variables fill
	/// the gaps, and export is enabled when either names an endpoint (unless the config says
	/// <c>enabled: false</c>).
	/// </summary>
	public static TelemetryOptions Resolve(TelemetryConfigFile? config, Func<string, string?>? environment = null)
	{
		environment ??= Environment.GetEnvironmentVariable;

		var endpoint = FirstNonEmpty(config?.OtlpEndpoint, environment("OTEL_EXPORTER_OTLP_ENDPOINT"));
		var protocol = FirstNonEmpty(config?.OtlpProtocol, environment("OTEL_EXPORTER_OTLP_PROTOCOL")) ?? "grpc";
		var serviceName = FirstNonEmpty(config?.ServiceName, environment("OTEL_SERVICE_NAME")) ?? DefaultServiceName;

		return new TelemetryOptions
		{
			OtlpEndpoint = endpoint,
			OtlpProtocol = protocol,
			ServiceName = serviceName,
			Traces = config?.Traces ?? true,
			Metrics = config?.Metrics ?? true,
			Enabled = config?.Enabled ?? endpoint is not null,
		};
	}

	private static string? FirstNonEmpty(params string?[] values)
		=> values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();
}

/// <summary>
/// Wires the OpenTelemetry SDK to the engine's <see cref="OrchestraDiagnostics"/> source and
/// meter (plus ASP.NET Core request instrumentation) and exports over OTLP.
/// </summary>
public static class TelemetryServiceCollectionExtensions
{
	/// <summary>
	/// Adds OpenTelemetry export when <paramref name="options"/> enables it. Without an endpoint
	/// nothing is registered, so the engine's instrumentation stays a no-op and no exporter
	/// retries against a collector that is not there. The resolved options are always
	/// registered so diagnostics endpoints can report the state.
	/// </summary>
	public static IServiceCollection AddOrchestraTelemetry(this IServiceCollection services, TelemetryOptions options)
	{
		ArgumentNullException.ThrowIfNull(options);
		services.AddSingleton(options);

		if (!options.Enabled || string.IsNullOrWhiteSpace(options.OtlpEndpoint))
			return services;

		var endpoint = new Uri(options.OtlpEndpoint);
		var protocol = string.Equals(options.OtlpProtocol, "http/protobuf", StringComparison.OrdinalIgnoreCase)
			? OtlpExportProtocol.HttpProtobuf
			: OtlpExportProtocol.Grpc;

		var builder = services.AddOpenTelemetry()
			.ConfigureResource(resource => resource.AddService(
				serviceName: options.ServiceName,
				serviceVersion: Api.UtilityApi.ProductVersion,
				serviceInstanceId: Environment.MachineName));

		if (options.Traces)
		{
			builder.WithTracing(tracing => tracing
				.AddSource(OrchestraDiagnostics.SourceName)
				.AddAspNetCoreInstrumentation()
				.AddOtlpExporter(exporter =>
				{
					exporter.Endpoint = endpoint;
					exporter.Protocol = protocol;
				}));
		}

		if (options.Metrics)
		{
			builder.WithMetrics(metrics => metrics
				.AddMeter(OrchestraDiagnostics.SourceName)
				.AddAspNetCoreInstrumentation()
				.AddOtlpExporter(exporter =>
				{
					exporter.Endpoint = endpoint;
					exporter.Protocol = protocol;
				}));
		}

		return services;
	}

	/// <summary>Convenience overload: resolves options from the loaded <c>orchestra.json</c> and the environment.</summary>
	public static IServiceCollection AddOrchestraTelemetry(this IServiceCollection services, OrchestraConfigFile? config)
		=> services.AddOrchestraTelemetry(TelemetryOptions.Resolve(config?.Telemetry));
}
