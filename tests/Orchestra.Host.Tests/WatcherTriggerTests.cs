using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Orchestra.Engine;
using Orchestra.Host.Api;
using Orchestra.Host.Triggers;
using Orchestra.Watchers;
using Orchestra.Watchers.Runtime;
using Xunit;

namespace Orchestra.Host.Tests;

/// <summary>
/// How <see cref="TriggerManager"/> treats <see cref="WatcherTriggerConfig"/>: registration turns
/// into a subscription on the watcher sink, disable/remove tear it down, subscription failures are
/// surfaced as an errored trigger instead of crashing registration, and watcher dispatches launch
/// through the normal execution path while returning as soon as the run is launched.
/// </summary>
public class WatcherTriggerTests : IDisposable
{
	private readonly string _tempDir;
	private readonly ConcurrentDictionary<string, CancellationTokenSource> _activeExecutions = new();
	private readonly ConcurrentDictionary<string, ActiveExecutionInfo> _activeExecutionInfos = new();
	private readonly RecordingLauncher _launcher = new();
	private readonly FakeSink _sink = new();

	public WatcherTriggerTests()
	{
		_tempDir = Path.Combine(Path.GetTempPath(), $"orchestra-watcher-trigger-tests-{Guid.NewGuid():N}");
		Directory.CreateDirectory(Path.Combine(_tempDir, "runs"));
	}

	public void Dispose()
	{
		try { Directory.Delete(_tempDir, recursive: true); } catch { }
	}

	private TriggerManager CreateTriggerManager(bool withSink = true)
	{
		var manager = new TriggerManager(
			_activeExecutions,
			_activeExecutionInfos,
			agentBuilder: null!,
			scheduler: null!,
			loggerFactory: NullLoggerFactory.Instance,
			logger: NullLogger<TriggerManager>.Instance,
			runStore: null!,
			checkpointStore: null!,
			launcher: _launcher,
			dataPath: _tempDir);
		if (withSink)
			manager.WatcherSink = _sink;
		return manager;
	}

	private static WatcherTriggerConfig WatcherConfig(bool enabled = true, string watcher = "lib") => new()
	{
		Type = TriggerType.Watcher,
		Enabled = enabled,
		Watcher = watcher,
		MaxConcurrent = 2,
		Parameters = new Dictionary<string, string> { ["path"] = "{{item.path}}" },
	};

	private TriggerRegistration Register(TriggerManager mgr, TriggerConfig config, string name = "handle-new-file")
		=> mgr.RegisterTrigger(
			orchestrationPath: Path.Combine(_tempDir, $"{name}.yaml"),
			config: config,
			orchestrationId: name,
			preloadedOrchestration: new Orchestration { Name = name, Description = name, Steps = [] });

	[Fact]
	public void RegisterTrigger_WatcherConfig_SubscribesWithTriggerIdAndOrchestrationFolder()
	{
		var mgr = CreateTriggerManager();

		var reg = Register(mgr, WatcherConfig());

		reg.Status.Should().Be(TriggerStatus.Waiting);
		reg.LastError.Should().BeNull();
		var request = _sink.Subscriptions.Should().ContainSingle().Subject;
		request.TriggerId.Should().Be("handle-new-file");
		request.OrchestrationId.Should().Be("handle-new-file");
		request.Config.Watcher.Should().Be("lib");
		request.BaseDirectory.Should().Be(Path.GetFullPath(_tempDir), "inline fetch paths resolve against the orchestration's folder");
	}

	[Fact]
	public void RegisterTrigger_DisabledWatcherConfig_DoesNotSubscribe_AndIsPaused()
	{
		var mgr = CreateTriggerManager();

		var reg = Register(mgr, WatcherConfig(enabled: false));

		reg.Status.Should().Be(TriggerStatus.Paused);
		_sink.Subscriptions.Should().BeEmpty();
		_sink.Unsubscribed.Should().Equal("handle-new-file");
	}

	[Fact]
	public void RegisterTrigger_SinkRejects_MarksTriggerErrored_WithoutThrowing()
	{
		_sink.FailWith = new WatcherInitializationException("Trigger 'handle-new-file': unknown watcher 'lib' (known watchers: other).");
		var mgr = CreateTriggerManager();

		var reg = Register(mgr, WatcherConfig());

		reg.Status.Should().Be(TriggerStatus.Error);
		reg.LastError.Should().Contain("unknown watcher 'lib'");
		mgr.GetTrigger("handle-new-file").Should().NotBeNull("the trigger stays visible so the dashboard shows the problem");
	}

