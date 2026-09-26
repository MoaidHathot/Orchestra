using System.Collections.Concurrent;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Orchestra.Engine;
using Orchestra.Host.Api;
using Orchestra.Host.Triggers;
using Xunit;

namespace Orchestra.Host.Tests;

/// <summary>
/// Behavioural tests for two trigger-firing bugs:
/// <list type="bullet">
///   <item>A <see cref="LoopTriggerConfig"/> with <c>delaySeconds &gt; 0</c> armed
///   <c>NextFireTime</c> after each iteration but nothing consumed it, so the loop silently
///   stopped after its first run.</item>
///   <item>A <see cref="WebhookTriggerConfig"/> with <c>maxConcurrent &gt; 1</c> was never
///   throttled: the old check only looked at the single most-recent <c>ActiveExecutionId</c>.</item>
/// </list>
/// The launcher is faked so the tests control exactly when each "run" completes.
/// </summary>
public class TriggerFiringTests : IDisposable
{
	private readonly string _tempDir;
	private readonly ConcurrentDictionary<string, CancellationTokenSource> _activeExecutions = new();
	private readonly ConcurrentDictionary<string, ActiveExecutionInfo> _activeExecutionInfos = new();
	private readonly RecordingLauncher _launcher = new();

	public TriggerFiringTests()
	{
		_tempDir = Path.Combine(Path.GetTempPath(), $"orchestra-trigger-firing-tests-{Guid.NewGuid():N}");
		Directory.CreateDirectory(Path.Combine(_tempDir, "runs"));
	}

	public void Dispose()
	{
		if (Directory.Exists(_tempDir))
		{
			try { Directory.Delete(_tempDir, recursive: true); }
			catch { /* best-effort cleanup */ }
		}
	}

	private TriggerManager CreateTriggerManager() => new(
		_activeExecutions,
		_activeExecutionInfos,
		agentBuilder: null!,
		scheduler: null!,
		loggerFactory: NullLoggerFactory.Instance,
		logger: NullLogger<TriggerManager>.Instance,
		runsDir: Path.Combine(_tempDir, "runs"),
		runStore: null!,
		checkpointStore: null!,
		launcher: _launcher,
		dataPath: _tempDir);

	private TriggerRegistration Register(TriggerManager mgr, TriggerConfig config, string name)
	{
		return mgr.RegisterTrigger(
			orchestrationPath: Path.Combine(_tempDir, $"{name}.json"),
			config: config,
			orchestrationId: name,
			preloadedOrchestration: new Orchestration { Name = name, Description = name, Steps = [] });
	}

	// ─── Loop trigger with delaySeconds ───

	[Fact]
	public async Task LoopTrigger_WithDelay_IsReFiredByTheSchedulerTickAfterTheDelayElapses()
	{
		var mgr = CreateTriggerManager();
		var reg = Register(mgr, new LoopTriggerConfig
		{
			Type = TriggerType.Loop,
			Enabled = true,
			DelaySeconds = 1,
		}, "delayed-loop");

		// Iteration 1: kicked off the way the Portal/CLI does it.
		var fireTask = mgr.FireTriggerAsync(reg.Id);
		var first = await _launcher.WaitForLaunchAsync(1);
		first.Complete(ExecutionStatus.Succeeded);
		await fireTask;

		// Post-run bookkeeping must have armed the next iteration.
		reg.Status.Should().Be(TriggerStatus.Waiting);
		reg.NextFireTime.Should().NotBeNull();
		_launcher.LaunchCount.Should().Be(1);

		// A tick before the delay elapses must not fire.
		await mgr.CheckSchedulerTriggersAsync(CancellationToken.None);
		_launcher.LaunchCount.Should().Be(1, "the delay has not elapsed yet");

		// Force the arm to be due and tick again.
		reg.NextFireTime = DateTime.UtcNow.AddSeconds(-1);
		await mgr.CheckSchedulerTriggersAsync(CancellationToken.None);

		var second = await _launcher.WaitForLaunchAsync(2);
		_launcher.LaunchCount.Should().Be(2, "the scheduler tick must start iteration 2");
		reg.Status.Should().Be(TriggerStatus.Running);

		second.Complete(ExecutionStatus.Succeeded);
		await WaitUntilAsync(() => reg.Status == TriggerStatus.Waiting);

		// ...and iteration 2 re-arms iteration 3, proving the loop keeps going.
		reg.NextFireTime.Should().NotBeNull();
		reg.RunCount.Should().Be(2);
	}

