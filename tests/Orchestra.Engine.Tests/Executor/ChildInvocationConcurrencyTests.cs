using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Orchestra.Engine.Tests.TestHelpers;

namespace Orchestra.Engine.Tests.Executor;

public class ChildInvocationConcurrencyTests
{
	private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
	private static OrchestrationExecutionContext Context() => new()
	{
		OrchestrationInfo = new("parent", "1", "parent-run", DateTimeOffset.UtcNow),
	};
	private static OrchestrationInvocationStep Step(bool fanOut = false, int? cap = null, string? handler = null) => new()
	{
		Name = "invoke", Type = OrchestrationStepType.Orchestration, OrchestrationName = "child",
		ForEach = fanOut ? "[0,1,2,3,4,5]" : null, ItemParameter = "item", MaxConcurrency = cap,
		ChildParameters = new() { ["raw"] = "input" }, InputHandlerPrompt = handler,
	};
	private static ChildOrchestrationResult Terminal(string id, ExecutionStatus status = ExecutionStatus.Succeeded) => new()
	{
		ExecutionId = id, OrchestrationId = "child", OrchestrationName = "child", Status = status,
		FinalContent = id, StartedAt = DateTimeOffset.UtcNow, CompletedAt = DateTimeOffset.UtcNow,
	};
	private static ChildOrchestrationHandle Handle(string id, Task<ChildOrchestrationResult>? completion = null) => new()
	{
		ExecutionId = id, OrchestrationId = "child", OrchestrationName = "child", StartedAt = DateTimeOffset.UtcNow,
		Reporter = NullOrchestrationReporter.Instance, Completion = completion ?? Task.FromResult(Terminal(id)),
	};
	private static OrchestrationStepExecutor Executor(Func<ChildLaunchRequest, CancellationToken, Task<ChildOrchestrationHandle>> launch, AgentBuilder? builder = null)
		=> new(new Launcher(launch), builder ?? new MockAgentBuilder(), NullOrchestrationReporter.Instance, NullLogger<OrchestrationStepExecutor>.Instance);

