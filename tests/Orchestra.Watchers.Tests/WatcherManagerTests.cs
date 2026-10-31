using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Orchestra.Engine;
using Orchestra.Watchers.Config;
using Orchestra.Watchers.Models;
using Orchestra.Watchers.Runtime;
using Orchestra.Watchers.State;
using Xunit;

namespace Orchestra.Watchers.Tests;

/// <summary>
/// Integration tests: a real <see cref="WatcherManager"/> over a real <see cref="FileWatcherStateStore"/>
/// in a temp directory, with an in-process fetcher and a recording dispatcher.
/// </summary>
public class WatcherManagerTests : IAsyncDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), $"orchestra-watcher-mgr-{Guid.NewGuid():N}");
	private readonly WatcherSubscriptionRegistry _registry = new();
	private readonly FileWatcherStateStore _store;
	private readonly ScriptedFetcher _fetcher = new();
	private readonly RecordingDispatcher _dispatcher = new();
	private readonly CapturingLoggerFactory _logs = new();
	private readonly WatcherManager _manager;

	public WatcherManagerTests()
	{
		Directory.CreateDirectory(_root);
		_store = new FileWatcherStateStore(Path.Combine(_root, "state"), NullLogger<FileWatcherStateStore>.Instance);
		_manager = new WatcherManager(_registry, _store, _fetcher, new WatcherOutputValidator(), _dispatcher, _logs) { ShutdownTimeout = TimeSpan.FromSeconds(5) };
	}

	public async ValueTask DisposeAsync()
	{
		await _manager.StopAsync(CancellationToken.None);
		try
		{ Directory.Delete(_root, recursive: true); }
		catch { }
	}

	private static WatcherLibrary Library(params WatcherEntry[] entries)
		=> new(entries.ToDictionary(e => e.Name, e => e, StringComparer.Ordinal), sourcePath: "/cfg/orchestra.watchers.json");

	private static WatcherSubscriptionRequest Reference(string triggerId, string watcher, WatcherCatchupMode catchup = WatcherCatchupMode.FromNow, int maxConcurrent = 1)
		=> new(triggerId, triggerId, new WatcherTriggerConfig
		{
			Type = TriggerType.Watcher,
			Watcher = watcher,
			Catchup = catchup,
			MaxConcurrent = maxConcurrent,
			Parameters = new Dictionary<string, string> { ["id"] = "{{item.id}}" },
		}, BaseDirectory: null);

	private static WatcherSubscriptionRequest Inline(string triggerId, string name, string? baseDirectory = null, string json = "")
	{
		var definition = JsonNode.Parse(json.Length > 0 ? json : $$$"""
			{ "name": "{{{name}}}", "type": "poll-command", "schedule": { "intervalSeconds": 3600 }, "fetch": { "command": "noop" }, "dedup": { "key": "{{item.id}}" } }
			""")!.AsObject();
		return new WatcherSubscriptionRequest(triggerId, triggerId, new WatcherTriggerConfig { Type = TriggerType.Watcher, Definition = definition }, baseDirectory);
	}

	private async Task WaitUntilAsync(Func<bool> condition, string what, TimeSpan? timeout = null)
	{
		var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
		while (!condition())
		{
			if (DateTime.UtcNow > deadline)
				throw new TimeoutException($"Timed out waiting for: {what}");
			await Task.Delay(20);
		}
	}

	[Fact]
	public async Task TwoSubscribers_OneFetchPerTick_EachGetsAnIndependentDispatch()
	{
		_manager.SetLibrary(Library(TestEntries.Command("lib", intervalSeconds: 3600)));
		_fetcher.Enqueue(ScriptedFetcher.Item("i1"));
		_manager.Subscribe(Reference("orch-a", "lib"));
		_manager.Subscribe(Reference("orch-b", "lib"));
		_manager.GetLoop("lib").Should().BeNull("loops only run after StartAsync");

		await _manager.StartAsync(CancellationToken.None);
		await _dispatcher.WaitForCountAsync(2);

		_fetcher.Calls.Should().Be(1);
		_dispatcher.Dispatches.Select(d => d.Subscription.OrchestrationId).Should().BeEquivalentTo("orch-a", "orch-b");
		_dispatcher.Dispatches.Should().OnlyContain(d => d.DedupKey == "i1" && d.Parameters["id"] == "i1" && d.Subscription.TriggerId == d.Subscription.OrchestrationId);
		_manager.GetStatus().Should().ContainSingle().Which.Should().Match<WatcherStatus>(s => s.Name == "lib" && s.SubscriberCount == 2 && s.IsRunning && !s.IsFaulted && !s.IsInline);
	}

	[Fact]
	public async Task RemovingOneSubscriber_KeepsTheLoop_RemovingTheLastStopsIt_ReaddingRestartsWithStatePreserved()
	{
		_manager.SetLibrary(Library(TestEntries.Command("lib", intervalSeconds: 1)));
		_fetcher.Fallback(_ => ScriptedFetcher.Success(null, ScriptedFetcher.Item("i1")));
		await _manager.StartAsync(CancellationToken.None);

		_manager.Subscribe(Reference("orch-a", "lib"));
		await _dispatcher.WaitForCountAsync(1);
		// The manager is already running, so the first subscriber can consume i1 before
		// the second subscribes. This lifecycle test explicitly requests that backlog;
		// FromNow is covered separately and correctly skips previously observed items.
		_manager.Subscribe(Reference("orch-b", "lib", WatcherCatchupMode.All));
		await _dispatcher.WaitForCountAsync(2);
		var loop = _manager.GetLoop("lib")!;

		_manager.Unsubscribe("orch-a").Should().BeTrue();
		_manager.GetLoop("lib").Should().BeSameAs(loop, "the watcher keeps running for the remaining subscriber");
		var ticksBefore = loop.TickCount;
		await WaitUntilAsync(() => loop.TickCount > ticksBefore, "another tick for the remaining subscriber");

		_manager.Unsubscribe("orch-b").Should().BeTrue();
		_manager.GetLoop("lib").Should().BeNull("the last subscriber stops the loop");
		_manager.GetStatus().Should().BeEmpty();
		_logs.Logger.Messages(Microsoft.Extensions.Logging.LogLevel.Information).Should().Contain(m => m.Contains("stopped (no subscribers)"));

		var dispatchesBefore = _dispatcher.Count;
		_manager.Subscribe(Reference("orch-b", "lib"));
		var restarted = _manager.GetLoop("lib");
		restarted.Should().NotBeNull().And.NotBeSameAs(loop);
		await WaitUntilAsync(() => restarted!.TickCount >= 2, "the restarted loop completes its first tick");

		_dispatcher.Count.Should().Be(dispatchesBefore, "state files survive the stop/start, so i1 is still seen for orch-b");
		File.Exists(_store.GetSubscriberPath("lib", "orch-b")).Should().BeTrue();
		File.Exists(_store.GetSharedPath("lib")).Should().BeTrue();
	}

	[Fact]
	public async Task Catchup_FromNow_SkipsBacklog_And_All_DispatchesIt()
	{
		_manager.SetLibrary(Library(TestEntries.Command("lib", intervalSeconds: 1)));
		_fetcher.Fallback(_ => ScriptedFetcher.Success(null, ScriptedFetcher.Item("backlog-1"), ScriptedFetcher.Item("backlog-2")));
		await _manager.StartAsync(CancellationToken.None);
		_manager.Subscribe(Reference("seed", "lib"));
		await _dispatcher.WaitForCountAsync(2);

		_manager.Subscribe(Reference("late-from-now", "lib", WatcherCatchupMode.FromNow));
		_manager.Subscribe(Reference("late-all", "lib", WatcherCatchupMode.All));
		await _dispatcher.WaitForCountAsync(4);
		var loop = _manager.GetLoop("lib")!;
		var ticks = loop.TickCount;
		await WaitUntilAsync(() => loop.TickCount >= ticks + 2, "two more ticks");

		_dispatcher.Dispatches.Where(d => d.Subscription.OrchestrationId == "late-all").Select(d => d.DedupKey).Should().BeEquivalentTo("backlog-1", "backlog-2");
		_dispatcher.Dispatches.Where(d => d.Subscription.OrchestrationId == "late-from-now").Should().BeEmpty();
		_dispatcher.Count.Should().Be(4);
	}

	[Fact]
	public void Subscribe_BothWatcherAndDefinition_Fails()
	{
		var request = new WatcherSubscriptionRequest("t", "t", new WatcherTriggerConfig { Type = TriggerType.Watcher, Watcher = "lib", Definition = new JsonObject() }, null);

		var act = () => _manager.Subscribe(request);

		act.Should().Throw<WatcherInitializationException>().Which.Message.Should().Contain("not both");
		_manager.Subscriptions.Should().BeEmpty();
	}

	[Fact]
	public void Subscribe_NeitherWatcherNorDefinition_Fails()
	{
		var request = new WatcherSubscriptionRequest("t", "t", new WatcherTriggerConfig { Type = TriggerType.Watcher }, null);

		var act = () => _manager.Subscribe(request);

		act.Should().Throw<WatcherInitializationException>().Which.Message.Should().Contain("requires 'watcher'").And.Contain("definition");
	}

	[Fact]
	public void Subscribe_UnknownLibraryReference_FailsListingKnownWatchers()
	{
		_manager.SetLibrary(Library(TestEntries.Command("alpha"), TestEntries.Command("beta")));

		var act = () => _manager.Subscribe(Reference("t", "gamma"));

		act.Should().Throw<WatcherInitializationException>().Which.Message.Should().Contain("unknown watcher 'gamma'").And.Contain("alpha, beta");
	}

	[Fact]
	public void Subscribe_UnknownReference_WithoutLibrary_ExplainsNoFileWasLoaded()
	{
		var act = () => _manager.Subscribe(Reference("t", "gamma"));

		act.Should().Throw<WatcherInitializationException>().Which.Message.Should().Contain("no orchestra.watchers.json was loaded");
	}

	[Fact]
	public void Subscribe_InlineNameCollidingWithLibrary_FailsNamingBoth()
	{
		_manager.SetLibrary(Library(TestEntries.Command("shared-name")));

		var act = () => _manager.Subscribe(Inline("t", "shared-name"));

		act.Should().Throw<WatcherInitializationException>().Which.Message
			.Should().Contain("inline watcher 'shared-name'").And.Contain("library watcher 'shared-name'").And.Contain("/cfg/orchestra.watchers.json").And.Contain("Trigger 't'");
	}

	[Fact]
	public void Subscribe_InlineNameCollidingWithAnotherOrchestrationsInline_FailsNamingBoth()
	{
		_manager.Subscribe(Inline("first-orch", "dup"));

		var act = () => _manager.Subscribe(Inline("second-orch", "dup"));

		act.Should().Throw<WatcherInitializationException>().Which.Message.Should().Contain("declared by orchestration 'first-orch'").And.Contain("Trigger 'second-orch'");
		_manager.Subscriptions.Should().ContainKey("first-orch");
	}

	[Fact]
	public void Subscribe_InlineReRegistrationBySameTrigger_ReplacesWithoutCollision()
	{
		_manager.Subscribe(Inline("orch", "mine"));

		var act = () => _manager.Subscribe(Inline("orch", "mine"));

		act.Should().NotThrow();
		_registry.Count("mine").Should().Be(1);
	}

	[Fact]
	public void Subscribe_InlineWithoutName_FailsWithDocumentedMessage()
	{
		var act = () => _manager.Subscribe(Inline("orch", "x", json: """{ "type": "poll-command", "schedule": { "intervalSeconds": 5 }, "fetch": { "command": "noop" } }"""));

		act.Should().Throw<WatcherInitializationException>().Which.Message.Should().Contain("inline watcher definitions require a `name` field");
	}

	[Fact]
	public void Subscribe_InvalidInlineDefinition_FailsWithPath()
	{
		var act = () => _manager.Subscribe(Inline("orch", "x", json: """{ "name": "x", "type": "poll-command", "schedule": { }, "fetch": { "command": "noop" } }"""));

		act.Should().Throw<WatcherInitializationException>().Which.Message.Should().Contain("trigger.definition.schedule");
	}

	[Fact]
	public void Subscribe_DisabledWatcher_Fails()
	{
		_manager.SetLibrary(Library(TestEntries.Command("off", enabled: false)));

		var act = () => _manager.Subscribe(Reference("t", "off"));

		act.Should().Throw<WatcherInitializationException>().Which.Message.Should().Contain("disabled");
	}

	[Fact]
	public void Subscribe_ChangingWatcherForSameTrigger_MovesTheSubscription()
	{
		_manager.SetLibrary(Library(TestEntries.Command("a"), TestEntries.Command("b")));
		_manager.Subscribe(Reference("t", "a"));

		_manager.Subscribe(Reference("t", "b"));

		_registry.Count("a").Should().Be(0);
		_registry.Count("b").Should().Be(1);
		_manager.Subscriptions["t"].WatcherName.Should().Be("b");
	}

	[Fact]
	public async Task Unsubscribe_UnknownTrigger_IsNoOp()
	{
		await _manager.StartAsync(CancellationToken.None);

		_manager.Unsubscribe("nobody").Should().BeFalse();
	}

	[Fact]
	public async Task InlineDefinition_ResolvesRelativeScriptPathAgainstOrchestrationFolder_AndDispatchesOnce()
	{
		// Real pwsh fetcher through the full manager path, mirroring the shipped folder example.
		var orchestrationDir = Path.Combine(_root, "orch");
		var inbox = Path.Combine(orchestrationDir, "inbox");
		Directory.CreateDirectory(inbox);
		File.Copy(TestEntries.Fixture("folder-fetch.ps1"), Path.Combine(orchestrationDir, "fetch.ps1"));
		var marker = Guid.NewGuid().ToString("N");
		await File.WriteAllTextAsync(Path.Combine(inbox, "hello.txt"), marker);

		var manager = new WatcherManager(new WatcherSubscriptionRegistry(), _store, new WatcherFetcherFactory(), new WatcherOutputValidator(), _dispatcher, _logs);
		var definition = JsonNode.Parse("""
			{ "name": "local-folder", "type": "poll-script", "schedule": { "intervalSeconds": 1 },
			  "fetch": { "shell": "pwsh", "scriptFile": "./fetch.ps1", "timeoutSeconds": 30, "config": { "folder": "./inbox" } },
			  "dedup": { "key": "{{item.path}}", "mode": "presence" } }
			""")!.AsObject();
		var config = new WatcherTriggerConfig
		{
			Type = TriggerType.Watcher,
			Definition = definition,
			Parameters = new Dictionary<string, string> { ["path"] = "{{item.path}}", ["name"] = "{{item.name}}" },
		};

		try
		{
			manager.Subscribe(new WatcherSubscriptionRequest("handle-new-file", "handle-new-file", config, orchestrationDir));
			await manager.StartAsync(CancellationToken.None);
			await _dispatcher.WaitForCountAsync(1, TimeSpan.FromSeconds(30));

			var dispatch = _dispatcher.Dispatches.Single();
			dispatch.Parameters["name"].Should().Be("hello.txt");
			Path.IsPathFullyQualified(dispatch.Parameters["path"]).Should().BeTrue();
			(await File.ReadAllTextAsync(dispatch.Parameters["path"])).Should().Be(marker, "pwsh may canonicalize casing or macOS /var symlinks, but must identify the same file");
			dispatch.Watcher.Name.Should().Be("local-folder");

			var loop = manager.GetLoop("local-folder")!;
			var ticks = loop.TickCount;
			await WaitUntilAsync(() => loop.TickCount >= ticks + 1, "a second tick", TimeSpan.FromSeconds(30));
			_dispatcher.Count.Should().Be(1, "the same file does not fire again");

			await File.WriteAllTextAsync(Path.Combine(inbox, "second.txt"), "again");
			await _dispatcher.WaitForCountAsync(2, TimeSpan.FromSeconds(30));
			_dispatcher.Dispatches[1].Parameters["name"].Should().Be("second.txt");
		}
		finally
		{
			await manager.StopAsync(CancellationToken.None);
		}
	}

	[Fact]
	public async Task StopAsync_CancelsLoops_NoFurtherTicks()
	{
		_manager.SetLibrary(Library(TestEntries.Command("lib", intervalSeconds: 1)));
		await _manager.StartAsync(CancellationToken.None);
		_manager.Subscribe(Reference("orch-a", "lib"));
		await WaitUntilAsync(() => _fetcher.Calls >= 1, "first tick");

		await _manager.StopAsync(CancellationToken.None);
		var callsAfterStop = _fetcher.Calls;
		await Task.Delay(1500);

		_fetcher.Calls.Should().Be(callsAfterStop, "no ticks after stop");
		_manager.GetLoop("lib").Should().BeNull();
	}

	[Fact]
	public async Task LoadLibrary_FromFile_UsesTheConfigLoader()
	{
		var path = Path.Combine(_root, "orchestra.watchers.json");
		await File.WriteAllTextAsync(path, """{ "watchers": { "from-file": { "type": "poll-command", "schedule": { "intervalSeconds": 5 }, "fetch": { "command": "x" } } } }""");

		_manager.LoadLibrary(path);

		_manager.Library.Watchers.Should().ContainKey("from-file");
		_manager.Library.SourcePath.Should().Be(path);
		_manager.Library.Watchers["from-file"].BaseDirectory.Should().Be(_root);
	}
}

