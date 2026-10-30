using FluentAssertions;
using Orchestra.Engine.Tests.TestHelpers;

namespace Orchestra.Engine.Tests.Executor;

public class TraceSnapshotConcurrencyTests
{
	[Fact]
	public async Task LiveTraceProjection_HandlesConcurrentStepResultChanges()
	{
		var context = new OrchestrationExecutionContext { OrchestrationInfo = new("trace", "1", "run", DateTimeOffset.UtcNow) };
		context.AddResult("stable", ExecutionResult.Succeeded("stable"));
		var result = ExecutionResult.Succeeded("changing");
		var step = TestOrchestrations.CreatePromptStep("viewer");
		var trace = new StepExecutionTrace();
		using var stop = new CancellationTokenSource();
		var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var writer = Task.Factory.StartNew(() =>
		{
			started.TrySetResult();
			while (!stop.IsCancellationRequested)
			{
				context.AddResult("changing", result);
				context.ClearResult("changing");
			}
		}, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
		await started.Task;
		try
		{
			for (var i = 0; i < 5000; i++)
			{
				var snapshot = trace.WithContext(context, step);
				snapshot.AccessibleStepData["stable"].Output.Should().Be("stable");
			}
		}
		finally { await stop.CancelAsync(); await writer; }
	}
}
