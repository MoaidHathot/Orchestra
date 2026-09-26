using System.Threading.Channels;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orchestra.Engine.Tests.TestHelpers;

namespace Orchestra.Engine.Tests.Executor;

/// <summary>
/// <see cref="Orchestration.OnStepFailure"/>: <c>continue</c> (default) skips dependents and
/// lets independent branches finish; <c>stop</c> cancels everything unfinished as soon as one
/// step fails.
/// </summary>
public class StepFailurePolicyTests
{
	private readonly IScheduler _scheduler = new OrchestrationScheduler();
	private readonly IOrchestrationReporter _reporter = SerializingReporterProxy.CreateRecording();

	/// <summary>
	/// Agent: the step named "boom" fails immediately; every other step waits until the
	/// <paramref name="release"/> gate opens (or its token is cancelled), then succeeds.
	/// Prompts are the step's user prompt, so the step name is threaded through it. The event
	/// channel is completed on every path — the engine drains it to completion before it
	/// looks at the result task, so a faulted/cancelled task with an open channel would hang.
	/// </summary>
	private static MockAgentBuilder GatedAgent(TaskCompletionSource release)
	{
		var agent = new MockAgentBuilder();
		agent.WithHandler((prompt, ct) =>
		{
			var channel = Channel.CreateUnbounded<AgentEvent>();
			var task = Task.Run(async () =>
			{
				try
				{
					if (prompt.Contains("boom", StringComparison.Ordinal))
						throw new InvalidOperationException("boom exploded");

					await release.Task.WaitAsync(ct);
					return new AgentResult { Content = $"ok:{prompt}" };
				}
				finally
				{
					channel.Writer.TryComplete();
				}
			}, ct);
			return new AgentTask(channel.Reader, task);
		});
		return agent;
	}

	private static PromptOrchestrationStep Step(string name, params string[] dependsOn)
		=> TestOrchestrations.CreatePromptStep(name, dependsOn: dependsOn, userPrompt: name);

	private static Orchestration Build(StepFailurePolicy policy) => new()
	{
		Name = $"policy-{policy}",
		Description = "boom fails; slow is an independent branch; after-boom depends on boom",
		OnStepFailure = policy,
		TimeoutSeconds = null,
		Steps =
		[
			Step("boom"),
			Step("after-boom", "boom"),
			Step("slow"),
			Step("after-slow", "slow"),
		],
	};

	[Fact]
	public async Task Continue_IsTheDefault()
	{
		new Orchestration { Name = "d", Description = "d", Steps = [] }.OnStepFailure.Should().Be(StepFailurePolicy.Continue);
	}

	[Fact]
	public async Task Continue_SkipsDependents_ButLetsIndependentBranchesFinish()
	{
		var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var executor = new OrchestrationExecutor(_scheduler, GatedAgent(release), _reporter, NullLoggerFactory.Instance);

		var run = executor.ExecuteAsync(Build(StepFailurePolicy.Continue));
		await Task.Delay(150); // let boom fail while slow is parked on the gate
		release.SetResult();
		var result = await run.WaitAsync(TimeSpan.FromSeconds(20));

		result.Status.Should().Be(ExecutionStatus.Failed);
		result.StepResults["boom"].Status.Should().Be(ExecutionStatus.Failed);
		result.StepResults["after-boom"].Status.Should().Be(ExecutionStatus.Skipped);
		result.StepResults["after-boom"].SkipKind.Should().Be(SkipKind.DependencyFailure);
		result.StepResults["slow"].Status.Should().Be(ExecutionStatus.Succeeded, "independent branches keep running under 'continue'");
		result.StepResults["after-slow"].Status.Should().Be(ExecutionStatus.Succeeded);
		result.Cancellation.Should().BeNull();
	}

	[Fact]
	public async Task Stop_CancelsEveryUnfinishedStep_AndTheRunIsFailedNotCancelled()
	{
		var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var executor = new OrchestrationExecutor(_scheduler, GatedAgent(release), _reporter, NullLoggerFactory.Instance);

		// The gate is never opened: if fail-fast did not cancel `slow`, this would hang.
		var result = await executor.ExecuteAsync(Build(StepFailurePolicy.Stop)).WaitAsync(TimeSpan.FromSeconds(20));

		result.Status.Should().Be(ExecutionStatus.Failed, "a step failure dominates; fail-fast must not relabel the run as Cancelled");
		result.StepResults["boom"].Status.Should().Be(ExecutionStatus.Failed);
		result.StepResults["slow"].Status.Should().Be(ExecutionStatus.Cancelled, "the in-flight independent branch is cancelled");
		result.StepResults["after-slow"].Status.Should().Be(ExecutionStatus.Cancelled, "not-yet-started steps are cancelled too");
		result.StepResults["after-boom"].Status.Should().Be(ExecutionStatus.Cancelled);

		foreach (var name in new[] { "slow", "after-slow", "after-boom" })
		{
			result.StepResults[name].ErrorMessage.Should().Contain("onStepFailure is 'stop'", $"{name} must carry the fail-fast cause")
				.And.Contain("boom");
		}
	}