public class WatcherServiceCollectionExtensionsTests
{
	[Fact]
	public async Task AddOrchestraWatchers_RegistersRuntime_WithDefaultLauncherDispatcher()
	{
		var services = new ServiceCollection();
		services.AddSingleton<Microsoft.Extensions.Logging.ILoggerFactory>(NullLoggerFactory.Instance);
		services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));
		services.AddSingleton<IChildOrchestrationLauncher>(new ThrowingLauncher());
		var root = Path.Combine(Path.GetTempPath(), $"orchestra-watcher-di-{Guid.NewGuid():N}");

		services.AddOrchestraWatchers(_ => root);
		await using var provider = services.BuildServiceProvider();

		var manager = provider.GetRequiredService<WatcherManager>();
		provider.GetRequiredService<IWatcherSubscriptionSink>().Should().BeSameAs(manager);
		provider.GetServices<Microsoft.Extensions.Hosting.IHostedService>().Should().ContainSingle().Which.Should().BeSameAs(manager);
		provider.GetRequiredService<IWatcherDispatcher>().Should().BeOfType<LauncherWatcherDispatcher>();
		provider.GetRequiredService<IWatcherStateStore>().Should().BeOfType<FileWatcherStateStore>().Which.RootDirectory.Should().Be(root);
		provider.GetRequiredService<IWatcherFetcherFactory>().Should().BeOfType<WatcherFetcherFactory>();
	}

	private sealed class ThrowingLauncher : IChildOrchestrationLauncher
	{
		public Task<ChildOrchestrationHandle> LaunchAsync(ChildLaunchRequest request, CancellationToken cancellationToken = default)
			=> throw new NotSupportedException();
	}
}
