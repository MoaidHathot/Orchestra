using System.Threading.Channels;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Orchestra.Engine.Tests.TestHelpers;

namespace Orchestra.Engine.Tests.Executor;

/// <summary>
/// <see cref="Orchestration.MaxConcurrency"/>: bounds how many steps execute at once within a
/// run. Every ready step used to start immediately on its own dedicated thread.
/// </summary>
public class MaxConcurrencyTests
{
	private readonly IScheduler _scheduler = new OrchestrationScheduler();
	private readonly IOrchestrationReporter _reporter = SerializingReporterProxy.CreateRecording();

	/// <summary>
	/// A mock agent whose steps take <paramref name="hold"/> and record the peak number that
	/// were inside the agent at the same time.
	/// </summary>
	private static (MockAgentBuilder Agent, Func<int> Peak) SlowAgent(TimeSpan hold)
	{
		var inFlight = 0;
		var peak = 0;
		var agent = new MockAgentBuilder();
		agent.WithHandler((prompt, ct) =>
		{
			var channel = Channel.CreateUnbounded<AgentEvent>();
			var task = Task.Run(async () =>
			{
				var now = Interlocked.Increment(ref inFlight);
				RecordPeak(ref peak, now);
				try
				{
					await Task.Delay(hold, ct);
				}
				finally
				{
					Interlocked.Decrement(ref inFlight);
					channel.Writer.TryComplete();
				}
				return new AgentResult { Content = "done" };
			}, ct);
			return new AgentTask(channel.Reader, task);
		});
		return (agent, () => Volatile.Read(ref peak));
	}

	/// <summary>
	/// A mock agent whose steps all wait inside the agent until <paramref name="parties"/> of
	/// them are there at once, then finish. Deterministic proof that the engine launched at
	/// least <paramref name="parties"/> steps concurrently: with fewer slots the barrier can
	/// never open and the steps fail after <paramref name="barrierTimeout"/> instead of hanging.
	/// </summary>
	private static (MockAgentBuilder Agent, Func<int> Peak) BarrierAgent(int parties, TimeSpan barrierTimeout)
	{
		var inFlight = 0;
		var peak = 0;
		var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var agent = new MockAgentBuilder();
		agent.WithHandler((prompt, ct) =>
		{
			var channel = Channel.CreateUnbounded<AgentEvent>();
			var task = Task.Run(async () =>
			{
				var now = Interlocked.Increment(ref inFlight);
				RecordPeak(ref peak, now);
				if (now >= parties)
					barrier.TrySetResult();
				try
				{
					await barrier.Task.WaitAsync(barrierTimeout, ct);
				}
				finally
				{
					Interlocked.Decrement(ref inFlight);
					channel.Writer.TryComplete();
				}
				return new AgentResult { Content = "done" };
			}, ct);
			return new AgentTask(channel.Reader, task);
		});
		return (agent, () => Volatile.Read(ref peak));
	}

	private static void RecordPeak(ref int peak, int now)
	{
		int observed;
		do { observed = Volatile.Read(ref peak); } while (now > observed && Interlocked.CompareExchange(ref peak, now, observed) != observed);
	}

	private static Orchestration FanOut(int stepCount, int? maxConcurrency, params OrchestrationStep[] extra) => new()
	{
		Name = "fan-out",
		Description = "independent steps",
		MaxConcurrency = maxConcurrency,
		TimeoutSeconds = null,
		Steps = Enumerable.Range(1, stepCount)
			.Select(i => (OrchestrationStep)TestOrchestrations.CreatePromptStep($"s{i}"))
			.Concat(extra)
			.ToArray(),
	};

	[Fact]
	public async Task MaxConcurrency_BoundsSimultaneousSteps()
	{
		// Barrier of 2: the run can only finish if two steps are inside the agent at once, which
		// proves the cap is a ceiling rather than a serializer; the peak proves it is a ceiling.
		var (agent, peak) = BarrierAgent(parties: 2, barrierTimeout: TimeSpan.FromSeconds(15));
		var executor = new OrchestrationExecutor(_scheduler, agent, _reporter, NullLoggerFactory.Instance);

		var result = await executor.ExecuteAsync(FanOut(stepCount: 8, maxConcurrency: 2)).WaitAsync(TimeSpan.FromSeconds(30));

		result.Status.Should().Be(ExecutionStatus.Succeeded);
		result.StepResults.Should().HaveCount(8);
		result.StepResults.Values.Should().OnlyContain(r => r.Status == ExecutionStatus.Succeeded);
		peak().Should().Be(2, "no more than maxConcurrency steps may be inside the agent at once, and the cap is not a serializer");
	}

	[Fact]
	public async Task MaxConcurrency_Unset_IsUnbounded()
	{
		// Every step blocks until all six are inside the agent together; only an unbounded run
		// can open that barrier. (Wall-clock overlap would be flaky under full-suite load.)
		var (agent, peak) = BarrierAgent(parties: 6, barrierTimeout: TimeSpan.FromSeconds(15));
		var executor = new OrchestrationExecutor(_scheduler, agent, _reporter, NullLoggerFactory.Instance);

		var result = await executor.ExecuteAsync(FanOut(stepCount: 6, maxConcurrency: null)).WaitAsync(TimeSpan.FromSeconds(30));

		result.Status.Should().Be(ExecutionStatus.Succeeded, "without a cap all independent steps start together (historical behaviour)");
		peak().Should().Be(6);
	}