	[Fact]
	public async Task Stop_ReportsCancelledStepsToTheReporter()
	{
		var reporter = Substitute.For<IOrchestrationReporter>();
		var serialized = SerializingReporterProxy.Wrap(reporter);
		var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var executor = new OrchestrationExecutor(_scheduler, GatedAgent(release), serialized, NullLoggerFactory.Instance);

		await executor.ExecuteAsync(Build(StepFailurePolicy.Stop)).WaitAsync(TimeSpan.FromSeconds(20));

		// ReportStepError has two overloads (with/without AgentSessionErrorDetails); match by name.
		reporter.ReceivedCalls()
			.Should().Contain(c => c.GetMethodInfo().Name == nameof(IOrchestrationReporter.ReportStepError) && (string)c.GetArguments()[0]! == "boom",
				"the failing step must be reported as an error");
		reporter.Received().ReportStepCancelled("after-slow");
		reporter.Received().ReportStepCancelled("slow");
	}

	[Fact]
	public async Task Stop_RunRecordCarriesTheFailFastCauseOnCancelledSteps()
	{
		var runStore = Substitute.For<IRunStore>();
		OrchestrationRunRecord? captured = null;
		runStore.SaveRunAsync(Arg.Do<OrchestrationRunRecord>(r => captured = r), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

		var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var executor = new OrchestrationExecutor(_scheduler, GatedAgent(release), _reporter, NullLoggerFactory.Instance, runStore: runStore);

		await executor.ExecuteAsync(Build(StepFailurePolicy.Stop)).WaitAsync(TimeSpan.FromSeconds(20));

		captured.Should().NotBeNull();
		captured!.Status.Should().Be(ExecutionStatus.Failed);
		captured.AllStepRecords["after-slow"].Status.Should().Be(ExecutionStatus.Cancelled);
		captured.AllStepRecords["after-slow"].ErrorMessage.Should().Contain("onStepFailure is 'stop'");
	}

	[Fact]
	public async Task Stop_AppliesOnlyAfterRetriesAreExhausted()
	{
		// A retried step that eventually succeeds must not trigger fail-fast on its first attempt.
		var attempts = 0;
		var agent = new MockAgentBuilder();
		agent.WithHandler((prompt, ct) =>
		{
			var channel = Channel.CreateUnbounded<AgentEvent>();
			var task = Task.Run(() =>
			{
				try
				{
					// The formatted prompt embeds the user prompt; match on containment.
					if (prompt.Contains("flaky", StringComparison.Ordinal) && Interlocked.Increment(ref attempts) == 1)
						throw new InvalidOperationException("transient");
					return new AgentResult { Content = $"ok:{prompt}" };
				}
				finally
				{
					channel.Writer.TryComplete();
				}
			}, ct);
			return new AgentTask(channel.Reader, task);
		});

		var executor = new OrchestrationExecutor(_scheduler, agent, _reporter, NullLoggerFactory.Instance);
		var orchestration = new Orchestration
		{
			Name = "stop-after-retry",
			Description = "flaky succeeds on its second attempt",
			OnStepFailure = StepFailurePolicy.Stop,
			TimeoutSeconds = null,
			Steps =
			[
				new PromptOrchestrationStep
				{
					Name = "flaky", Type = OrchestrationStepType.Prompt, SystemPrompt = "s", UserPrompt = "flaky", Model = "claude-opus-4.8",
					Retry = new RetryPolicy { MaxRetries = 2, BackoffSeconds = 0 },
				},
				Step("other"),
			],
		};

		var result = await executor.ExecuteAsync(orchestration).WaitAsync(TimeSpan.FromSeconds(20));

		result.Status.Should().Be(ExecutionStatus.Succeeded);
		attempts.Should().Be(2);
		result.StepResults["other"].Status.Should().Be(ExecutionStatus.Succeeded, "the first (retried) failure must not stop the run");
	}

	[Fact]
	public async Task Stop_ParsesFromJson_CaseInsensitively()
	{
		var orchestration = OrchestrationParser.ParseOrchestration("""
		{ "name": "p", "description": "d", "onStepFailure": "STOP", "maxConcurrency": 3,
		  "steps": [ { "name": "a", "type": "Transform", "template": "x" } ] }
		""", []);

		orchestration.OnStepFailure.Should().Be(StepFailurePolicy.Stop);
		orchestration.MaxConcurrency.Should().Be(3);
	}
}
