using System.Collections.Concurrent;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orchestra.Engine.Tests.TestHelpers;

namespace Orchestra.Engine.Tests.Executor;

public class DagSchedulingTests
{
	private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);

	private static TransformOrchestrationStep Step(string name, params string[] dependencies) => new()
	{
		Name = name, Type = OrchestrationStepType.Transform, Template = name, DependsOn = dependencies,
	};

	private static Orchestration Definition(params OrchestrationStep[] steps) => new()
	{
		Name = "scheduling", Description = "Controlled DAG execution", Steps = steps, TimeoutSeconds = null,
	};

	private static OrchestrationExecutor Executor(
		Func<OrchestrationStep, OrchestrationExecutionContext, CancellationToken, Task<ExecutionResult>> execute,
		ICheckpointStore? checkpoints = null, IRunStore? runs = null)
		=> new(new OrchestrationScheduler(), new MockAgentBuilder(), NullOrchestrationReporter.Instance, NullLoggerFactory.Instance,
			checkpointStore: checkpoints, runStore: runs,
			stepExecutorRegistry: new StepExecutorRegistry().Register(new DelegateStepExecutor(OrchestrationStepType.Transform, execute)));

	[Fact]
	public async Task Join_WaitsForEveryDependencyToFinishCheckpointing()
	{
		var savingA = Gate();
		var releaseA = Gate();
		var bProducedOutput = Gate();
		var joinStarted = Gate();
		var checkpoints = Substitute.For<ICheckpointStore>();
		checkpoints.SaveCheckpointAsync(Arg.Any<CheckpointData>(), Arg.Any<CancellationToken>()).Returns(async call =>
		{
			var checkpoint = call.Arg<CheckpointData>();
			if (checkpoint.CompletedSteps.ContainsKey("a") && !checkpoint.CompletedSteps.ContainsKey("b"))
			{
				savingA.TrySetResult();
				await releaseA.Task;
			}
		});
		var executor = Executor(async (step, _, token) =>
		{
			if (step.Name == "b")
			{
				await savingA.Task.WaitAsync(token);
				bProducedOutput.TrySetResult();
			}
			if (step.Name == "join")
				joinStarted.TrySetResult();
			return ExecutionResult.Succeeded(step.Name);
		}, checkpoints);
		using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
		var run = executor.ExecuteAsync(Definition(Step("a"), Step("b"), Step("join", "a", "b")), cancellationToken: cancellation.Token);
		try
		{
			await bProducedOutput.Task.WaitAsync(cancellation.Token);
			var first = await Task.WhenAny(joinStarted.Task, Task.Delay(150, cancellation.Token));
			first.Should().NotBe(joinStarted.Task, "a result being visible is not completion: a dependency's checkpoint is still in flight");
		}
		finally
		{
			releaseA.TrySetResult();
			await run.WaitAsync(TimeSpan.FromSeconds(10));
		}
		(await run).Status.Should().Be(ExecutionStatus.Succeeded);
		joinStarted.Task.IsCompleted.Should().BeTrue();
	}

	[Fact]
	public async Task MissingExecutor_ProducesFailureAndSettlesDependents()
	{
		var executor = new OrchestrationExecutor(new OrchestrationScheduler(), new MockAgentBuilder(),
			NullOrchestrationReporter.Instance, NullLoggerFactory.Instance, stepExecutorRegistry: new StepExecutorRegistry());
		var result = await executor.ExecuteAsync(Definition(Step("missing"), Step("after", "missing")))
			.WaitAsync(TimeSpan.FromSeconds(3));

		result.Status.Should().Be(ExecutionStatus.Failed);
		result.StepResults["missing"].ErrorMessage.Should().Contain("No step executor registered");
		result.StepResults["after"].Status.Should().Be(ExecutionStatus.Skipped);
	}

	[Fact]
	public async Task ThrowingExecutor_ObservesFailFastPolicy()
	{
		var slowStarted = Gate();
		var executor = Executor(async (step, _, token) =>
		{
			if (step.Name == "boom")
			{
				await slowStarted.Task.WaitAsync(token);
				throw new InvalidOperationException("plugin failed");
			}
			slowStarted.TrySetResult();
			await Task.Delay(Timeout.Infinite, token);
			return ExecutionResult.Succeeded("unreachable");
		});
		using var cancellation = new CancellationTokenSource();
		var definition = new Orchestration
		{
			Name = "plugin-stop", Description = "Plugin exceptions use normal failure policy", TimeoutSeconds = null,
			OnStepFailure = StepFailurePolicy.Stop, Steps = [Step("boom"), Step("slow"), Step("after", "boom")],
		};
		var run = executor.ExecuteAsync(definition, cancellationToken: cancellation.Token);
		try
		{
			var result = await run.WaitAsync(TimeSpan.FromSeconds(3));
			result.Status.Should().Be(ExecutionStatus.Failed);
			result.StepResults["slow"].Status.Should().Be(ExecutionStatus.Cancelled);
			result.StepResults["slow"].ErrorMessage.Should().Contain("onStepFailure is 'stop'");
			result.StepResults["after"].Status.Should().Be(ExecutionStatus.Cancelled);
		}
		finally
		{
			await cancellation.CancelAsync();
			await run.WaitAsync(TimeSpan.FromSeconds(5));
		}
	}

	[Fact]
	public async Task Cancellation_JoinsActiveWorkersAndPreservesPartialResultsWithoutPlanCapture()
	{
		var slowStarted = Gate();
		var cleanupStarted = Gate();
		var releaseCleanup = Gate();
		var runs = Substitute.For<IRunStore>();
		OrchestrationRunRecord? saved = null;
		runs.SaveRunAsync(Arg.Do<OrchestrationRunRecord>(r => saved = r), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
		var executor = Executor(async (step, _, token) =>
		{
			if (step.Name == "slow")
				slowStarted.TrySetResult();
			try
			{
				await slowStarted.Task.WaitAsync(token);
				await Task.Delay(Timeout.Infinite, token);
			}
			catch (OperationCanceledException) when (step.Name == "slow")
			{
				cleanupStarted.TrySetResult();
				await releaseCleanup.Task;
				throw new StepExecutionCanceledException("Cancelled", ExecutionResult.Cancelled() with
				{
					RawContent = "partial work", Trace = new StepExecutionTrace { FinalResponse = "partial work" },
				}, null, token);
			}
			return ExecutionResult.Succeeded("unreachable");
		}, runs: runs);
		using var cancellation = new CancellationTokenSource();
		var run = executor.ExecuteAsync(Definition(Step("slow"), Step("fast"), Step("after", "slow")), cancellationToken: cancellation.Token);
		try
		{
			await slowStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
			await cancellation.CancelAsync();
			await cleanupStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
			(await Task.WhenAny(run, Task.Delay(150))).Should().NotBe(run, "worker cleanup owns the final partial trace");
		}
		finally
		{
			releaseCleanup.TrySetResult();
			await run.WaitAsync(TimeSpan.FromSeconds(5));
		}

		(await run).StepResults["slow"].RawContent.Should().Be("partial work");
		saved!.StepRecords["slow"].RawContent.Should().Be("partial work");
		saved.StepRecords["slow"].Trace!.FinalResponse.Should().Be("partial work");
		saved.StepRecords["slow"].ErrorMessage.Should().Contain("cancelled by caller");
	}

	[Fact]
	public async Task LateCompletionSignal_CannotReplaceTheSignalThatAlreadyStoppedTheRun()
	{
		var lateStarted = Gate();
		var cancellationObserved = Gate();
		var releaseLate = Gate();
		var executor = Executor(async (step, _, token) =>
		{
			if (step.Name == "late")
			{
				using var registration = token.Register(() => cancellationObserved.TrySetResult());
				lateStarted.TrySetResult();
				await releaseLate.Task; // An already in-flight provider response settles after cancellation.
			}
			else
			{
				await lateStarted.Task.WaitAsync(token);
			}
			return ExecutionResult.Succeeded(step.Name) with
			{
				OrchestrationCompleteRequested = true, OrchestrationCompleteStatus = ExecutionStatus.Succeeded,
				OrchestrationCompleteReason = step.Name,
			};
		});
		var run = executor.ExecuteAsync(Definition(Step("first"), Step("late")));
		try
		{
			await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
		}
		finally
		{
			releaseLate.TrySetResult();
			await run.WaitAsync(TimeSpan.FromSeconds(5));
		}
		(await run).CompletedByStep.Should().Be("first");
		(await run).CompletionReason.Should().Be("first");
	}

	[Fact]
	public async Task ValidatedCaseInsensitiveStepReferences_ResolveAtRuntime()
	{
		var executor = new OrchestrationExecutor(new OrchestrationScheduler(), new MockAgentBuilder(),
			NullOrchestrationReporter.Instance, NullLoggerFactory.Instance);
		var definition = Definition(
			new TransformOrchestrationStep { Name = "source", Type = OrchestrationStepType.Transform, Template = "{\"value\":42}" },
			new TransformOrchestrationStep { Name = "result", Type = OrchestrationStepType.Transform, DependsOn = ["source"], Template = "{{SOURCE.output.value}} / {{SOURCE.status}}" });

		TemplateExpressionValidator.ValidateOrchestration(definition).IsValid.Should().BeTrue();
		var result = await executor.ExecuteAsync(definition);

		result.StepResults["result"].Content.Should().Be("42 / succeeded");
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task WideAndDeepDags_ExecuteEachStepOnceAndPreserveDependencyOrder(bool chain)
	{
		const int count = 512;
		var calls = new ConcurrentDictionary<string, int>();
		var executor = Executor((step, context, _) =>
		{
			calls.AddOrUpdate(step.Name, 1, (_, value) => value + 1);
			foreach (var dependency in step.DependsOn)
				context.GetResult(dependency).Content.Should().Be(dependency);
			return Task.FromResult(ExecutionResult.Succeeded(step.Name));
		});
		var steps = Enumerable.Range(0, count)
			.Select(i => (OrchestrationStep)Step($"s{i}", chain && i > 0 ? [$"s{i - 1}"] : []))
			.Append(Step("join", chain ? [$"s{count - 1}"] : Enumerable.Range(0, count).Select(i => $"s{i}").ToArray()))
			.ToArray();

		var result = await executor.ExecuteAsync(Definition(steps)).WaitAsync(TimeSpan.FromSeconds(20));

		result.Status.Should().Be(ExecutionStatus.Succeeded);
		result.Results.Keys.Should().Equal("join");
		calls.Should().HaveCount(count + 1);
		calls.Values.Should().OnlyContain(value => value == 1);
	}
}
