using System.Collections.Concurrent;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orchestra.Engine.Tests.TestHelpers;

namespace Orchestra.Engine.Tests.Executor;

public class CheckpointConcurrencyTests
{
	[Fact]
	public async Task ParallelCheckpoint_DoesNotMarkAnActiveCheckerLoopComplete_AndResumeFinishesIt()
	{
		var loopStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var independentSaved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var calls = new ConcurrentDictionary<string, int>();
		CheckpointData? latest = null;
		var store = Substitute.For<ICheckpointStore>();
		store.SaveCheckpointAsync(Arg.Any<CheckpointData>(), Arg.Any<CancellationToken>()).Returns(call =>
		{
			latest = call.Arg<CheckpointData>();
			if (latest.CompletedSteps.ContainsKey("independent"))
				independentSaved.TrySetResult();
			return Task.CompletedTask;
		});
		var registry = new StepExecutorRegistry().Register(new DelegateStepExecutor(OrchestrationStepType.Prompt, async (step, _, token) =>
		{
			var attempt = calls.AddOrUpdate(step.Name, 1, (_, count) => count + 1);
			if (step.Name == "draft" && attempt == 2)
			{
				loopStarted.TrySetResult();
				await Task.Delay(Timeout.Infinite, token);
			}
			if (step.Name == "independent")
				await loopStarted.Task.WaitAsync(token);
			return ExecutionResult.Succeeded(step.Name == "check" && attempt == 1 ? "refine" : "PASS");
		}));
		var definition = OrchestrationParser.ParseOrchestration("""
			{"name":"checkpoint-loop","description":"Interrupt a loop beside a completed branch","defaultModel":"test","steps":[
			 {"name":"draft","type":"Prompt","systemPrompt":"","userPrompt":"draft"},
			 {"name":"check","type":"Prompt","systemPrompt":"","userPrompt":"check","dependsOn":["draft"],
			  "loop":{"target":"draft","exitPattern":"PASS","maxIterations":2}},
			 {"name":"independent","type":"Prompt","systemPrompt":"","userPrompt":"independent"}
			]}
			""", []);
		var executor = new OrchestrationExecutor(new OrchestrationScheduler(), new MockAgentBuilder(), NullOrchestrationReporter.Instance,
			NullLoggerFactory.Instance, checkpointStore: store, stepExecutorRegistry: registry);
		using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
		var running = executor.ExecuteAsync(definition, cancellationToken: cancellation.Token,
			resolveExternalCancellationCause: () => CancellationDetails.HostShutdown());
		await independentSaved.Task.WaitAsync(cancellation.Token);
		await cancellation.CancelAsync();
		(await running.WaitAsync(TimeSpan.FromSeconds(5))).Status.Should().Be(ExecutionStatus.Cancelled);

		latest!.CompletedSteps.Should().ContainKey("draft").And.ContainKey("independent");
		latest.CompletedSteps.Should().NotContainKey("check", "a successful intermediate response is not a completed checker loop");
		var restored = latest;
		var resumed = await executor.ResumeAsync(definition, restored).WaitAsync(TimeSpan.FromSeconds(5));
		resumed.Status.Should().Be(ExecutionStatus.Succeeded);
		calls["check"].Should().Be(2, "the interrupted checker must execute again after restart");
		calls["independent"].Should().Be(1, "finished parallel work remains checkpointed");
	}
}
