using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Orchestra.Engine;
using Orchestra.Host.Api;
using Orchestra.Host.Extensions;
using Orchestra.Host.Hosting;
using Orchestra.Host.Registry;
using Orchestra.Host.Triggers;
using Xunit;

namespace Orchestra.Host.Tests;

/// <summary>
/// <c>GET /api/health</c> is a real readiness probe: 200 while the host can persist and run
/// orchestrations (possibly degraded), 503 when the data path is not writable.
/// </summary>
public class HealthCheckTests : IDisposable
{
	private readonly string _tempDir;

	public HealthCheckTests()
	{
		_tempDir = Path.Combine(Path.GetTempPath(), $"orchestra-health-tests-{Guid.NewGuid():N}");
		Directory.CreateDirectory(_tempDir);
	}

	public void Dispose()
	{
		if (Directory.Exists(_tempDir))
		{
			try { Directory.Delete(_tempDir, recursive: true); }
			catch { /* best-effort cleanup */ }
		}
		GC.SuppressFinalize(this);
	}

	private static IAgentProviderRegistry Providers(params string[] names)
	{
		var registry = Substitute.For<IAgentProviderRegistry>();
		registry.ProviderNames.Returns(names);
		registry.DefaultProviderName.Returns(names.FirstOrDefault() ?? string.Empty);
		registry.Builders.Returns([]);
		return registry;
	}

	#region HealthReport

	[Fact]
	public void Build_EverythingInPlace_IsHealthy_AndListsEveryCheck()
	{
		var options = new OrchestrationHostOptions { DataPath = _tempDir };

		var report = HealthReport.Build(options, Providers("copilot", "opencode"), new OrchestrationRegistry(), activeRuns: 2, telemetry: new TelemetryOptions { Enabled = true, OtlpEndpoint = "http://localhost:4317" });

		report.Status.Should().Be(HealthReport.Healthy);
		report.ActiveRuns.Should().Be(2);
		report.Version.Should().Be(UtilityApi.ProductVersion);
		report.Timestamp.Offset.Should().Be(TimeSpan.Zero);
		report.Checks.Select(c => c.Name).Should().Equal("dataPath", "agentProviders", "orchestrations", "scheduler", "fileLogging", "telemetry");
		report.Checks.Should().OnlyContain(c => c.Status == HealthReport.Healthy);
		report.Checks.Single(c => c.Name == "agentProviders").Detail.Should().Contain("copilot, opencode").And.Contain("default: copilot");
		report.Checks.Single(c => c.Name == "telemetry").Detail.Should().Contain("http://localhost:4317");
	}

	[Fact]
	public void Build_NoAgentProvider_IsDegraded()
	{
		var options = new OrchestrationHostOptions { DataPath = _tempDir };

		var report = HealthReport.Build(options, Providers(), new OrchestrationRegistry(), 0, telemetry: null);

		report.Status.Should().Be(HealthReport.Degraded);
		report.Checks.Single(c => c.Name == "agentProviders").Status.Should().Be(HealthReport.Degraded);
		report.Checks.Single(c => c.Name == "telemetry").Detail.Should().StartWith("off");
	}

	[Fact]
	public void Build_SchedulerDisabled_IsDegraded_NotUnhealthy()
	{
		var options = new OrchestrationHostOptions { DataPath = _tempDir, EnableScheduler = false };

		var report = HealthReport.Build(options, Providers("copilot"), new OrchestrationRegistry(), 0, null);

		report.Status.Should().Be(HealthReport.Degraded);
		report.Checks.Single(c => c.Name == "scheduler").Detail.Should().Contain("disabled");
	}

	[Fact]
	public void Build_UnwritableDataPath_IsUnhealthy()
	{
		// A regular file where the directory should be: CreateDirectory throws IOException.
		var blocker = Path.Combine(_tempDir, "not-a-directory");
		File.WriteAllText(blocker, "x");
		var options = new OrchestrationHostOptions { DataPath = blocker };

		var report = HealthReport.Build(options, Providers("copilot"), new OrchestrationRegistry(), 0, null);

		report.Status.Should().Be(HealthReport.Unhealthy);
		report.Checks.Single(c => c.Name == "dataPath").Status.Should().Be(HealthReport.Unhealthy);
	}

	[Fact]
	public void CheckDataPath_LeavesNoProbeFileBehind()
	{
		HealthReport.CheckDataPath(_tempDir).Status.Should().Be(HealthReport.Healthy);

		Directory.GetFiles(_tempDir).Should().BeEmpty();
	}

	#endregion

	#region Endpoint

	private IHost CreateHost(string dataPath, IAgentProviderRegistry providers)
	{
		var options = new OrchestrationHostOptions { DataPath = dataPath };
		var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
		var triggerManager = new TriggerManager(
			new ConcurrentDictionary<string, CancellationTokenSource>(),
			new ConcurrentDictionary<string, ActiveExecutionInfo>(),
			providers,
			new OrchestrationScheduler(),
			Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance,
			Microsoft.Extensions.Logging.Abstractions.NullLogger<TriggerManager>.Instance,
			_tempDir,
			Substitute.For<IRunStore>(),
			Substitute.For<ICheckpointStore>(),
			Substitute.For<IChildOrchestrationLauncher>());

		var host = new HostBuilder()
			.ConfigureWebHost(webHost =>
			{
				webHost.UseTestServer();
				webHost.ConfigureServices(services =>
				{
					services.AddRouting();
					services.AddSingleton(options);
					services.AddSingleton(providers);
					services.AddSingleton(new OrchestrationRegistry());
					services.AddSingleton(new ConcurrentDictionary<string, CancellationTokenSource>());
					services.AddSingleton(triggerManager);
					services.AddSingleton(new Mcp.McpManager(Microsoft.Extensions.Logging.Abstractions.NullLogger<Mcp.McpManager>.Instance));
				});
				webHost.Configure(app =>
				{
					app.UseRouting();
					app.UseEndpoints(endpoints => endpoints.MapUtilityApi(jsonOptions));
				});
			})
			.Build();

		host.Start();
		return host;
	}

	[Fact]
	public async Task Endpoint_Healthy_Is200_WithChecks()
	{
		using var host = CreateHost(_tempDir, Providers("copilot"));
		var client = host.GetTestClient();

		var response = await client.GetAsync("/api/health");

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		var body = await response.Content.ReadFromJsonAsync<JsonElement>();
		body.GetProperty("status").GetString().Should().Be("healthy");
		body.GetProperty("activeRuns").GetInt32().Should().Be(0);
		body.GetProperty("checks").EnumerateArray().Should().Contain(c => c.GetProperty("name").GetString() == "dataPath");
		body.TryGetProperty("timestamp", out _).Should().BeTrue("existing probes still read timestamp");
	}

	[Fact]
	public async Task Endpoint_UnwritableDataPath_Is503()
	{
		var blocker = Path.Combine(_tempDir, "blocker-file");
		File.WriteAllText(blocker, "x");
		using var host = CreateHost(blocker, Providers("copilot"));
		var client = host.GetTestClient();

		var response = await client.GetAsync("/api/health");

		response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
		var body = await response.Content.ReadFromJsonAsync<JsonElement>();
		body.GetProperty("status").GetString().Should().Be("unhealthy");
	}

	#endregion
}
