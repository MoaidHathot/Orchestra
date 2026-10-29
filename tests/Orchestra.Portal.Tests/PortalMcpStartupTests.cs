using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using Orchestra.Host.Mcp;
using Orchestra.ProcessHost;
using Xunit;
using McpServerOptions = Orchestra.Host.McpServer.McpServerOptions;

namespace Orchestra.Portal.Tests;

[Collection(PortalHostEnvironmentCollection.Name)]
public class PortalMcpStartupTests
{
	[Fact]
	public async Task Portal_WithUnavailableBackend_FailsStartupAndStopsManagedServices()
	{
		var backendRequests = 0;
		var serviceWasReady = false;
		PortalWithBackendsFactory? factory = null;
		var builder = WebApplication.CreateSlimBuilder();
		builder.Logging.ClearProviders();
		builder.WebHost.UseUrls("http://127.0.0.1:0");
		await using var backend = builder.Build();
		backend.MapPost("/mcp", () =>
		{
			Interlocked.Increment(ref backendRequests);
			serviceWasReady = factory?.ServiceManager?.Processes.Values.Any(p => p.State == ProcessState.Ready) == true;
			return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
		});
		await backend.StartAsync();

		try
		{
			using var configuredFactory = new PortalWithBackendsFactory($"{backend.Urls.Single()}/mcp", startService: true);
			factory = configuredFactory;
			var start = () => factory.CreateClient();
			start.Should().Throw<McpInitializationException>().WithMessage("*calendar*503*");

			Volatile.Read(ref backendRequests).Should().BeGreaterThan(0);
			serviceWasReady.Should().BeTrue("the managed service must finish readiness before MCP validation");
			factory.Manager.Should().NotBeNull();
			factory.Manager!.IsRunning.Should().BeFalse();
			factory.ServiceManager!.Processes.Should().BeEmpty("failed startup must stop the service it already started");
		}
		finally
		{
			await backend.StopAsync();
		}
	}

	[Fact]
	public async Task Portal_WithHealthyBackend_ValidatesBeforeServingAndDisposesProxy()
	{
		var backendRequests = 0;
		var builder = WebApplication.CreateSlimBuilder();
		builder.Logging.ClearProviders();
		builder.WebHost.UseUrls("http://127.0.0.1:0");
		builder.Services.AddMcpServer().WithHttpTransport().WithTools([
			McpServerTool.Create((Func<string>)(() => "ready"), new McpServerToolCreateOptions { Name = "echo" }),
		]);
		await using var backend = builder.Build();
		backend.Use(async (context, next) =>
		{
			Interlocked.Increment(ref backendRequests);
			await next(context);
		});
		backend.MapMcp("/mcp");
		await backend.StartAsync();

		try
		{
			using var factory = new PortalWithBackendsFactory($"{backend.Urls.Single()}/mcp");
			using var client = factory.CreateClient();
			Volatile.Read(ref backendRequests).Should().BeGreaterThan(0, "validation must happen before the first portal request");
			using var status = await client.GetAsync("/api/status");
			using var health = await client.GetAsync("/api/health");
			status.StatusCode.Should().Be(HttpStatusCode.OK);
			health.StatusCode.Should().Be(HttpStatusCode.OK);
			factory.Services.GetRequiredService<McpServerOptions>().BackendStartupTimeoutSeconds.Should().Be(3,
				"the startup budget must be loaded from orchestra.json");
			factory.Manager!.IsRunning.Should().BeTrue();

			await factory.DisposeAsync();
			factory.Manager.IsRunning.Should().BeFalse();
		}
		finally
		{
			await backend.StopAsync();
		}
	}

	private sealed class PortalWithBackendsFactory : PortalWebApplicationFactory
	{
		public McpManager? Manager { get; private set; }
		public ServiceManager? ServiceManager { get; private set; }

		public PortalWithBackendsFactory(string endpoint, bool startService = false)
		{
			File.WriteAllText(Path.Combine(ConfigDirectory, "orchestra.json"), JsonSerializer.Serialize(new
			{
				logLevel = "Warning",
				mcpServer = new { backendStartupTimeoutSeconds = 3 },
			}));
			File.WriteAllText(Path.Combine(ConfigDirectory, "orchestra.mcp.json"), JsonSerializer.Serialize(new
			{
				mcps = new[] { new { name = "calendar", type = "remote", endpoint } },
			}));
			if (startService)
			{
				var windows = OperatingSystem.IsWindows();
				File.WriteAllText(Path.Combine(ConfigDirectory, "orchestra.services.json"), JsonSerializer.Serialize(new
				{
					services = new[]
					{
						new
						{
							name = "test-service",
							type = "process",
							command = windows ? "powershell.exe" : "/bin/sh",
							arguments = windows
								? new[] { "-NoProfile", "-NonInteractive", "-Command", "[Console]::Out.WriteLine('test-service-ready'); [Console]::In.ReadLine() | Out-Null" }
								: new[] { "-c", "printf 'test-service-ready\\n'; read value" },
							required = true,
							forceKill = true,
							readiness = new { stdoutPattern = "test-service-ready", timeoutSeconds = 10 },
						},
					},
				}));
			}
		}

		protected override void ConfigureWebHost(IWebHostBuilder builder)
		{
			base.ConfigureWebHost(builder);
			// Exercise actual sidecar loading; the normal Testing environment skips MCP startup.
			builder.UseEnvironment("Production");
			builder.ConfigureServices(services => services.AddSingleton(sp =>
			{
				ServiceManager = sp.GetRequiredService<ServiceManager>();
				return Manager = new McpManager(sp.GetRequiredService<ILogger<McpManager>>(), sp.GetRequiredService<McpServerOptions>());
			}));
		}
	}
}
