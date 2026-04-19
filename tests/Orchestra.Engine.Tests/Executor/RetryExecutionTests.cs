using System.Threading.Channels;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orchestra.Engine.Tests.TestHelpers;

namespace Orchestra.Engine.Tests.Executor;

public class RetryExecutionTests
{
	private readonly IScheduler _scheduler = new OrchestrationScheduler();
	private readonly ILoggerFactory _loggerFactory = NullLoggerFactory.Instance;

	private static PromptOrchestrationStep CreateStep(
		string name = "test-step",
		RetryPolicy? retry = null,
		int? timeoutSeconds = null,
		string[]? dependsOn = null) => new()
	{
		Name = name,
		Type = OrchestrationStepType.Prompt,
		DependsOn = dependsOn ?? [],
		Parameters = [],
		SystemPrompt = "Test",
		UserPrompt = "Test",
		Model = "claude-opus-4.5",
		Retry = retry,
		TimeoutSeconds = timeoutSeconds,
	};

	private Orchestration CreateOrchestration(
		OrchestrationStep[] steps,
		RetryPolicy? defaultRetryPolicy = null,
		int? timeoutSeconds = 3600) => new()
	{
		Name = "retry-test",
		Description = "Test retry behavior",
		Steps = steps,
		DefaultRetryPolicy = defaultRetryPolicy,
		TimeoutSeconds = timeoutSeconds,
	};

	#region Basic Retry Behavior

