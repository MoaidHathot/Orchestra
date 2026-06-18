using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orchestra.Engine;
using Orchestra.Host.Extensions;
using Orchestra.Host.Registry;
using Xunit;

namespace Orchestra.Host.Tests;

/// <summary>
/// Tests for <see cref="Orchestra.Host.Hosting.OrchestrationHostOptions.StartExternalServices"/> and
/// its handling in <c>InitializeOrchestraHostAsync</c>. The read-only management host
/// (StartExternalServices = false) must still load the global MCP <em>definitions</em> from
/// <c>orchestra.mcp.json</c> — so orchestrations that reference global MCPs parse and list — while
/// never <em>starting</em> the MCP proxies or <c>orchestra.services.json</c> processes (the slow,
/// side-effecting part). The reproducible skip-services path keeps loading nothing.
/// </summary>
public class StartExternalServicesTests : IDisposable
{
	private readonly string _tempDir;
	private readonly string _dataPath;
	private readonly Dictionary<string, string?> _savedEnvVars = new();

	public StartExternalServicesTests()
	{
		_tempDir = Path.Combine(Path.GetTempPath(), $"orchestra-start-external-{Guid.NewGuid():N}");
		_dataPath = Path.Combine(_tempDir, "data");
		Directory.CreateDirectory(_dataPath);

		SaveAndClear("ORCHESTRA_CONFIG_PATH");
		SaveAndClear("XDG_CONFIG_HOME");

		// Plant orchestra.json + a co-located orchestra.mcp.json defining one global MCP, and point
		// config discovery at it. ResolveGlobalMcpPath finds orchestra.mcp.json next to orchestra.json.
		var configDir = Path.Combine(_tempDir, "config");
		Directory.CreateDirectory(configDir);
		var configPath = Path.Combine(configDir, "orchestra.json");
		File.WriteAllText(configPath, "{}");
		File.WriteAllText(Path.Combine(configDir, "orchestra.mcp.json"),
			/*lang=json,strict*/ """{ "mcps": [ { "name": "foo", "type": "remote", "endpoint": "http://127.0.0.1:1/mcp" } ] }""");
		Environment.SetEnvironmentVariable("ORCHESTRA_CONFIG_PATH", configPath);
	}

	public void Dispose()
	{
		foreach (var kv in _savedEnvVars)
			Environment.SetEnvironmentVariable(kv.Key, kv.Value);

		if (Directory.Exists(_tempDir))
		{
			try { Directory.Delete(_tempDir, recursive: true); }
			catch { /* best-effort */ }
		}
	}

	private void SaveAndClear(string name)
	{
		_savedEnvVars[name] = Environment.GetEnvironmentVariable(name);
		Environment.SetEnvironmentVariable(name, null);
	}

	private async Task<Engine.Mcp[]> InitAndGetGlobalMcpsAsync(bool startExternalServices, bool skipServices)
	{
		var builder = WebApplication.CreateBuilder();
		builder.Logging.SetMinimumLevel(LogLevel.Warning);
		if (skipServices)
		{
			builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["skip-services"] = "true" });
		}

		builder.Services.AddOrchestraHost(o =>
		{
			o.DataPath = _dataPath;
			o.StartExternalServices = startExternalServices;
			o.EnableScheduler = false;
			o.AutoResumeCheckpointsOnStartup = false;
		}, loadConfigurationFile: false);
		// The host DI graph needs an AgentBuilder for the fallback IAgentProviderRegistry; it is never
		// invoked here (we only initialize + inspect the registry, never run an orchestration).
		builder.Services.AddSingleton<AgentBuilder>(new StubAgentBuilder());

		var app = builder.Build();
		try
		{
			await app.Services.InitializeOrchestraHostAsync();
			return app.Services.GetRequiredService<OrchestrationRegistry>().GlobalMcps;
		}
		finally
		{
			await app.DisposeAsync();
		}
	}

	[Fact]
	public async Task StartExternalServicesFalse_StillLoadsGlobalMcpDefinitions()
	{
		// The management host loads MCP definitions for correct parsing, without starting anything.
		var globalMcps = await InitAndGetGlobalMcpsAsync(startExternalServices: false, skipServices: false);

		globalMcps.Should().ContainSingle(m => m.Name == "foo");
	}

	[Fact]
	public async Task StartExternalServicesTrue_LoadsGlobalMcpDefinitions()
	{
		// The normal path loads definitions too (and would also start them — not asserted here).
		var globalMcps = await InitAndGetGlobalMcpsAsync(startExternalServices: true, skipServices: false);

		globalMcps.Should().ContainSingle(m => m.Name == "foo");
	}

	[Fact]
	public async Task SkipServices_LoadsNoGlobalMcpDefinitions()
	{
		// The reproducible skip-services path loads nothing — unchanged by the management-host work.
		var globalMcps = await InitAndGetGlobalMcpsAsync(startExternalServices: true, skipServices: true);

		globalMcps.Should().BeEmpty();
	}

	private sealed class StubAgentBuilder : AgentBuilder
	{
		public override Task<IAgent> BuildAgentAsync(CancellationToken cancellationToken = default)
			=> throw new NotSupportedException("management host never builds an agent");

		public override Task<IAgent> BuildAgentAsync(AgentBuildConfig config, CancellationToken cancellationToken = default)
			=> throw new NotSupportedException("management host never builds an agent");

		public override AgentProviderCapabilities GetCapabilities() => AgentProviderCapabilities.All("stub");
	}
}