	[Fact]
	public async Task BoundedFanOut_KeepsTheCapAndInputOrderWhenChildrenCompleteOutOfOrder()
	{
		var firstTwo = Gate();
		var release = Gate();
		var active = 0;
		var peak = 0;
		var launched = 0;
		var executor = Executor((request, _) =>
		{
			var id = request.Parameters!["item"];
			var current = Interlocked.Increment(ref active);
			int prior;
			do { prior = Volatile.Read(ref peak); } while (current > prior && Interlocked.CompareExchange(ref peak, current, prior) != prior);
			if (Interlocked.Increment(ref launched) == 2) firstTwo.TrySetResult();
			async Task<ChildOrchestrationResult> CompleteAsync()
			{
				await release.Task;
				await Task.Delay(id == "0" ? 30 : 1);
				Interlocked.Decrement(ref active);
				return Terminal(id);
			}
			return Task.FromResult(Handle(id, CompleteAsync()));
		});
		var run = executor.ExecuteAsync(Step(fanOut: true, cap: 2), Context());
		try
		{
			await firstTwo.Task.WaitAsync(TimeSpan.FromSeconds(5));
			Volatile.Read(ref launched).Should().Be(2, "queued items must wait for a child slot");
		}
		finally
		{
			release.TrySetResult();
			await run.WaitAsync(TimeSpan.FromSeconds(5));
		}
		peak.Should().Be(2);
		using var json = JsonDocument.Parse((await run).Content);
		json.RootElement.GetProperty("results").EnumerateArray().Select(item => item.GetProperty("executionId").GetString())
			.Should().Equal("0", "1", "2", "3", "4", "5");
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task CallerCancellation_IsNotConvertedToFailureOrSuccessfulFanOut(bool fanOut)
	{
		var started = Gate();
		using var cancellation = new CancellationTokenSource();
		var executor = Executor((_, token) =>
		{
			async Task<ChildOrchestrationResult> CompleteAsync()
			{
				try { await Task.Delay(Timeout.Infinite, token); }
				catch (OperationCanceledException) { }
				return Terminal("cancelled", ExecutionStatus.Cancelled);
			}
			started.TrySetResult();
			return Task.FromResult(Handle("cancelled", CompleteAsync()));
		});
		var running = executor.ExecuteAsync(Step(fanOut), Context(), cancellation.Token);
		await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
		await cancellation.CancelAsync();
		var run = () => running;

		await run.Should().ThrowAsync<OperationCanceledException>();
	}

	[Fact]
	public async Task FanOut_InputHandlerIsAppliedSeparatelyToEachItem()
	{
		var transformed = new ConcurrentDictionary<string, string>();
		var builder = new MockAgentBuilder().WithResponse("""{"normalized":"yes"}""");
		var executor = Executor(async (request, token) =>
		{
			request.PreExecutionParameterTransform.Should().NotBeNull("inputHandlerPrompt also applies in forEach mode");
			var shaped = await request.PreExecutionParameterTransform!(token);
			transformed[request.Parameters!["item"]] = shaped!["normalized"];
			return Handle(request.Parameters["item"]);
		}, builder);

		var result = await executor.ExecuteAsync(Step(fanOut: true, cap: 2, handler: "Normalize each item"), Context());

		result.Status.Should().Be(ExecutionStatus.Succeeded);
		transformed.Should().HaveCount(6);
		transformed.Values.Should().OnlyContain(value => value == "yes");
	}

	[Fact]
	public async Task UnexpectedLaunchFailure_DoesNotLoseOtherItemsInABoundedFanOut()
	{
		var calls = 0;
		var executor = Executor((request, _) =>
		{
			calls++;
			var item = request.Parameters!["item"];
			if (item == "1") throw new IOException("launch transport failed");
			return Task.FromResult(Handle(item));
		});

		var result = await executor.ExecuteAsync(Step(fanOut: true, cap: 1), Context());

		result.Status.Should().Be(ExecutionStatus.Succeeded, "continueOnItemFailure defaults to true");
		calls.Should().Be(6);
		using var json = JsonDocument.Parse(result.Content);
		json.RootElement.GetProperty("failed").GetInt32().Should().Be(1);
		json.RootElement.GetProperty("succeeded").GetInt32().Should().Be(5);
	}

	[Fact]
	public async Task InputHandler_DrainsBoundedEventsBeforeWaitingForTheFinalResult()
	{
		Func<CancellationToken, Task<Dictionary<string, string>?>>? transform = null;
		var channel = Channel.CreateBounded<AgentEvent>(1);
		var builder = new MockAgentBuilder().WithHandler((_, token) =>
		{
			async Task<AgentResult> ProduceAsync()
			{
				try
				{
					for (var i = 0; i < 3; i++)
						await channel.Writer.WriteAsync(new() { Type = AgentEventType.MessageDelta, Content = "chunk" }, token);
					return new() { Content = "{\"ready\":\"yes\"}" };
				}
				finally { channel.Writer.TryComplete(); }
			}
			return new AgentTask(channel.Reader, ProduceAsync());
		});
		var executor = Executor((request, _) =>
		{
			transform = request.PreExecutionParameterTransform;
			return Task.FromResult(Handle("input"));
		}, builder);
		await executor.ExecuteAsync(Step(handler: "Normalize"), Context());
		var running = transform!(CancellationToken.None);
		try
		{
			var result = await running.WaitAsync(TimeSpan.FromSeconds(3));
			result!["ready"].Should().Be("yes");
		}
		finally
		{
			channel.Writer.TryComplete();
			await running.WaitAsync(TimeSpan.FromSeconds(5));
		}
	}

	[Fact]
	public async Task InputHandler_ForwardsCancellationAndDoesNotSwallowIt()
	{
		Func<CancellationToken, Task<Dictionary<string, string>?>>? transform = null;
		using var cancellation = new CancellationTokenSource();
		await cancellation.CancelAsync();
		var observed = CancellationToken.None;
		var builder = new MockAgentBuilder().WithHandler((_, token) =>
		{
			observed = token;
			var channel = Channel.CreateUnbounded<AgentEvent>();
			channel.Writer.TryComplete();
			return new AgentTask(channel.Reader, Task.FromCanceled<AgentResult>(cancellation.Token));
		});
		var executor = Executor((request, _) =>
		{
			transform = request.PreExecutionParameterTransform;
			return Task.FromResult(Handle("input"));
		}, builder);
		await executor.ExecuteAsync(Step(handler: "Normalize"), Context());
		var run = () => transform!(cancellation.Token);

		await run.Should().ThrowAsync<OperationCanceledException>();
		observed.Should().Be(cancellation.Token);
	}

	private sealed class Launcher(Func<ChildLaunchRequest, CancellationToken, Task<ChildOrchestrationHandle>> launch) : IChildOrchestrationLauncher
	{
		public Task<ChildOrchestrationHandle> LaunchAsync(ChildLaunchRequest request, CancellationToken cancellationToken = default) => launch(request, cancellationToken);
	}
}
