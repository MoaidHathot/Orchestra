using System.Threading.Channels;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orchestra.Engine.Tests.TestHelpers;

namespace Orchestra.Engine.Tests.Executor;

public class OrchestrationTimeoutTests
{
	private readonly IScheduler _scheduler = new OrchestrationScheduler();
	private readonly ILoggerFactory _loggerFactory = NullLoggerFactory.Instance;

	[Fact]
	public async Task ExecuteAsync_OrchestrationTimeoutExceeded_StepsMarkedAsCancelled()
	{
		// Arrange — step takes longer than orchestration timeout.
		// The executor catches OperationCanceledException inside TryLaunchStep and marks
		// the step as Cancelled, so the overall result is Cancelled (no actual Failed steps).
		var agentBuilder = new MockAgentBuilder();
		agentBuilder.WithHandler((prompt, ct) =>
		{
			var channel = Channel.CreateUnbounded<AgentEvent>();
			var resultTask = Task.Run(async () =>
			{
				await Task.Delay(TimeSpan.FromSeconds(30), ct);
				channel.Writer.Complete();
				return new AgentResult { Content = "unreachable" };
			}, ct);
			return new AgentTask(channel.Reader, resultTask);
		});

		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new OrchestrationExecutor(_scheduler, agentBuilder, reporter, _loggerFactory);
		var orchestration = new Orchestration
		{
			Name = "timeout-orch",
			Description = "Orchestration times out",
			TimeoutSeconds = 1,
			Steps =
			[
				new PromptOrchestrationStep
				{
					Name = "slow-step",
					Type = OrchestrationStepType.Prompt,
					DependsOn = [],
					Parameters = [],
					SystemPrompt = "Test",
					UserPrompt = "Test",
					Model = "claude-opus-4.5",
				}
			]
		};

		// Act
		var result = await executor.ExecuteAsync(orchestration);

		// Assert — step should be marked as Cancelled
		result.Status.Should().Be(ExecutionStatus.Cancelled);
		result.StepResults["slow-step"].Status.Should().Be(ExecutionStatus.Cancelled);
		result.StepResults["slow-step"].ErrorMessage.Should().Contain("Cancelled");
	}

