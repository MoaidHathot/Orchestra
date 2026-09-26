using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Orchestra.Host.Extensions;
using Orchestra.Host.Hosting;
using Xunit;

namespace Orchestra.Host.Tests;

/// <summary>
/// <see cref="TelemetryOptions.Resolve"/> precedence and <c>AddOrchestraTelemetry</c> gating.
/// </summary>
public class TelemetryOptionsTests
{
	private static Func<string, string?> Env(params (string Name, string? Value)[] values)
		=> name => values.FirstOrDefault(v => v.Name == name).Value;

	[Fact]
	public void Resolve_NothingConfigured_IsDisabled_WithDefaults()
	{
		var options = TelemetryOptions.Resolve(null, Env());

		options.Enabled.Should().BeFalse();
		options.OtlpEndpoint.Should().BeNull();
		options.OtlpProtocol.Should().Be("grpc");
		options.ServiceName.Should().Be("orchestra");
		options.Traces.Should().BeTrue();
		options.Metrics.Should().BeTrue();
	}

	[Fact]
	public void Resolve_EnvironmentEndpoint_EnablesExport()
	{
		var options = TelemetryOptions.Resolve(null, Env(
			("OTEL_EXPORTER_OTLP_ENDPOINT", "http://collector:4318"),
			("OTEL_EXPORTER_OTLP_PROTOCOL", "http/protobuf"),
			("OTEL_SERVICE_NAME", "orchestra-prod")));

		options.Enabled.Should().BeTrue();
		options.OtlpEndpoint.Should().Be("http://collector:4318");
		options.OtlpProtocol.Should().Be("http/protobuf");
		options.ServiceName.Should().Be("orchestra-prod");
	}

	[Fact]
	public void Resolve_ConfigWinsOverEnvironment()
	{
		var config = new TelemetryConfigFile { OtlpEndpoint = "http://from-config:4317", ServiceName = "cfg", Traces = false };

		var options = TelemetryOptions.Resolve(config, Env(("OTEL_EXPORTER_OTLP_ENDPOINT", "http://from-env:4317"), ("OTEL_SERVICE_NAME", "env")));

		options.OtlpEndpoint.Should().Be("http://from-config:4317");
		options.ServiceName.Should().Be("cfg");
		options.Traces.Should().BeFalse();
		options.Metrics.Should().BeTrue();
		options.Enabled.Should().BeTrue();
	}

	[Fact]
	public void Resolve_ExplicitEnabledFalse_OverridesAnEndpoint()
	{
		var options = TelemetryOptions.Resolve(new TelemetryConfigFile { Enabled = false }, Env(("OTEL_EXPORTER_OTLP_ENDPOINT", "http://collector:4317")));

		options.Enabled.Should().BeFalse();
		options.OtlpEndpoint.Should().Be("http://collector:4317", "the endpoint is still reported so /api/health can explain the state");
	}

	[Fact]
	public void AddOrchestraTelemetry_Disabled_RegistersOnlyTheOptions()
	{
		var services = new ServiceCollection();

		services.AddOrchestraTelemetry(new TelemetryOptions { Enabled = false });
		using var provider = services.BuildServiceProvider();

		provider.GetRequiredService<TelemetryOptions>().Enabled.Should().BeFalse();
		provider.GetService<TracerProvider>().Should().BeNull("no SDK when nothing exports");
		provider.GetService<MeterProvider>().Should().BeNull();
	}

	[Fact]
	public void AddOrchestraTelemetry_Enabled_RegistersTracerAndMeterProviders()
	{
		var services = new ServiceCollection();
		services.AddLogging();

		services.AddOrchestraTelemetry(new TelemetryOptions { Enabled = true, OtlpEndpoint = "http://127.0.0.1:1", ServiceName = "test" });
		using var provider = services.BuildServiceProvider();

		provider.GetService<TracerProvider>().Should().NotBeNull();
		provider.GetService<MeterProvider>().Should().NotBeNull();
	}

	[Fact]
	public void AddOrchestraTelemetry_TracesOff_RegistersOnlyMetrics()
	{
		var services = new ServiceCollection();
		services.AddLogging();

		services.AddOrchestraTelemetry(new TelemetryOptions { Enabled = true, OtlpEndpoint = "http://127.0.0.1:1", Traces = false });
		using var provider = services.BuildServiceProvider();

		provider.GetService<TracerProvider>().Should().BeNull();
		provider.GetService<MeterProvider>().Should().NotBeNull();
	}

	[Fact]
	public void ConfigLoader_ParsesTheTelemetryBlock()
	{
		var config = System.Text.Json.JsonSerializer.Deserialize<OrchestraConfigFile>("""
			{ "telemetry": { "otlpEndpoint": "http://localhost:4317", "otlpProtocol": "grpc", "serviceName": "orc", "traces": true, "metrics": false, "enabled": true } }
			""", new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });

		config!.Telemetry.Should().NotBeNull();
		config.Telemetry!.OtlpEndpoint.Should().Be("http://localhost:4317");
		config.Telemetry.Metrics.Should().BeFalse();
		TelemetryOptions.Resolve(config.Telemetry, Env()).Enabled.Should().BeTrue();
	}
}
