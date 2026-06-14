using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orchestra.Engine;
using Orchestra.Host.Extensions;
using Orchestra.Host.Hosting;
using Orchestra.Host.McpServer;

namespace Orchestra.Exec.Tests;

/// <summary>
/// A real, persistent Orchestra host listening on a loopback port — stands in for an
/// "already-running" Orchestra instance that <c>orchestra-exec --mode existing|auto</c>
/// connects to. Unlike the exec-spawned host, this one is NOT torn down by exec, so tests can
/// assert it is still healthy afterward and inspect its registry/tag state in-process.
/// </summary>
internal sealed class TestServerHost : IAsyncDisposable
{
	private readonly WebApplication _app;

	public string Url { get; }
	public IServiceProvider Services => _app.Services;

	private TestServerHost(WebApplication app, string url)
	{
		_app = app;
		Url = url;
	}

	public static async Task<TestServerHost> StartAsync(AgentBuilder agent, string dataPath)
	{
		var builder = WebApplication.CreateBuilder();
		builder.Logging.SetMinimumLevel(LogLevel.Warning);
		builder.WebHost.UseUrls("http://127.0.0.1:0");
		builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["skip-services"] = "true" });

		builder.Services.AddOrchestraHost(o =>
		{
			o.DataPath = dataPath;
			// Not exercising the scheduler here; runs are triggered manually via /run.
			o.EnableScheduler = false;
		}, loadConfigurationFile: false);
		builder.Services.AddSingleton(agent);
		builder.Services.AddOrchestraMcpServer();

		var app = builder.Build();
		await app.Services.InitializeOrchestraHostAsync();
		app.UseOrchestraHostProblemDetails();
		app.MapOrchestraHostEndpoints();
		app.MapOrchestraMcpEndpoints();
		await app.StartAsync();

		var url = app.Services.GetRequiredService<IServer>()
			.Features.Get<IServerAddressesFeature>()!
			.Addresses.First();

		return new TestServerHost(app, url);
	}

	public async ValueTask DisposeAsync()
	{
		try
		{
			using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
			await _app.StopAsync(cts.Token);
		}
		catch { /* best-effort */ }
		await _app.DisposeAsync();
	}
}