	[Fact]
	public async Task ExecuteAsync_StepFailsThenSucceeds_RetriesAndSucceeds()
	{
		// Arrange — fail once, then succeed
		var callCount = 0;
		var agentBuilder = new MockAgentBuilder();
		agentBuilder.WithHandler((prompt, ct) =>
		{
			var count = Interlocked.Increment(ref callCount);
			if (count == 1)
			{
				var channel = Channel.CreateUnbounded<AgentEvent>();
				channel.Writer.Complete();
				return new AgentTask(channel.Reader, Task.FromException<AgentResult>(new Exception("Transient error")));
			}

			var ch = Channel.CreateUnbounded<AgentEvent>();
			var task = Task.Run(async () =>
			{
				await ch.Writer.WriteAsync(new AgentEvent { Type = AgentEventType.MessageDelta, Content = "Success" }, ct);
				ch.Writer.Complete();
				return new AgentResult { Content = "Success" };
			}, ct);
			return new AgentTask(ch.Reader, task);
		});

		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new OrchestrationExecutor(_scheduler, agentBuilder, reporter, _loggerFactory);
		var orchestration = CreateOrchestration(
			[CreateStep(retry: new RetryPolicy { MaxRetries = 3, BackoffSeconds = 0.01 })]);

		// Act
		var result = await executor.ExecuteAsync(orchestration);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Succeeded);
		result.StepResults["test-step"].Status.Should().Be(ExecutionStatus.Succeeded);
		result.StepResults["test-step"].Content.Should().Be("Success");
		callCount.Should().Be(2); // 1 initial failure + 1 retry success
		reporter.Received(1).ReportStepRetry("test-step", 1, 3, Arg.Any<string>(), Arg.Any<TimeSpan>());
	}

	[Fact]
	public async Task ExecuteAsync_StepExhaustsRetries_Fails()
	{
		// Arrange — always fail
		var callCount = 0;
		var agentBuilder = new MockAgentBuilder();
		agentBuilder.WithHandler((prompt, ct) =>
		{
			Interlocked.Increment(ref callCount);
			var channel = Channel.CreateUnbounded<AgentEvent>();
			channel.Writer.Complete();
			return new AgentTask(channel.Reader, Task.FromException<AgentResult>(new Exception("Persistent error")));
		});

		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new OrchestrationExecutor(_scheduler, agentBuilder, reporter, _loggerFactory);
		var orchestration = CreateOrchestration(
			[CreateStep(retry: new RetryPolicy { MaxRetries = 2, BackoffSeconds = 0.01 })]);

		// Act
		var result = await executor.ExecuteAsync(orchestration);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Failed);
		result.StepResults["test-step"].Status.Should().Be(ExecutionStatus.Failed);
		callCount.Should().Be(3); // 1 initial + 2 retries
		reporter.Received(2).ReportStepRetry("test-step", Arg.Any<int>(), 2, Arg.Any<string>(), Arg.Any<TimeSpan>());
	}

	[Fact]
	public async Task ExecuteAsync_NoRetryPolicy_DoesNotRetry()
	{
		// Arrange — fail once, no retry policy
		var callCount = 0;
		var agentBuilder = new MockAgentBuilder();
		agentBuilder.WithHandler((prompt, ct) =>
		{
			Interlocked.Increment(ref callCount);
			var channel = Channel.CreateUnbounded<AgentEvent>();
			channel.Writer.Complete();
			return new AgentTask(channel.Reader, Task.FromException<AgentResult>(new Exception("Error")));
		});

		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new OrchestrationExecutor(_scheduler, agentBuilder, reporter, _loggerFactory);
		var orchestration = CreateOrchestration([CreateStep()]);

		// Act
		var result = await executor.ExecuteAsync(orchestration);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Failed);
		callCount.Should().Be(1); // Only 1 attempt, no retry
		reporter.DidNotReceive().ReportStepRetry(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<TimeSpan>());
	}

	[Fact]
	public async Task ExecuteAsync_MaxRetriesZero_DoesNotRetry()
	{
		// Arrange
		var callCount = 0;
		var agentBuilder = new MockAgentBuilder();
		agentBuilder.WithHandler((prompt, ct) =>
		{
			Interlocked.Increment(ref callCount);
			var channel = Channel.CreateUnbounded<AgentEvent>();
			channel.Writer.Complete();
			return new AgentTask(channel.Reader, Task.FromException<AgentResult>(new Exception("Error")));
		});

		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new OrchestrationExecutor(_scheduler, agentBuilder, reporter, _loggerFactory);
		var orchestration = CreateOrchestration(
			[CreateStep(retry: new RetryPolicy { MaxRetries = 0 })]);

		// Act
		var result = await executor.ExecuteAsync(orchestration);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Failed);
		callCount.Should().Be(1);
		reporter.DidNotReceive().ReportStepRetry(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<TimeSpan>());
	}

	[Fact]
	public async Task ExecuteAsync_StepSucceedsFirstTime_NoRetryAttempted()
	{
		// Arrange
		var agentBuilder = new MockAgentBuilder().WithResponse("Immediate success");
		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new OrchestrationExecutor(_scheduler, agentBuilder, reporter, _loggerFactory);
		var orchestration = CreateOrchestration(
			[CreateStep(retry: new RetryPolicy { MaxRetries = 5, BackoffSeconds = 0.01 })]);

		// Act
		var result = await executor.ExecuteAsync(orchestration);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Succeeded);
		reporter.DidNotReceive().ReportStepRetry(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<TimeSpan>());
	}

	#endregion

	#region Default Retry Policy

	[Fact]
	public async Task ExecuteAsync_DefaultRetryPolicy_AppliedWhenStepHasNone()
	{
		// Arrange — fail once, then succeed; using orchestration default retry
		var callCount = 0;
		var agentBuilder = new MockAgentBuilder();
		agentBuilder.WithHandler((prompt, ct) =>
		{
			var count = Interlocked.Increment(ref callCount);
			if (count == 1)
			{
				var channel = Channel.CreateUnbounded<AgentEvent>();
				channel.Writer.Complete();
				return new AgentTask(channel.Reader, Task.FromException<AgentResult>(new Exception("Transient")));
			}

			var ch = Channel.CreateUnbounded<AgentEvent>();
			var task = Task.Run(async () =>
			{
				await ch.Writer.WriteAsync(new AgentEvent { Type = AgentEventType.MessageDelta, Content = "OK" }, ct);
				ch.Writer.Complete();
				return new AgentResult { Content = "OK" };
			}, ct);
			return new AgentTask(ch.Reader, task);
		});

		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new OrchestrationExecutor(_scheduler, agentBuilder, reporter, _loggerFactory);
		var orchestration = CreateOrchestration(
			[CreateStep()],  // no per-step retry
			defaultRetryPolicy: new RetryPolicy { MaxRetries = 3, BackoffSeconds = 0.01 });

		// Act
		var result = await executor.ExecuteAsync(orchestration);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Succeeded);
		callCount.Should().Be(2);
		reporter.Received(1).ReportStepRetry("test-step", 1, 3, Arg.Any<string>(), Arg.Any<TimeSpan>());
	}

	[Fact]
	public async Task ExecuteAsync_StepRetryOverridesDefault_StepPolicyUsed()
	{
		// Arrange — fail always; step has maxRetries=1, default has maxRetries=5
		var callCount = 0;
		var agentBuilder = new MockAgentBuilder();
		agentBuilder.WithHandler((prompt, ct) =>
		{
			Interlocked.Increment(ref callCount);
			var channel = Channel.CreateUnbounded<AgentEvent>();
			channel.Writer.Complete();
			return new AgentTask(channel.Reader, Task.FromException<AgentResult>(new Exception("Error")));
		});

		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new OrchestrationExecutor(_scheduler, agentBuilder, reporter, _loggerFactory);
		var orchestration = CreateOrchestration(
			[CreateStep(retry: new RetryPolicy { MaxRetries = 1, BackoffSeconds = 0.01 })],
			defaultRetryPolicy: new RetryPolicy { MaxRetries = 5, BackoffSeconds = 0.01 });

		// Act
		var result = await executor.ExecuteAsync(orchestration);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Failed);
		callCount.Should().Be(2); // 1 initial + 1 retry (step policy maxRetries=1, not 5)
	}

	#endregion

	#region RetryOnTimeout

	[Fact]
	public async Task ExecuteAsync_StepTimesOutAndRetryOnTimeoutDisabled_NoRetry()
	{
		// Arrange — step times out, retryOnTimeout is false
		var callCount = 0;
		var agentBuilder = new MockAgentBuilder();
		agentBuilder.WithHandler((prompt, ct) =>
		{
			Interlocked.Increment(ref callCount);
			var channel = Channel.CreateUnbounded<AgentEvent>();
			var resultTask = Task.Run(async () =>
			{
				await Task.Delay(TimeSpan.FromSeconds(10), ct);
				channel.Writer.Complete();
				return new AgentResult { Content = "unreachable" };
			}, ct);
			return new AgentTask(channel.Reader, resultTask);
		});

		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new OrchestrationExecutor(_scheduler, agentBuilder, reporter, _loggerFactory);
		var orchestration = CreateOrchestration(
			[CreateStep(
				timeoutSeconds: 1,
				retry: new RetryPolicy { MaxRetries = 3, BackoffSeconds = 0.01, RetryOnTimeout = false })]);

		// Act
		var result = await executor.ExecuteAsync(orchestration);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Failed);
		result.StepResults["test-step"].ErrorMessage.Should().Contain("timed out");
		callCount.Should().Be(1); // Only 1 attempt — no retry on timeout
		reporter.DidNotReceive().ReportStepRetry(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<TimeSpan>());
	}

	[Fact]
	public async Task ExecuteAsync_StepTimesOutAndRetryOnTimeoutEnabled_Retries()
	{
		// Arrange — first call times out, second call succeeds quickly
		var callCount = 0;
		var agentBuilder = new MockAgentBuilder();
		agentBuilder.WithHandler((prompt, ct) =>
		{
			var count = Interlocked.Increment(ref callCount);

			if (count == 1)
			{
				// First call: simulate timeout (takes longer than step timeout)
				var channel = Channel.CreateUnbounded<AgentEvent>();
				var resultTask = Task.Run(async () =>
				{
					await Task.Delay(TimeSpan.FromSeconds(10), ct);
					channel.Writer.Complete();
					return new AgentResult { Content = "unreachable" };
				}, ct);
				return new AgentTask(channel.Reader, resultTask);
			}

			// Subsequent calls: succeed immediately
			var ch = Channel.CreateUnbounded<AgentEvent>();
			var task = Task.Run(async () =>
			{
				await ch.Writer.WriteAsync(new AgentEvent { Type = AgentEventType.MessageDelta, Content = "Recovered" }, ct);
				ch.Writer.Complete();
				return new AgentResult { Content = "Recovered" };
			}, ct);
			return new AgentTask(ch.Reader, task);
		});

		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new OrchestrationExecutor(_scheduler, agentBuilder, reporter, _loggerFactory);
		var orchestration = CreateOrchestration(
			[CreateStep(
				timeoutSeconds: 1,
				retry: new RetryPolicy { MaxRetries = 3, BackoffSeconds = 0.01, RetryOnTimeout = true })]);

		// Act
		var result = await executor.ExecuteAsync(orchestration);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Succeeded);
		result.StepResults["test-step"].Content.Should().Be("Recovered");
		callCount.Should().Be(2);
		reporter.Received(1).ReportStepRetry("test-step", 1, 3, Arg.Is<string>(s => s.Contains("timed out")), Arg.Any<TimeSpan>());
	}

	#endregion

	#region Retry with Dependencies

	[Fact]
	public async Task ExecuteAsync_RetryingStepDoesNotAffectDependents_UntilResolved()
	{
		// Arrange — step A fails once then succeeds, step B depends on A
		var callCount = 0;
		var agentBuilder = new MockAgentBuilder();
		agentBuilder.WithHandler((prompt, ct) =>
		{
			var count = Interlocked.Increment(ref callCount);

			// First call (step A attempt 1) fails
			if (count == 1)
			{
				var channel = Channel.CreateUnbounded<AgentEvent>();
				channel.Writer.Complete();
				return new AgentTask(channel.Reader, Task.FromException<AgentResult>(new Exception("Transient")));
			}

			// All other calls succeed
			var content = count == 2 ? "A result" : "B result";
			var ch = Channel.CreateUnbounded<AgentEvent>();
			var task = Task.Run(async () =>
			{
				await ch.Writer.WriteAsync(new AgentEvent { Type = AgentEventType.MessageDelta, Content = content }, ct);
				ch.Writer.Complete();
				return new AgentResult { Content = content };
			}, ct);
			return new AgentTask(ch.Reader, task);
		});

		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new OrchestrationExecutor(_scheduler, agentBuilder, reporter, _loggerFactory);
		var orchestration = CreateOrchestration([
			CreateStep(name: "A", retry: new RetryPolicy { MaxRetries = 2, BackoffSeconds = 0.01 }),
			CreateStep(name: "B", dependsOn: ["A"]),
		]);

		// Act
		var result = await executor.ExecuteAsync(orchestration);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Succeeded);
		result.StepResults["A"].Status.Should().Be(ExecutionStatus.Succeeded);
		result.StepResults["A"].Content.Should().Be("A result");
		result.StepResults["B"].Status.Should().Be(ExecutionStatus.Succeeded);
		result.StepResults["B"].Content.Should().Be("B result");
	}

	[Fact]
	public async Task ExecuteAsync_RetryExhaustedCascadesToDependents()
	{
		// Arrange — step A always fails, step B depends on A and should be skipped
		var agentBuilder = new MockAgentBuilder();
		agentBuilder.WithHandler((prompt, ct) =>
		{
			var channel = Channel.CreateUnbounded<AgentEvent>();
			channel.Writer.Complete();
			return new AgentTask(channel.Reader, Task.FromException<AgentResult>(new Exception("Always fails")));
		});

		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new OrchestrationExecutor(_scheduler, agentBuilder, reporter, _loggerFactory);
		var orchestration = CreateOrchestration([
			CreateStep(name: "A", retry: new RetryPolicy { MaxRetries = 1, BackoffSeconds = 0.01 }),
			CreateStep(name: "B", dependsOn: ["A"]),
		]);

		// Act
		var result = await executor.ExecuteAsync(orchestration);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Failed);
		result.StepResults["A"].Status.Should().Be(ExecutionStatus.Failed);
		result.StepResults["B"].Status.Should().Be(ExecutionStatus.Skipped);
	}

	#endregion

	#region Cancellation During Retry

	[Fact]
	public async Task ExecuteAsync_CancellationDuringRetryDelay_StopsRetrying()
	{
		// Arrange — fail always, cancel during retry delay.
		// The executor's TryLaunchStep catches OperationCanceledException and marks
		// the step as Cancelled, so no exception is thrown to the caller.
		var callCount = 0;
		using var cts = new CancellationTokenSource();
		var agentBuilder = new MockAgentBuilder();
		agentBuilder.WithHandler((prompt, ct) =>
		{
			var count = Interlocked.Increment(ref callCount);
			if (count == 1)
			{
				// First attempt fails, schedule cancellation during the retry delay
				_ = Task.Run(async () =>
				{
					await Task.Delay(50);
					cts.Cancel();
				});
			}

			var channel = Channel.CreateUnbounded<AgentEvent>();
			channel.Writer.Complete();
			return new AgentTask(channel.Reader, Task.FromException<AgentResult>(new Exception("Error")));
		});

		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new OrchestrationExecutor(_scheduler, agentBuilder, reporter, _loggerFactory);
		var orchestration = CreateOrchestration(
			[CreateStep(retry: new RetryPolicy { MaxRetries = 10, BackoffSeconds = 5.0 })],
			timeoutSeconds: null);

		// Act
		var result = await executor.ExecuteAsync(orchestration, cancellationToken: cts.Token);

		// Assert — step should be cancelled, not all 10 retries attempted
		result.Status.Should().Be(ExecutionStatus.Cancelled);
		result.StepResults["test-step"].Status.Should().Be(ExecutionStatus.Cancelled);
		callCount.Should().BeLessThan(10); // Should not have exhausted all retries
	}

	#endregion

	#region Multiple Retry Attempts Reporting

	[Fact]
	public async Task ExecuteAsync_MultipleRetries_ReportsEachAttempt()
	{
		// Arrange — fail 3 times, succeed on 4th
		var callCount = 0;
		var agentBuilder = new MockAgentBuilder();
		agentBuilder.WithHandler((prompt, ct) =>
		{
			var count = Interlocked.Increment(ref callCount);
			if (count <= 3)
			{
				var channel = Channel.CreateUnbounded<AgentEvent>();
				channel.Writer.Complete();
				return new AgentTask(channel.Reader, Task.FromException<AgentResult>(new Exception($"Error {count}")));
			}

			var ch = Channel.CreateUnbounded<AgentEvent>();
			var task = Task.Run(async () =>
			{
				await ch.Writer.WriteAsync(new AgentEvent { Type = AgentEventType.MessageDelta, Content = "Finally" }, ct);
				ch.Writer.Complete();
				return new AgentResult { Content = "Finally" };
			}, ct);
			return new AgentTask(ch.Reader, task);
		});

		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new OrchestrationExecutor(_scheduler, agentBuilder, reporter, _loggerFactory);
		var orchestration = CreateOrchestration(
			[CreateStep(retry: new RetryPolicy { MaxRetries = 5, BackoffSeconds = 0.01 })]);

		// Act
		var result = await executor.ExecuteAsync(orchestration);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Succeeded);
		callCount.Should().Be(4);
		reporter.Received(1).ReportStepRetry("test-step", 1, 5, Arg.Any<string>(), Arg.Any<TimeSpan>());
		reporter.Received(1).ReportStepRetry("test-step", 2, 5, Arg.Any<string>(), Arg.Any<TimeSpan>());
		reporter.Received(1).ReportStepRetry("test-step", 3, 5, Arg.Any<string>(), Arg.Any<TimeSpan>());
	}

	#endregion

	#region ClientUnhealthy Short-Circuit

	/// <summary>Test-only stand-in for the agent-implementation exception that signals an unhealthy client.</summary>
	private sealed class FakeClientUnhealthyException : Exception, IAgentClientUnhealthyException
	{
		public FakeClientUnhealthyException(string message) : base(message) { }
		public string TriggeringSessionId => "test-trigger-session";
		public string TriggeringFailureReason => "test-trigger-reason";
		public string? ProbeDetails => "test-probe-details";
	}

	[Fact]
	public async Task ExecuteAsync_ClientUnhealthyOnFirstAttempt_DoesNotRetry()
	{
		// Arrange — first attempt throws an IAgentClientUnhealthyException; if retry logic
		// were to fire we'd see callCount > 1. The executor MUST short-circuit because retries
		// on a dead client are guaranteed to fail.
		var callCount = 0;
		var agentBuilder = new MockAgentBuilder();
		agentBuilder.WithHandler((prompt, ct) =>
		{
			Interlocked.Increment(ref callCount);
			var channel = Channel.CreateUnbounded<AgentEvent>();
			channel.Writer.Complete();
			return new AgentTask(channel.Reader,
				Task.FromException<AgentResult>(new FakeClientUnhealthyException("client is dead")));
		});

		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new OrchestrationExecutor(_scheduler, agentBuilder, reporter, _loggerFactory);
		var orchestration = CreateOrchestration(
			[CreateStep(retry: new RetryPolicy { MaxRetries = 5, BackoffSeconds = 0.01 })]);

		// Act
		var result = await executor.ExecuteAsync(orchestration);

		// Assert — exactly one attempt, no retries reported, category is ClientUnhealthy.
		result.Status.Should().Be(ExecutionStatus.Failed);
		callCount.Should().Be(1);
		result.StepResults["test-step"].ErrorCategory.Should().Be(StepErrorCategory.ClientUnhealthy);
		reporter.DidNotReceive().ReportStepRetry(
			Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<TimeSpan>());
	}

	[Fact]
	public async Task ExecuteAsync_ClientUnhealthyWrappedInInnerException_StillShortCircuits()
	{
		// Arrange — wrap the unhealthy exception so the executor must walk InnerException chain.
		var callCount = 0;
		var agentBuilder = new MockAgentBuilder();
		agentBuilder.WithHandler((prompt, ct) =>
		{
			Interlocked.Increment(ref callCount);
			var channel = Channel.CreateUnbounded<AgentEvent>();
			channel.Writer.Complete();
			var wrapped = new InvalidOperationException(
				"agent crashed",
				new FakeClientUnhealthyException("client is dead"));
			return new AgentTask(channel.Reader, Task.FromException<AgentResult>(wrapped));
		});

		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new OrchestrationExecutor(_scheduler, agentBuilder, reporter, _loggerFactory);
		var orchestration = CreateOrchestration(
			[CreateStep(retry: new RetryPolicy { MaxRetries = 4, BackoffSeconds = 0.01 })]);

		// Act
		var result = await executor.ExecuteAsync(orchestration);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Failed);
		callCount.Should().Be(1);
		result.StepResults["test-step"].ErrorCategory.Should().Be(StepErrorCategory.ClientUnhealthy);
		reporter.DidNotReceive().ReportStepRetry(
			Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<TimeSpan>());
	}

	[Fact]
	public async Task ExecuteAsync_GenericFailure_StillRetries_RegressionGuard()
	{
		// Arrange — a plain Exception (NOT IAgentClientUnhealthyException) MUST still retry.
		// This guards against the short-circuit accidentally firing on every failure.
		var callCount = 0;
		var agentBuilder = new MockAgentBuilder();
		agentBuilder.WithHandler((prompt, ct) =>
		{
			Interlocked.Increment(ref callCount);
			var channel = Channel.CreateUnbounded<AgentEvent>();
			channel.Writer.Complete();
			return new AgentTask(channel.Reader,
				Task.FromException<AgentResult>(new InvalidOperationException("transient model error")));
		});

		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new OrchestrationExecutor(_scheduler, agentBuilder, reporter, _loggerFactory);
		var orchestration = CreateOrchestration(
			[CreateStep(retry: new RetryPolicy { MaxRetries = 3, BackoffSeconds = 0.01 })]);

		// Act
		var result = await executor.ExecuteAsync(orchestration);

		// Assert — initial attempt + 3 retries = 4 total calls.
		result.Status.Should().Be(ExecutionStatus.Failed);
		callCount.Should().Be(4);
		result.StepResults["test-step"].ErrorCategory.Should().NotBe(StepErrorCategory.ClientUnhealthy);
	}

	#endregion
}