	[Fact]
	public async Task LoopTrigger_WithDelay_PassesTheOriginalParametersToTheNextIteration()
	{
		var mgr = CreateTriggerManager();
		var reg = Register(mgr, new LoopTriggerConfig
		{
			Type = TriggerType.Loop,
			Enabled = true,
			DelaySeconds = 1,
		}, "delayed-loop-params");

		var fireTask = mgr.FireTriggerAsync(reg.Id, new Dictionary<string, string> { ["topic"] = "quokkas" });
		var first = await _launcher.WaitForLaunchAsync(1);
		first.Complete(ExecutionStatus.Succeeded);
		await fireTask;

		reg.NextFireTime = DateTime.UtcNow.AddSeconds(-1);
		await mgr.CheckSchedulerTriggersAsync(CancellationToken.None);
		var second = await _launcher.WaitForLaunchAsync(2);

		second.Request.Parameters.Should().NotBeNull();
		second.Request.Parameters!["topic"].Should().Be("quokkas", "iteration parameters must survive the delay");
		second.Complete(ExecutionStatus.Succeeded);
	}

	[Fact]
	public async Task LoopTrigger_WithDelay_TickDoesNotDoubleFire()
	{
		var mgr = CreateTriggerManager();
		var reg = Register(mgr, new LoopTriggerConfig
		{
			Type = TriggerType.Loop,
			Enabled = true,
			DelaySeconds = 1,
		}, "delayed-loop-once");

		var fireTask = mgr.FireTriggerAsync(reg.Id);
		(await _launcher.WaitForLaunchAsync(1)).Complete(ExecutionStatus.Succeeded);
		await fireTask;

		reg.NextFireTime = DateTime.UtcNow.AddSeconds(-1);

		// Two concurrent ticks (the real loop is single-threaded, but the CAS must hold anyway).
		await Task.WhenAll(
			mgr.CheckSchedulerTriggersAsync(CancellationToken.None),
			mgr.CheckSchedulerTriggersAsync(CancellationToken.None));
		await _launcher.WaitForLaunchAsync(2);
		await Task.Delay(100);

		_launcher.LaunchCount.Should().Be(2, "exactly one new iteration must start");
		_launcher.Launches[1].Complete(ExecutionStatus.Succeeded);
	}

	[Fact]
	public async Task LoopTrigger_WithDelay_DoesNotReFireWhenDisabledDuringTheDelay()
	{
		var mgr = CreateTriggerManager();
		var reg = Register(mgr, new LoopTriggerConfig
		{
			Type = TriggerType.Loop,
			Enabled = true,
			DelaySeconds = 1,
		}, "delayed-loop-disabled");

		var fireTask = mgr.FireTriggerAsync(reg.Id);
		(await _launcher.WaitForLaunchAsync(1)).Complete(ExecutionStatus.Succeeded);
		await fireTask;

		mgr.SetTriggerEnabled(reg.Id, false);
		reg.NextFireTime = DateTime.UtcNow.AddSeconds(-1);
		await mgr.CheckSchedulerTriggersAsync(CancellationToken.None);
		await Task.Delay(100);

		_launcher.LaunchCount.Should().Be(1, "a paused loop must stay paused");
	}

	// ─── Webhook maxConcurrent ───