	[Fact]
	public void RegisterTrigger_WithoutWatcherRuntime_MarksTriggerErrored()
	{
		var mgr = CreateTriggerManager(withSink: false);

		var reg = Register(mgr, WatcherConfig());

		reg.Status.Should().Be(TriggerStatus.Error);
		reg.LastError.Should().Contain("not available");
	}

	[Fact]
	public void SetTriggerEnabled_TogglesTheSubscription()
	{
		var mgr = CreateTriggerManager();
		var reg = Register(mgr, WatcherConfig());

		mgr.SetTriggerEnabled(reg.Id, false).Should().BeTrue();
		_sink.Unsubscribed.Should().Equal("handle-new-file");
		reg.Status.Should().Be(TriggerStatus.Paused);
		reg.Config.Enabled.Should().BeFalse("CloneTriggerConfigWithEnabled must know the watcher shape");
		reg.Config.Should().BeOfType<WatcherTriggerConfig>().Which.Parameters.Should().ContainKey("path");

		mgr.SetTriggerEnabled(reg.Id, true).Should().BeTrue();
		_sink.Subscriptions.Should().HaveCount(2);
		reg.Status.Should().Be(TriggerStatus.Waiting);
		reg.Config.Enabled.Should().BeTrue();
	}

	[Fact]
	public void RemoveTrigger_Unsubscribes()
	{
		var mgr = CreateTriggerManager();
		var reg = Register(mgr, WatcherConfig());

		mgr.RemoveTrigger(reg.Id).Should().BeTrue();

		_sink.Unsubscribed.Should().Equal("handle-new-file");
		mgr.GetTrigger(reg.Id).Should().BeNull();
	}

	[Fact]
	public void CloneTriggerConfigWithEnabled_Watcher_PreservesEveryField()
	{
		var original = new WatcherTriggerConfig
		{
			Type = TriggerType.Watcher,
			Enabled = true,
			Definition = JsonNode.Parse("""{ "name": "x", "type": "poll-command" }""")!.AsObject(),
			MaxConcurrent = 5,
			Catchup = WatcherCatchupMode.All,
			Parameters = new Dictionary<string, string> { ["a"] = "{{item.a}}" },
			InputHandlerPrompt = "shape it",
			InputHandlerModel = "claude-opus-4.8",
		};

		var clone = TriggerManager.CloneTriggerConfigWithEnabled(original, false).Should().BeOfType<WatcherTriggerConfig>().Subject;

		clone.Enabled.Should().BeFalse();
		clone.Watcher.Should().BeNull();
		clone.Definition!["name"]!.GetValue<string>().Should().Be("x");
		clone.Definition.Should().NotBeSameAs(original.Definition);
		clone.MaxConcurrent.Should().Be(5);
		clone.Catchup.Should().Be(WatcherCatchupMode.All);
		clone.Parameters.Should().Equal(original.Parameters);
		clone.InputHandlerPrompt.Should().Be("shape it");
		clone.InputHandlerModel.Should().Be("claude-opus-4.8");
	}

	[Fact]
	public async Task DispatchWatcherItemAsync_LaunchesThroughTheLauncher_AndReturnsBeforeCompletion()
	{
		var mgr = CreateTriggerManager();
		var reg = Register(mgr, WatcherConfig());

		var dispatch = mgr.DispatchWatcherItemAsync(reg.Id, new Dictionary<string, string> { ["path"] = "C:/inbox/a.txt" });
		var launch = await _launcher.WaitForLaunchAsync(1);

		var executionId = await dispatch.WaitAsync(TimeSpan.FromSeconds(5));
		executionId.Should().Be(launch.ExecutionId, "the dispatcher gets the id as soon as the run is launched");
		launch.Completion.Task.IsCompleted.Should().BeFalse("the run is still going - dispatch must not wait for it");
		launch.Request.TriggeredBy.Should().Be("watcher:lib");
		launch.Request.TriggerId.Should().Be(reg.Id);
		launch.Request.Parameters.Should().Equal(new Dictionary<string, string> { ["path"] = "C:/inbox/a.txt" });
		reg.Status.Should().Be(TriggerStatus.Running);
		reg.RunCount.Should().Be(1);

		launch.Complete(ExecutionStatus.Succeeded);
		await WaitUntilAsync(() => reg.Status == TriggerStatus.Waiting);
		reg.LastExecutionId.Should().Be(launch.ExecutionId);
		reg.ActiveExecutionCount.Should().Be(0);
	}

