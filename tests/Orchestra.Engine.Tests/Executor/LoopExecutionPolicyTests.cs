using System.Collections.Concurrent;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orchestra.Engine.Tests.TestHelpers;

namespace Orchestra.Engine.Tests.Executor;

public class LoopExecutionPolicyTests
{
	private static PromptOrchestrationStep Step(string name, string[]? dependencies = null, LoopConfig? loop = null,
		RetryPolicy? retry = null, int? timeout = null) => new()
	{
		Name = name, Type = OrchestrationStepType.Prompt, SystemPrompt = "", UserPrompt = name,
		Model = "test", DependsOn = dependencies ?? [], Loop = loop, Retry = retry, TimeoutSeconds = timeout,
	};

	private static LoopConfig Loop() => new() { Target = "draft", ExitPattern = "PASS", MaxIterations = 2 };

	private static Orchestration Definition(PromptOrchestrationStep? target = null, PromptOrchestrationStep? checker = null) => new()
	{
		Name = "loop-policy", Description = "Loop iterations use ordinary step policies", TimeoutSeconds = null,
		MaxConcurrency = 1,
		Steps = [target ?? Step("draft"), checker ?? Step("check", ["draft"], Loop()), Step("after", ["check"])],
	};

	private static OrchestrationExecutor Executor(
		Func<OrchestrationStep, OrchestrationExecutionContext, CancellationToken, Task<ExecutionResult>> execute,
		IRunStore? runs = null, IOrchestrationReporter? reporter = null)
		=> new(new OrchestrationScheduler(), new MockAgentBuilder(), reporter ?? NullOrchestrationReporter.Instance, NullLoggerFactory.Instance,
			runStore: runs, stepExecutorRegistry: new StepExecutorRegistry().Register(new DelegateStepExecutor(OrchestrationStepType.Prompt, execute)));

	[Theory]
	[InlineData("draft")]
	[InlineData("check")]
	public async Task Iterations_ApplyRetryPolicyAndPreserveFeedback(string flakyStep)
	{
		var calls = new ConcurrentDictionary<string, int>();
		var feedback = new ConcurrentQueue<string?>();
		var executor = Executor((step, context, _) =>
		{
			var attempt = calls.AddOrUpdate(step.Name, 1, (_, count) => count + 1);
			if (step.Name == "draft" && attempt > 1)
				feedback.Enqueue(context.ConsumeLoopFeedback(step.Name));
			if (step.Name == flakyStep && attempt == 2)
				return Task.FromResult(ExecutionResult.Failed("transient"));
			return Task.FromResult(ExecutionResult.Succeeded(step.Name == "check" && attempt == 1 ? "Please refine the draft" : "PASS"));
		});
		var retry = new RetryPolicy { MaxRetries = 1, BackoffSeconds = 0 };
		var result = await executor.ExecuteAsync(Definition(Step("draft", retry: retry), Step("check", ["draft"], Loop(), retry)));

		result.Status.Should().Be(ExecutionStatus.Succeeded);
		calls[flakyStep].Should().Be(3);
		result.StepResults[flakyStep].RetryHistory.Should().ContainSingle();
		feedback.Should().NotBeEmpty().And.OnlyContain(value => value == "Please refine the draft");
		result.StepResults["after"].Status.Should().Be(ExecutionStatus.Succeeded);
	}

	[Fact]
	public async Task Iterations_TakeTheSameConcurrencySlotAsInitialAttempts()
	{
		var calls = new ConcurrentDictionary<string, int>();
		var unboundedAttempts = new ConcurrentQueue<string>();
		var executor = Executor((step, context, _) =>
		{
			var attempt = calls.AddOrUpdate(step.Name, 1, (_, count) => count + 1);
			// With maxConcurrency=1, no slot may be available while executor work is active.
			// This also catches the bypass when there is no coincidentally overlapping sibling.
			if (context.StepConcurrencyLimiter!.CurrentCount != 0)
				unboundedAttempts.Enqueue($"{step.Name}:{attempt}");
			return Task.FromResult(ExecutionResult.Succeeded(step.Name == "check" && attempt == 1 ? "refine" : "PASS"));
		});

		var result = await executor.ExecuteAsync(Definition());

		result.Status.Should().Be(ExecutionStatus.Succeeded);
		calls["draft"].Should().Be(2);
		calls["check"].Should().Be(2);
		unboundedAttempts.Should().BeEmpty("loop iterations are work subject to the run's concurrency cap");
	}

	[Theory]
	[InlineData("draft")]
	[InlineData("check")]
	public async Task Iterations_EnforceStepTimeoutAndDoNotReleaseDownstreamWork(string slowStep)
	{
		var calls = new ConcurrentDictionary<string, int>();
		var executor = Executor(async (step, _, token) =>
		{
			var attempt = calls.AddOrUpdate(step.Name, 1, (_, count) => count + 1);
			if (step.Name == slowStep && attempt == 2)
				await Task.Delay(Timeout.Infinite, token);
			return ExecutionResult.Succeeded(step.Name == "check" && attempt == 1 ? "refine" : "PASS");
		});
		using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
		var result = await executor.ExecuteAsync(Definition(Step("draft", timeout: 1), Step("check", ["draft"], Loop(), timeout: 1)),
			cancellationToken: cancellation.Token).WaitAsync(TimeSpan.FromSeconds(8));

		result.Status.Should().Be(ExecutionStatus.Failed);
		result.StepResults[slowStep].ErrorCategory.Should().Be(StepErrorCategory.Timeout);
		result.StepResults["after"].Status.Should().Be(ExecutionStatus.Skipped);
		calls.Should().NotContainKey("after");
	}