	[Fact]
	public async Task ExecuteAsync_OrchestrationCompletesBeforeTimeout_Succeeds()
	{
		// Arrange
		var agentBuilder = new MockAgentBuilder().WithResponse("Quick result");
		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new OrchestrationExecutor(_scheduler, agentBuilder, reporter, _loggerFactory);
		var orchestration = new Orchestration
		{
			Name = "fast-orch",
			Description = "Completes before timeout",
			TimeoutSeconds = 60,
			Steps =
			[
				new PromptOrchestrationStep
				{
					Name = "fast-step",
					Type = OrchestrationStepType.Prompt,
					DependsOn = [],
					Parameters = [],
					SystemPrompt = "Test",
					UserPrompt = "Test",
					Model = "claude-opus-4.5",
				}
			]
		};

		// Act
		var result = await executor.ExecuteAsync(orchestration);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Succeeded);
		result.StepResults["fast-step"].Content.Should().Be("Quick result");
	}

	[Fact]
	public async Task ExecuteAsync_NullTimeoutSeconds_NoTimeoutApplied()
	{
		// Arrange — null timeout means no orchestration-level timeout
		var agentBuilder = new MockAgentBuilder();
		agentBuilder.WithHandler((prompt, ct) =>
		{
			var channel = Channel.CreateUnbounded<AgentEvent>();
			var resultTask = Task.Run(async () =>
			{
				// Small delay — should be fine without timeout
				await Task.Delay(100, ct);
				await channel.Writer.WriteAsync(new AgentEvent { Type = AgentEventType.MessageDelta, Content = "Done" }, ct);
				channel.Writer.Complete();
				return new AgentResult { Content = "Done" };
			}, ct);
			return new AgentTask(channel.Reader, resultTask);
		});

		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new OrchestrationExecutor(_scheduler, agentBuilder, reporter, _loggerFactory);
		var orchestration = new Orchestration
		{
			Name = "no-timeout",
			Description = "No orchestration timeout",
			TimeoutSeconds = null,
			Steps =
			[
				new PromptOrchestrationStep
				{
					Name = "step1",
					Type = OrchestrationStepType.Prompt,
					DependsOn = [],
					Parameters = [],
					SystemPrompt = "Test",
					UserPrompt = "Test",
					Model = "claude-opus-4.5",
				}
			]
		};

		// Act
		var result = await executor.ExecuteAsync(orchestration);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Succeeded);
	}

	[Fact]
	public async Task ExecuteAsync_ZeroTimeoutSeconds_NoTimeoutApplied()
	{
		// Arrange — zero timeout means disabled
		var agentBuilder = new MockAgentBuilder().WithResponse("Result");
		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new OrchestrationExecutor(_scheduler, agentBuilder, reporter, _loggerFactory);
		var orchestration = new Orchestration
		{
			Name = "zero-timeout",
			Description = "Zero timeout",
			TimeoutSeconds = 0,
			Steps =
			[
				new PromptOrchestrationStep
				{
					Name = "step1",
					Type = OrchestrationStepType.Prompt,
					DependsOn = [],
					Parameters = [],
					SystemPrompt = "Test",
					UserPrompt = "Test",
					Model = "claude-opus-4.5",
				}
			]
		};

		// Act
		var result = await executor.ExecuteAsync(orchestration);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Succeeded);
	}

	[Fact]
	public async Task ExecuteAsync_ExternalCancellation_StepsMarkedAsCancelled()
	{
		// Arrange — external cancellation causes steps to be marked as Cancelled.
		// Because TryLaunchStep catches OperationCanceledException, the executor returns
		// a result rather than throwing.
		using var cts = new CancellationTokenSource();
		var agentBuilder = new MockAgentBuilder();
		agentBuilder.WithHandler((prompt, ct) =>
		{
			var channel = Channel.CreateUnbounded<AgentEvent>();
			var resultTask = Task.Run(async () =>
			{
				// Cancel externally during execution
				cts.Cancel();
				await Task.Delay(TimeSpan.FromSeconds(30), ct);
				channel.Writer.Complete();
				return new AgentResult { Content = "unreachable" };
			}, ct);
			return new AgentTask(channel.Reader, resultTask);
		});

		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new OrchestrationExecutor(_scheduler, agentBuilder, reporter, _loggerFactory);
		var orchestration = new Orchestration
		{
			Name = "external-cancel",
			Description = "External cancellation",
			TimeoutSeconds = 60,
			Steps =
			[
				new PromptOrchestrationStep
				{
					Name = "step1",
					Type = OrchestrationStepType.Prompt,
					DependsOn = [],
					Parameters = [],
					SystemPrompt = "Test",
					UserPrompt = "Test",
					Model = "claude-opus-4.5",
				}
			]
		};

		// Act
		var result = await executor.ExecuteAsync(orchestration, cancellationToken: cts.Token);

		// Assert — should be marked as Cancelled
		result.Status.Should().Be(ExecutionStatus.Cancelled);
		result.StepResults["step1"].Status.Should().Be(ExecutionStatus.Cancelled);
		result.StepResults["step1"].ErrorMessage.Should().Contain("Cancelled");
	}

	[Fact]
	public async Task ExecuteAsync_MultipleSteps_TimeoutCancelsAll()
	{
		// Arrange — parallel steps, orchestration timeout should cancel all
		var completedSteps = 0;
		var agentBuilder = new MockAgentBuilder();
		agentBuilder.WithHandler((prompt, ct) =>
		{
			var channel = Channel.CreateUnbounded<AgentEvent>();
			var resultTask = Task.Run(async () =>
			{
				// All steps take a long time
				await Task.Delay(TimeSpan.FromSeconds(30), ct);
				Interlocked.Increment(ref completedSteps);
				channel.Writer.Complete();
				return new AgentResult { Content = "Done" };
			}, ct);
			return new AgentTask(channel.Reader, resultTask);
		});

		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new OrchestrationExecutor(_scheduler, agentBuilder, reporter, _loggerFactory);
		var orchestration = new Orchestration
		{
			Name = "multi-timeout",
			Description = "Timeout cancels multiple steps",
			TimeoutSeconds = 1,
			Steps =
			[
				new PromptOrchestrationStep
				{
					Name = "A",
					Type = OrchestrationStepType.Prompt,
					DependsOn = [],
					Parameters = [],
					SystemPrompt = "Test",
					UserPrompt = "Test",
					Model = "claude-opus-4.5",
				},
				new PromptOrchestrationStep
				{
					Name = "B",
					Type = OrchestrationStepType.Prompt,
					DependsOn = [],
					Parameters = [],
					SystemPrompt = "Test",
					UserPrompt = "Test",
					Model = "claude-opus-4.5",
				}
			]
		};

		// Act
		var result = await executor.ExecuteAsync(orchestration);

		// Assert — both steps should be cancelled
		result.Status.Should().Be(ExecutionStatus.Cancelled);
		result.StepResults["A"].Status.Should().Be(ExecutionStatus.Cancelled);
		result.StepResults["A"].ErrorMessage.Should().Contain("Cancelled");
		result.StepResults["B"].Status.Should().Be(ExecutionStatus.Cancelled);
		result.StepResults["B"].ErrorMessage.Should().Contain("Cancelled");
		completedSteps.Should().Be(0); // Neither step should have completed
	}

	[Fact]
	public async Task ExecuteAsync_OrchestrationTimeout_DependentsSkipped()
	{
		// Arrange — step A takes too long and gets cancelled, step B depends on A and should be skipped
		var agentBuilder = new MockAgentBuilder();
		agentBuilder.WithHandler((prompt, ct) =>
		{
			var channel = Channel.CreateUnbounded<AgentEvent>();
			var resultTask = Task.Run(async () =>
			{
				await Task.Delay(TimeSpan.FromSeconds(30), ct);
				channel.Writer.Complete();
				return new AgentResult { Content = "unreachable" };
			}, ct);
			return new AgentTask(channel.Reader, resultTask);
		});

		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new OrchestrationExecutor(_scheduler, agentBuilder, reporter, _loggerFactory);
		var orchestration = new Orchestration
		{
			Name = "timeout-cascade",
			Description = "Timeout cascades to dependents",
			TimeoutSeconds = 1,
			Steps =
			[
				new PromptOrchestrationStep
				{
					Name = "A",
					Type = OrchestrationStepType.Prompt,
					DependsOn = [],
					Parameters = [],
					SystemPrompt = "Test",
					UserPrompt = "Test",
					Model = "claude-opus-4.5",
				},
				new PromptOrchestrationStep
				{
					Name = "B",
					Type = OrchestrationStepType.Prompt,
					DependsOn = ["A"],
					Parameters = [],
					SystemPrompt = "Test",
					UserPrompt = "Test",
					Model = "claude-opus-4.5",
				}
			]
		};

		// Act
		var result = await executor.ExecuteAsync(orchestration);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Cancelled);
		result.StepResults["A"].Status.Should().Be(ExecutionStatus.Cancelled);
		result.StepResults["A"].ErrorMessage.Should().Contain("Cancelled");
		// B should either be skipped (dependency cancelled) or also cancelled
		result.StepResults["B"].Status.Should().BeOneOf(ExecutionStatus.Cancelled, ExecutionStatus.Skipped);
	}
}