	[Fact]
	public async Task DispatchWatcherItemAsync_UnknownOrPausedTrigger_ReturnsNull()
	{
		var mgr = CreateTriggerManager();
		var reg = Register(mgr, WatcherConfig());

		(await mgr.DispatchWatcherItemAsync("nope", null)).Should().BeNull();

		mgr.SetTriggerEnabled(reg.Id, false);
		(await mgr.DispatchWatcherItemAsync(reg.Id, null)).Should().BeNull();
		_launcher.LaunchCount.Should().Be(0);
	}

	[Fact]
	public async Task DispatchWatcherItemAsync_LaunchFailure_ReturnsNullAndMarksError()
	{
		var mgr = CreateTriggerManager();
		var reg = Register(mgr, WatcherConfig());
		_launcher.ThrowOnNextLaunch = new ChildOrchestrationLaunchException(ChildOrchestrationLaunchException.OrchestrationNotFound, "gone");

		var executionId = await mgr.DispatchWatcherItemAsync(reg.Id, null).WaitAsync(TimeSpan.FromSeconds(5));

		executionId.Should().BeNull();
		await WaitUntilAsync(() => reg.Status == TriggerStatus.Error);
		reg.LastError.Should().Be("gone");
	}

	[Fact]
	public void TriggerManagerWatcherDispatcher_RoutesToDispatchWatcherItem()
	{
		var mgr = CreateTriggerManager();
		var reg = Register(mgr, WatcherConfig());
		var services = new ServiceCollection();
		services.AddSingleton(mgr);
		using var provider = services.BuildServiceProvider();
		var dispatcher = new TriggerManagerWatcherDispatcher(provider);
		var subscription = new WatcherSubscription("lib", reg.Id, 1, null, WatcherCatchupMode.FromNow, null, null, reg.Id);
		var entry = new Orchestra.Watchers.Models.PollCommandWatcher { Name = "lib", Schedule = new Orchestra.Watchers.Models.WatcherSchedule { IntervalSeconds = 5 }, Fetch = new Orchestra.Watchers.Models.WatcherFetchCommandConfig { Command = "x" } };

		var task = dispatcher.DispatchAsync(new WatcherDispatch(subscription, entry, null, "k", new Dictionary<string, string> { ["p"] = "v" }, null), CancellationToken.None);

		_launcher.WaitForLaunchAsync(1).GetAwaiter().GetResult().Request.Parameters.Should().ContainKey("p");
		task.Should().NotBeNull();
	}

	[Fact]
	public void PersistedUserTrigger_WatcherShape_RoundTripsThroughLoadPersistedTriggers()
	{
		// Persisted user triggers are re-registered by path, so the files must exist and parse; ids derive from path + name.
		var userPath = Path.Combine(_tempDir, "user.yaml");
		var nightlyPath = Path.Combine(_tempDir, "nightly.yaml");
		File.WriteAllText(userPath, "name: user\ndescription: d\nsteps: []\n");
		File.WriteAllText(nightlyPath, "name: nightly\ndescription: d\nsteps: []\n");

		var mgr = CreateTriggerManager();
		var reg = mgr.RegisterTrigger(userPath, WatcherConfig(), parameters: null, source: TriggerSource.User);
		var schedulerReg = mgr.RegisterTrigger(
			nightlyPath,
			new SchedulerTriggerConfig { Type = TriggerType.Scheduler, Cron = "0 3 * * *", MaxRuns = 9 },
			parameters: new Dictionary<string, string> { ["env"] = "prod" },
			source: TriggerSource.User);

		var fresh = CreateTriggerManager();
		fresh.LoadPersistedTriggers();

		var loaded = fresh.GetTrigger(reg.Id);
		loaded.Should().NotBeNull();
		var config = loaded!.Config.Should().BeOfType<WatcherTriggerConfig>().Subject;
		config.Watcher.Should().Be("lib");
		config.MaxConcurrent.Should().Be(2);
		config.Parameters.Should().ContainKey("path");
		_sink.Subscriptions.Should().HaveCount(2, "the reloaded watcher trigger subscribes again");

		// The same sidecar path previously dropped every derived field (and serialized the type as a number).
		var scheduler = fresh.GetTrigger(schedulerReg.Id)!.Config.Should().BeOfType<SchedulerTriggerConfig>().Subject;
		scheduler.Cron.Should().Be("0 3 * * *");
		scheduler.MaxRuns.Should().Be(9);
		fresh.GetTrigger(schedulerReg.Id)!.Parameters.Should().Equal(new Dictionary<string, string> { ["env"] = "prod" });
	}