	[Fact]
	public async Task TargetFailure_BlocksCheckerDependentsAndPreservesIterationHistory()
	{
		var calls = new ConcurrentDictionary<string, int>();
		var runs = Substitute.For<IRunStore>();
		OrchestrationRunRecord? saved = null;
		runs.SaveRunAsync(Arg.Do<OrchestrationRunRecord>(r => saved = r), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
		var executor = Executor((step, _, _) =>
		{
			var attempt = calls.AddOrUpdate(step.Name, 1, (_, count) => count + 1);
			return Task.FromResult(step.Name == "draft" && attempt == 2
				? ExecutionResult.Failed("revision failed")
				: ExecutionResult.Succeeded("refine"));
		}, runs);

		var result = await executor.ExecuteAsync(Definition());

		result.Status.Should().Be(ExecutionStatus.Failed);
		result.StepResults["check"].BlocksDependents.Should().BeTrue();
		result.StepResults["after"].Status.Should().Be(ExecutionStatus.Skipped);
		calls.Should().NotContainKey("after");
		saved!.AllStepRecords["draft"].Status.Should().Be(ExecutionStatus.Succeeded);
		saved.AllStepRecords["draft:iteration-1"].Status.Should().Be(ExecutionStatus.Failed);
	}

	[Fact]
	public async Task Iterations_PublishCompletionAndIncludeAllIterationsInRunUsage()
	{
		var calls = new ConcurrentDictionary<string, int>();
		var runs = Substitute.For<IRunStore>();
		var reporter = SerializingReporterProxy.CreateRecording();
		OrchestrationRunRecord? saved = null;
		runs.SaveRunAsync(Arg.Do<OrchestrationRunRecord>(r => saved = r), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
		var executor = Executor((step, _, _) =>
		{
			var attempt = calls.AddOrUpdate(step.Name, 1, (_, count) => count + 1);
			return Task.FromResult(ExecutionResult.Succeeded(step.Name == "check" && attempt == 1 ? "refine" : "PASS") with
			{
				Usage = new TokenUsage { InputTokens = 10, OutputTokens = 2, Cost = 0.1 },
			});
		}, runs, reporter);

		await executor.ExecuteAsync(Definition());

		saved!.AllStepRecords.Should().HaveCount(5);
		saved.TotalUsage!.InputTokens.Should().Be(50, "both the original calls and the refinement calls consumed tokens");
		saved.TotalUsage.OutputTokens.Should().Be(10);
		saved.TotalUsage.Cost.Should().BeApproximately(0.5, 0.00001);
		SerializingReporterProxy.Recorded(reporter).Received(2).ReportStepCompleted("draft", Arg.Any<AgentResult>(), OrchestrationStepType.Prompt);
		SerializingReporterProxy.Recorded(reporter).Received(2).ReportStepCompleted("check", Arg.Any<AgentResult>(), OrchestrationStepType.Prompt);
	}

	[Theory]
	[InlineData("draft", false)]
	[InlineData("check", false)]
	[InlineData("draft", true)]
	[InlineData("check", true)]
	public async Task IterationTerminalSignals_StopUnfinishedWork(string signalingStep, bool complete)
	{
		var calls = new ConcurrentDictionary<string, int>();
		var slowStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var executor = Executor(async (step, _, token) =>
		{
			if (step.Name == "slow")
			{
				slowStarted.TrySetResult();
				await Task.Delay(Timeout.Infinite, token);
			}
			var attempt = calls.AddOrUpdate(step.Name, 1, (_, count) => count + 1);
			if (step.Name == signalingStep && attempt == 2)
			{
				await slowStarted.Task.WaitAsync(token);
				return complete
					? ExecutionResult.Succeeded("finished early") with
					{
						OrchestrationCompleteRequested = true, OrchestrationCompleteStatus = ExecutionStatus.Succeeded,
						OrchestrationCompleteReason = "iteration finished the work",
					}
					: ExecutionResult.Failed("iteration failed");
			}
			return ExecutionResult.Succeeded(step.Name == "check" && attempt == 1 ? "refine" : "PASS");
		});
		var definition = new Orchestration
		{
			Name = "loop-signals", Description = "Iteration results drive run control", TimeoutSeconds = null,
			OnStepFailure = StepFailurePolicy.Stop,
			Steps = [Step("draft"), Step("check", ["draft"], Loop()), Step("slow"), Step("after", ["check"])],
		};
		using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(3));
		var result = await executor.ExecuteAsync(definition, cancellationToken: cancellation.Token).WaitAsync(TimeSpan.FromSeconds(5));

		cancellation.IsCancellationRequested.Should().BeFalse("the iteration's own signal must terminate the run");
		result.Status.Should().Be(complete ? ExecutionStatus.Succeeded : ExecutionStatus.Failed);
		result.StepResults["slow"].Status.Should().Be(ExecutionStatus.Cancelled);
		result.StepResults["after"].Status.Should().Be(ExecutionStatus.Cancelled);
		if (complete)
		{
			result.CompletedByStep.Should().Be(signalingStep);
			result.CompletionReason.Should().Be("iteration finished the work");
		}
		else
		{
			result.StepResults["slow"].ErrorMessage.Should().Contain(signalingStep);
		}
	}
}
