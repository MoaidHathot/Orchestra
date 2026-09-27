using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orchestra.Engine;
using Orchestra.Host.Extensions;
using Orchestra.Host.Triggers;
using Orchestra.ProcessHost;
using Orchestra.Watchers.Runtime;
using Orchestra.Watchers.State;
using Xunit;

namespace Orchestra.Host.Tests;

/// <summary>
/// Host wiring for the watcher runtime: DI registrations, the state directory sharing the
/// <see cref="ProcessTracker"/> root, and the <c>orchestra.watchers.json</c> load rules in
/// <c>InitializeOrchestraHostAsync</c> (loaded next to the effective orchestra.json; skipped in the
/// Testing environment or with <c>skip-watchers=true</c>).
/// </summary>
[Collection(HostConfigEnvironmentCollection.Name)]
public class WatcherHostWiringTests : IDisposable
{
	private readonly string _tempDir;
	private readonly string _dataPath;
	private readonly string _configDir;
	private readonly Dictionary<string, string?> _savedEnvVars = new();

	public WatcherHostWiringTests()
	{
		_tempDir = Path.Combine(Path.GetTempPath(), $"orchestra-watcher-wiring-{Guid.NewGuid():N}");
		_dataPath = Path.Combine(_tempDir, "data");
		_configDir = Path.Combine(_tempDir, "config");
		Directory.CreateDirectory(_dataPath);
		Directory.CreateDirectory(_configDir);

		SaveAndClear("ORCHESTRA_CONFIG_PATH");
		SaveAndClear("XDG_CONFIG_HOME");

		var configPath = Path.Combine(_configDir, "orchestra.json");
		File.WriteAllText(configPath, "{}");
		File.WriteAllText(Path.Combine(_configDir, "orchestra.watchers.json"), """
			{ "watchers": { "from-sidecar": { "type": "poll-command", "schedule": { "intervalSeconds": 3600 }, "fetch": { "command": "noop" } } } }
			""");
		Environment.SetEnvironmentVariable("ORCHESTRA_CONFIG_PATH", configPath);
	}

	public void Dispose()
	{
		foreach (var kv in _savedEnvVars)
			Environment.SetEnvironmentVariable(kv.Key, kv.Value);

		try { Directory.Delete(_tempDir, recursive: true); } catch { }
	}

	private void SaveAndClear(string name)
	{
		_savedEnvVars[name] = Environment.GetEnvironmentVariable(name);
		Environment.SetEnvironmentVariable(name, null);
	}

	private WebApplication BuildHost(string? environment = null, bool skipWatchers = false)
	{
		var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = environment ?? Environments.Production });
		builder.Logging.SetMinimumLevel(LogLevel.Warning);
		var settings = new Dictionary<string, string?> { ["skip-services"] = "true" };
		if (skipWatchers)
			settings["skip-watchers"] = "true";
		builder.Configuration.AddInMemoryCollection(settings);

		builder.Services.AddOrchestraHost(o =>
		{
			o.DataPath = _dataPath;
			o.StartExternalServices = false;
			o.EnableScheduler = false;
			o.AutoResumeCheckpointsOnStartup = false;
		}, loadConfigurationFile: false);
		builder.Services.AddSingleton<AgentBuilder>(new StubAgentBuilder());
		return builder.Build();
	}

	[Fact]
	public async Task Di_RegistersWatcherRuntime_RoutedThroughTriggerManager_WithStateBesideThePidFile()
	{
		var app = BuildHost();
		try
		{
			var manager = app.Services.GetRequiredService<WatcherManager>();
			app.Services.GetRequiredService<IWatcherSubscriptionSink>().Should().BeSameAs(manager);
			app.Services.GetServices<IHostedService>().Should().Contain(manager);
			app.Services.GetRequiredService<IWatcherDispatcher>().Should().BeOfType<TriggerManagerWatcherDispatcher>();
			app.Services.GetRequiredService<TriggerManager>().WatcherSink.Should().BeSameAs(manager);

			var pidFile = app.Services.GetRequiredService<ProcessTracker>().PidFilePath;
			var store = app.Services.GetRequiredService<IWatcherStateStore>().Should().BeOfType<FileWatcherStateStore>().Subject;
			store.RootDirectory.Should().Be(FileWatcherStateStore.ResolveStateDirectory(Path.GetDirectoryName(pidFile)!));
			store.RootDirectory.Should().Be(Path.Combine(Path.GetFullPath(_configDir), ".orchestra", "watchers"));
		}
		finally
		{
			await app.DisposeAsync();
		}
	}

	[Fact]
	public async Task Initialize_LoadsTheWatcherSidecar_BesideTheEffectiveConfig()
	{
		var app = BuildHost();
		try
		{
			await app.Services.InitializeOrchestraHostAsync();

			var manager = app.Services.GetRequiredService<WatcherManager>();
			manager.Library.Watchers.Should().ContainKey("from-sidecar");
			manager.Library.SourcePath.Should().Be(Path.Combine(_configDir, "orchestra.watchers.json"));
		}
		finally
		{
			await app.DisposeAsync();
		}
	}

	[Fact]
	public async Task Initialize_TestingEnvironment_SkipsTheLibrary()
	{
		var app = BuildHost(environment: "Testing");
		try
		{
			await app.Services.InitializeOrchestraHostAsync();

			app.Services.GetRequiredService<WatcherManager>().Library.Watchers.Should().BeEmpty();
		}
		finally
		{
			await app.DisposeAsync();
		}
	}

	[Fact]
	public async Task Initialize_SkipWatchersFlag_SkipsTheLibrary()
	{
		var app = BuildHost(skipWatchers: true);
		try
		{
			await app.Services.InitializeOrchestraHostAsync();

			app.Services.GetRequiredService<WatcherManager>().Library.Watchers.Should().BeEmpty();
		}
		finally
		{
			await app.DisposeAsync();
		}
	}

	[Fact]
	public async Task Initialize_BrokenLibrary_LogsAndKeepsTheHostStarting()
	{
		File.WriteAllText(Path.Combine(_configDir, "orchestra.watchers.json"), """{ "watchers": { "bad": { "type": "poll-command", "schedule": { }, "fetch": { "command": "x" } } } }""");
		var app = BuildHost();
		try
		{
			var act = () => app.Services.InitializeOrchestraHostAsync();

			await act.Should().NotThrowAsync();
			app.Services.GetRequiredService<WatcherManager>().Library.Watchers.Should().BeEmpty();
		}
		finally
		{
			await app.DisposeAsync();
		}
	}

	private sealed class StubAgentBuilder : AgentBuilder
	{
		public override Task<IAgent> BuildAgentAsync(CancellationToken cancellationToken = default)
			=> throw new NotSupportedException("never builds an agent");

		public override Task<IAgent> BuildAgentAsync(AgentBuildConfig config, CancellationToken cancellationToken = default)
			=> throw new NotSupportedException("never builds an agent");

		public override AgentProviderCapabilities GetCapabilities() => AgentProviderCapabilities.All("stub");
	}
}