	private static async Task WaitUntilAsync(Func<bool> condition)
	{
		var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
		while (!condition())
		{
			if (DateTimeOffset.UtcNow > deadline)
				throw new TimeoutException("condition not met");
			await Task.Delay(20);
		}
	}

	private sealed class FakeSink : IWatcherSubscriptionSink
	{
		public List<WatcherSubscriptionRequest> Subscriptions { get; } = [];

		public List<string> Unsubscribed { get; } = [];

		public WatcherInitializationException? FailWith { get; set; }

		public WatcherSubscription Subscribe(WatcherSubscriptionRequest request)
		{
			if (FailWith is not null)
				throw FailWith;

			Subscriptions.Add(request);
			return new WatcherSubscription(request.Config.Watcher ?? "inline", request.OrchestrationId, request.Config.MaxConcurrent, request.Config.Parameters, request.Config.Catchup, null, null, request.TriggerId);
		}

		public bool Unsubscribe(string triggerId)
		{
			Unsubscribed.Add(triggerId);
			return true;
		}
	}

	private sealed class RecordingLauncher : IChildOrchestrationLauncher
	{
		private readonly List<Launch> _launches = [];
		private readonly SemaphoreSlim _signal = new(0);

		public Exception? ThrowOnNextLaunch { get; set; }

		public int LaunchCount { get { lock (_launches) return _launches.Count; } }

		public IReadOnlyList<Launch> Launches { get { lock (_launches) return _launches.ToArray(); } }

		public Task<ChildOrchestrationHandle> LaunchAsync(ChildLaunchRequest request, CancellationToken cancellationToken = default)
		{
			if (ThrowOnNextLaunch is { } ex)
			{
				ThrowOnNextLaunch = null;
				throw ex;
			}

			var launch = new Launch(request);
			lock (_launches) _launches.Add(launch);
			_signal.Release();

			return Task.FromResult(new ChildOrchestrationHandle
			{
				ExecutionId = launch.ExecutionId,
				OrchestrationId = request.OrchestrationId,
				OrchestrationName = request.OrchestrationId,
				Reporter = request.Reporter ?? NullOrchestrationReporter.Instance,
				StartedAt = DateTimeOffset.UtcNow,
				Completion = launch.Completion.Task,
			});
		}

		public async Task<Launch> WaitForLaunchAsync(int count)
		{
			var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
			while (LaunchCount < count)
			{
				var remaining = deadline - DateTimeOffset.UtcNow;
				if (remaining <= TimeSpan.Zero)
					throw new TimeoutException($"Expected {count} launches, saw {LaunchCount}.");
				await _signal.WaitAsync(remaining);
			}

			return Launches[count - 1];
		}
	}

	private sealed class Launch
	{
		public Launch(ChildLaunchRequest request)
		{
			Request = request;
			ExecutionId = Guid.NewGuid().ToString("N")[..12];
		}

		public ChildLaunchRequest Request { get; }

		public string ExecutionId { get; }

		public TaskCompletionSource<ChildOrchestrationResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

		public void Complete(ExecutionStatus status)
		{
			Completion.TrySetResult(new ChildOrchestrationResult
			{
				ExecutionId = ExecutionId,
				OrchestrationId = Request.OrchestrationId,
				OrchestrationName = Request.OrchestrationId,
				Status = status,
				OrchestrationResult = new OrchestrationResult
				{
					Status = status,
					Results = new Dictionary<string, ExecutionResult>(),
					StepResults = new Dictionary<string, ExecutionResult>(),
				},
				StartedAt = DateTimeOffset.UtcNow,
				CompletedAt = DateTimeOffset.UtcNow,
			});
		}
	}
}