	[Fact]
	public async Task MaxConcurrency_HostDefault_AppliesWhenOrchestrationSetsNone()
	{
		var (agent, peak) = SlowAgent(TimeSpan.FromMilliseconds(150));
		var executor = new OrchestrationExecutor(_scheduler, agent, _reporter, NullLoggerFactory.Instance,
			hostDefaults: new HostAgentDefaults { MaxConcurrency = 1 });

		var result = await executor.ExecuteAsync(FanOut(stepCount: 4, maxConcurrency: null));

		result.Status.Should().Be(ExecutionStatus.Succeeded);
		peak().Should().Be(1, "the host default serializes the run");
	}

	[Fact]
	public async Task MaxConcurrency_OrchestrationValue_OverridesHostDefault()
	{
		// With the host default (1) in effect a barrier of 3 could never open; it does, because
		// the orchestration's own value (3) wins.
		var (agent, peak) = BarrierAgent(parties: 3, barrierTimeout: TimeSpan.FromSeconds(15));
		var executor = new OrchestrationExecutor(_scheduler, agent, _reporter, NullLoggerFactory.Instance,
			hostDefaults: new HostAgentDefaults { MaxConcurrency = 1 });

		var result = await executor.ExecuteAsync(FanOut(stepCount: 6, maxConcurrency: 3)).WaitAsync(TimeSpan.FromSeconds(30));

		result.Status.Should().Be(ExecutionStatus.Succeeded, "the orchestration's own value (3) wins over the host's (1)");
		peak().Should().Be(3);
	}

	[Fact]
	public async Task MaxConcurrency_SkippedSteps_DoNotConsumeSlots_AndDependencyOrderIsKept()
	{
		// With a cap of 1, a chain A -> B -> C plus two condition-skipped siblings must still
		// finish in order; skips return before touching the limiter.
		var (agent, peak) = SlowAgent(TimeSpan.FromMilliseconds(50));
		var executor = new OrchestrationExecutor(_scheduler, agent, _reporter, NullLoggerFactory.Instance);

		var orchestration = new Orchestration
		{
			Name = "cap-1-chain",
			Description = "serialized chain with skipped siblings",
			MaxConcurrency = 1,
			TimeoutSeconds = null,
			Steps =
			[
				TestOrchestrations.CreatePromptStep("A"),
				TestOrchestrations.CreatePromptStep("B", dependsOn: ["A"]),
				TestOrchestrations.CreatePromptStep("C", dependsOn: ["B"]),
				new TransformOrchestrationStep { Name = "skip1", Type = OrchestrationStepType.Transform, Template = "x", When = "false" },
				new TransformOrchestrationStep { Name = "skip2", Type = OrchestrationStepType.Transform, Template = "x", When = "false" },
			],
		};

		var result = await executor.ExecuteAsync(orchestration);

		result.Status.Should().Be(ExecutionStatus.Succeeded);
		result.StepResults["C"].Status.Should().Be(ExecutionStatus.Succeeded);
		result.StepResults["skip1"].SkipKind.Should().Be(SkipKind.Condition);
		peak().Should().Be(1);
	}

	[Fact]
	public void OccupiesConcurrencySlot_ExemptsWaits()
	{
		OrchestrationExecutor.OccupiesConcurrencySlot(TestOrchestrations.CreatePromptStep("p")).Should().BeTrue();
		OrchestrationExecutor.OccupiesConcurrencySlot(new TransformOrchestrationStep { Name = "t", Type = OrchestrationStepType.Transform, Template = "x" }).Should().BeTrue();
		OrchestrationExecutor.OccupiesConcurrencySlot(new ApprovalOrchestrationStep { Name = "a", Type = OrchestrationStepType.Approval, Prompt = "?" }).Should().BeFalse("an Approval gate is a wait, not work");
		OrchestrationExecutor.OccupiesConcurrencySlot(new OrchestrationInvocationStep { Name = "o", Type = OrchestrationStepType.Orchestration, OrchestrationName = "child", Mode = OrchestrationInvocationMode.Async }).Should().BeFalse("async dispatch returns immediately");
		OrchestrationExecutor.OccupiesConcurrencySlot(new OrchestrationInvocationStep { Name = "o", Type = OrchestrationStepType.Orchestration, OrchestrationName = "child", Mode = OrchestrationInvocationMode.Sync }).Should().BeTrue("a sync child blocks the parent step for its whole duration");
	}

	[Fact]
	public async Task MaxConcurrency_Invalid_IsRejectedBeforeTheRunStarts()
	{
		var executor = new OrchestrationExecutor(_scheduler, new MockAgentBuilder().WithResponse("x"), _reporter, NullLoggerFactory.Instance);
		var act = () => executor.ExecuteAsync(FanOut(stepCount: 1, maxConcurrency: 0));

		await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*maxConcurrency*positive*");
	}

	[Fact]
	public async Task MaxConcurrency_WaitingStepIsNotReportedStarted_UntilItHasASlot()
	{
		// The reporter must see step-started only when the step really begins, so a live view
		// does not show 8 "running" steps under a cap of 1 and their timeouts don't tick while
		// queued.
		var recording = SerializingReporterProxy.CreateRecording();
		var startedAt = new System.Collections.Concurrent.ConcurrentDictionary<string, DateTimeOffset>();
		var (agent, _) = SlowAgent(TimeSpan.FromMilliseconds(120));
		var executor = new OrchestrationExecutor(_scheduler, agent, recording, NullLoggerFactory.Instance);

		var orchestration = FanOut(stepCount: 3, maxConcurrency: 1);
		var runStarted = DateTimeOffset.UtcNow;
		var result = await executor.ExecuteAsync(orchestration);
		var runEnded = DateTimeOffset.UtcNow;

		result.Status.Should().Be(ExecutionStatus.Succeeded);
		// Three steps at 120 ms each strictly serialized must take at least ~360 ms.
		(runEnded - runStarted).Should().BeGreaterThan(TimeSpan.FromMilliseconds(300),
			"a cap of 1 serializes the three 120 ms steps");
	}
}