	[Fact]
	public async Task Webhook_MaxConcurrentTwo_AdmitsTwoRunsAndRejectsTheThird()
	{
		var mgr = CreateTriggerManager();
		var reg = Register(mgr, new WebhookTriggerConfig
		{
			Type = TriggerType.Webhook,
			Enabled = true,
			MaxConcurrent = 2,
		}, "webhook-2");

		var fire1 = mgr.FireWebhookTriggerAsync(reg.Id, null);
		var launch1 = await _launcher.WaitForLaunchAsync(1);
		var fire2 = mgr.FireWebhookTriggerAsync(reg.Id, null);
		var launch2 = await _launcher.WaitForLaunchAsync(2);

		reg.ActiveExecutionCount.Should().Be(2);

		// Third arrival while two are in flight is rejected (Found=true, no execution).
		var third = await mgr.FireWebhookTriggerAsync(reg.Id, null);
		third.Found.Should().BeTrue();
		third.ExecutionId.Should().BeNull("the limit of 2 concurrent runs is reached");
		_launcher.LaunchCount.Should().Be(2);

		// Finishing one frees a slot.
		launch1.Complete(ExecutionStatus.Succeeded);
		await fire1;
		reg.ActiveExecutionCount.Should().Be(1);

		var fourth = mgr.FireWebhookTriggerAsync(reg.Id, null);
		var launch4 = await _launcher.WaitForLaunchAsync(3);
		_launcher.LaunchCount.Should().Be(3);

		launch2.Complete(ExecutionStatus.Succeeded);
		launch4.Complete(ExecutionStatus.Succeeded);
		await Task.WhenAll(fire2, fourth);
		reg.ActiveExecutionCount.Should().Be(0);
	}

	[Fact]
	public async Task Webhook_MaxConcurrentOne_RejectsWhileAnyRunIsInFlight_EvenIfStartedManually()
	{
		var mgr = CreateTriggerManager();
		var reg = Register(mgr, new WebhookTriggerConfig
		{
			Type = TriggerType.Webhook,
			Enabled = true,
			MaxConcurrent = 1,
		}, "webhook-1");

		// Started through the generic manual path, not the webhook endpoint.
		var manual = mgr.FireTriggerAsync(reg.Id);
		var launch = await _launcher.WaitForLaunchAsync(1);

		var viaWebhook = await mgr.FireWebhookTriggerAsync(reg.Id, null);
		viaWebhook.Found.Should().BeTrue();
		viaWebhook.ExecutionId.Should().BeNull("a manually started run must count against maxConcurrent");
		_launcher.LaunchCount.Should().Be(1);

		launch.Complete(ExecutionStatus.Succeeded);
		await manual;
		reg.ActiveExecutionCount.Should().Be(0);

		var afterwards = mgr.FireWebhookTriggerAsync(reg.Id, null);
		(await _launcher.WaitForLaunchAsync(2)).Complete(ExecutionStatus.Succeeded);
		(await afterwards).ExecutionId.Should().NotBeNull();
	}

	[Fact]
	public async Task Webhook_SlotIsReleasedWhenTheLauncherThrows()
	{
		var mgr = CreateTriggerManager();
		var reg = Register(mgr, new WebhookTriggerConfig
		{
			Type = TriggerType.Webhook,
			Enabled = true,
			MaxConcurrent = 1,
		}, "webhook-throws");

		_launcher.ThrowOnNextLaunch = new InvalidOperationException("orchestration not found");

		var failed = await mgr.FireWebhookTriggerAsync(reg.Id, null);
		failed.ExecutionId.Should().BeNull();
		reg.ActiveExecutionCount.Should().Be(0, "a launch failure must not leak the slot");
		reg.Status.Should().Be(TriggerStatus.Error);
	}

	// ─── helpers ───

	private static async Task WaitUntilAsync(Func<bool> condition)
	{
		var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
		while (DateTimeOffset.UtcNow < deadline)
		{
			if (condition()) return;
			await Task.Delay(20);
		}

		throw new TimeoutException("Condition was not met before timeout.");
	}

	/// <summary>
	/// Fake launcher: records every request and hands the test a <see cref="Launch"/> it can
	/// complete at will, so run duration is fully under test control.
	/// </summary>
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

		/// <summary>Waits until at least <paramref name="count"/> launches have been recorded and returns the last one.</summary>
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
