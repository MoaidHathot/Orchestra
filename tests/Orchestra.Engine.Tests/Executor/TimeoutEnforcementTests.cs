using System.Threading.Channels;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orchestra.Engine.Tests.TestHelpers;

namespace Orchestra.Engine.Tests.Executor;

public class TimeoutEnforcementTests
{
	private readonly IScheduler _scheduler = new OrchestrationScheduler();
	private readonly IOrchestrationReporter _reporter = Substitute.For<IOrchestrationReporter>();
	private readonly ILoggerFactory _loggerFactory = NullLoggerFactory.Instance;

	[Fact]
	public async Task ExecuteAsync_StepWithTimeout_CompletesBeforeTimeout_Succeeds()
	{
		// Arrange
		var agentBuilder = new MockAgentBuilder().WithResponse("Quick response");
		var executor = new OrchestrationExecutor(_scheduler, agentBuilder, _reporter, _loggerFactory);
		var orchestration = new Orchestration
		{
			Name = "timeout-success",
			Description = "Step completes before timeout",
			Steps =
			[
				new PromptOrchestrationStep
				{
					Name = "fast-step",
					Type = OrchestrationStepType.Prompt,
					DependsOn = [],
					Parameters = [],
					SystemPrompt = "You are a test assistant.",
					UserPrompt = "Quick task",
					Model = "claude-opus-4.5",
					TimeoutSeconds = 30
				}
			]
		};

		// Act
		var result = await executor.ExecuteAsync(orchestration);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Succeeded);
		result.StepResults["fast-step"].Status.Should().Be(ExecutionStatus.Succeeded);
		result.StepResults["fast-step"].Content.Should().Be("Quick response");
	}

	[Fact]
	public async Task ExecuteAsync_StepWithTimeout_ExceedsTimeout_FailsWithTimeoutMessage()
	{
		// Arrange - agent that delays longer than the timeout
		var agentBuilder = new MockAgentBuilder();
		agentBuilder.WithHandler((prompt, ct) =>
		{
			var channel = Channel.CreateUnbounded<AgentEvent>();
			var resultTask = Task.Run(async () =>
			{
				// Simulate a slow operation that respects cancellation
				await Task.Delay(TimeSpan.FromSeconds(10), ct);
				channel.Writer.Complete();
				return new AgentResult { Content = "Should not reach here" };
			}, ct);

			return new AgentTask(channel.Reader, resultTask);
		});

		var executor = new OrchestrationExecutor(_scheduler, agentBuilder, _reporter, _loggerFactory);
		var orchestration = new Orchestration
		{
			Name = "timeout-exceeded",
			Description = "Step exceeds timeout",
			Steps =
			[
				new PromptOrchestrationStep
				{
					Name = "slow-step",
					Type = OrchestrationStepType.Prompt,
					DependsOn = [],
					Parameters = [],
					SystemPrompt = "You are a test assistant.",
					UserPrompt = "Very slow task",
					Model = "claude-opus-4.5",
					TimeoutSeconds = 1
				}
			]
		};

		// Act
		var result = await executor.ExecuteAsync(orchestration);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Failed);
		result.StepResults["slow-step"].Status.Should().Be(ExecutionStatus.Failed);
		result.StepResults["slow-step"].ErrorMessage.Should().Contain("timed out");
		result.StepResults["slow-step"].ErrorMessage.Should().Contain("1 seconds");
	}

	[Fact]
	public async Task ExecuteAsync_StepWithoutTimeout_NoTimeoutApplied()
	{
		// Arrange - agent with slight delay, but no timeout should be fine
		var agentBuilder = new MockAgentBuilder();
		agentBuilder.WithHandler((prompt, ct) =>
		{
			var channel = Channel.CreateUnbounded<AgentEvent>();
			var resultTask = Task.Run(async () =>
			{
				await Task.Delay(100, ct);
				await channel.Writer.WriteAsync(new AgentEvent
				{
					Type = AgentEventType.MessageDelta,
					Content = "Completed after delay"
				}, ct);
				channel.Writer.Complete();
				return new AgentResult { Content = "Completed after delay" };
			}, ct);

			return new AgentTask(channel.Reader, resultTask);
		});

		var executor = new OrchestrationExecutor(_scheduler, agentBuilder, _reporter, _loggerFactory);
		var orchestration = new Orchestration
		{
			Name = "no-timeout",
			Description = "Step without timeout",
			Steps =
			[
				new PromptOrchestrationStep
				{
					Name = "normal-step",
					Type = OrchestrationStepType.Prompt,
					DependsOn = [],
					Parameters = [],
					SystemPrompt = "You are a test assistant.",
					UserPrompt = "Normal task",
					Model = "claude-opus-4.5"
				}
			]
		};

		// Act
		var result = await executor.ExecuteAsync(orchestration);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Succeeded);
		result.StepResults["normal-step"].Status.Should().Be(ExecutionStatus.Succeeded);
	}

	[Fact]
	public async Task ExecuteAsync_StepWithZeroTimeout_NoTimeoutApplied()
	{
		// Arrange - zero timeout means no timeout (only > 0 triggers timeout)
		var agentBuilder = new MockAgentBuilder().WithResponse("No timeout applied");
		var executor = new OrchestrationExecutor(_scheduler, agentBuilder, _reporter, _loggerFactory);
		var orchestration = new Orchestration
		{
			Name = "zero-timeout",
			Description = "Step with zero timeout",
			Steps =
			[
				new PromptOrchestrationStep
				{
					Name = "step1",
					Type = OrchestrationStepType.Prompt,
					DependsOn = [],
					Parameters = [],
					SystemPrompt = "You are a test assistant.",
					UserPrompt = "Task",
					Model = "claude-opus-4.5",
					TimeoutSeconds = 0
				}
			]
		};

		// Act
		var result = await executor.ExecuteAsync(orchestration);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Succeeded);
		result.StepResults["step1"].Status.Should().Be(ExecutionStatus.Succeeded);
	}

	[Fact]
	public async Task ExecuteAsync_TimedOutStep_ReportsErrorToReporter()
	{
		// Arrange
		var agentBuilder = new MockAgentBuilder();
		agentBuilder.WithHandler((prompt, ct) =>
		{
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
		var orchestration = new Orchestration
		{
			Name = "timeout-reporter",
			Description = "Test reporter is called on timeout",
			Steps =
			[
				new PromptOrchestrationStep
				{
					Name = "timed-out-step",
					Type = OrchestrationStepType.Prompt,
					DependsOn = [],
					Parameters = [],
					SystemPrompt = "Test",
					UserPrompt = "Test",
					Model = "claude-opus-4.5",
					TimeoutSeconds = 1
				}
			]
		};

		// Act
		await executor.ExecuteAsync(orchestration);

		// Assert - reporter should receive step error about timeout
		reporter.Received().ReportStepError("timed-out-step", Arg.Is<string>(msg => msg.Contains("timed out")));
	}

	[Fact]
	public async Task ExecuteAsync_TimedOutStep_DependentsSkipped()
	{
		// Arrange - step A times out, step B depends on A and should be skipped
		var agentBuilder = new MockAgentBuilder();
		agentBuilder.WithHandler((prompt, ct) =>
		{
			var channel = Channel.CreateUnbounded<AgentEvent>();
			var resultTask = Task.Run(async () =>
			{
				await Task.Delay(TimeSpan.FromSeconds(10), ct);
				channel.Writer.Complete();
				return new AgentResult { Content = "unreachable" };
			}, ct);

			return new AgentTask(channel.Reader, resultTask);
		});

		var executor = new OrchestrationExecutor(_scheduler, agentBuilder, _reporter, _loggerFactory);
		var orchestration = new Orchestration
		{
			Name = "timeout-cascade",
			Description = "Timeout cascades to dependents",
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
					TimeoutSeconds = 1
				},
				new PromptOrchestrationStep
				{
					Name = "B",
					Type = OrchestrationStepType.Prompt,
					DependsOn = ["A"],
					Parameters = [],
					SystemPrompt = "Test",
					UserPrompt = "Test",
					Model = "claude-opus-4.5"
				}
			]
		};

		// Act
		var result = await executor.ExecuteAsync(orchestration);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Failed);
		result.StepResults["A"].Status.Should().Be(ExecutionStatus.Failed);
		result.StepResults["A"].ErrorMessage.Should().Contain("timed out");
		result.StepResults["B"].Status.Should().Be(ExecutionStatus.Skipped);
	}

	#region DefaultStepTimeoutSeconds

	[Fact]
	public async Task ExecuteAsync_StepWithoutTimeout_UsesDefaultStepTimeout()
	{
		// Arrange - step has no TimeoutSeconds, orchestration has DefaultStepTimeoutSeconds = 1
		var agentBuilder = new MockAgentBuilder();
		agentBuilder.WithHandler((prompt, ct) =>
		{
			var channel = Channel.CreateUnbounded<AgentEvent>();
			var resultTask = Task.Run(async () =>
			{
				await Task.Delay(TimeSpan.FromSeconds(10), ct);
				channel.Writer.Complete();
				return new AgentResult { Content = "unreachable" };
			}, ct);

			return new AgentTask(channel.Reader, resultTask);
		});

		var executor = new OrchestrationExecutor(_scheduler, agentBuilder, _reporter, _loggerFactory);
		var orchestration = new Orchestration
		{
			Name = "default-step-timeout",
			Description = "Step inherits default timeout",
			DefaultStepTimeoutSeconds = 1,
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
					Model = "claude-opus-4.5"
					// No TimeoutSeconds — should fall back to DefaultStepTimeoutSeconds = 1
				}
			]
		};

		// Act
		var result = await executor.ExecuteAsync(orchestration);

		// Assert — step should time out via the default
		result.Status.Should().Be(ExecutionStatus.Failed);
		result.StepResults["slow-step"].Status.Should().Be(ExecutionStatus.Failed);
		result.StepResults["slow-step"].ErrorMessage.Should().Contain("timed out");
		result.StepResults["slow-step"].ErrorMessage.Should().Contain("1 seconds");
	}

	[Fact]
	public async Task ExecuteAsync_StepWithExplicitTimeout_OverridesDefaultStepTimeout()
	{
		// Arrange - step has TimeoutSeconds = 1, orchestration has DefaultStepTimeoutSeconds = 60
		// The step-level value should win
		var agentBuilder = new MockAgentBuilder();
		agentBuilder.WithHandler((prompt, ct) =>
		{
			var channel = Channel.CreateUnbounded<AgentEvent>();
			var resultTask = Task.Run(async () =>
			{
				await Task.Delay(TimeSpan.FromSeconds(10), ct);
				channel.Writer.Complete();
				return new AgentResult { Content = "unreachable" };
			}, ct);

			return new AgentTask(channel.Reader, resultTask);
		});

		var executor = new OrchestrationExecutor(_scheduler, agentBuilder, _reporter, _loggerFactory);
		var orchestration = new Orchestration
		{
			Name = "step-override-default",
			Description = "Step timeout overrides default",
			DefaultStepTimeoutSeconds = 60,
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
					TimeoutSeconds = 1 // Should use 1, not 60
				}
			]
		};

		// Act
		var result = await executor.ExecuteAsync(orchestration);

		// Assert — step should time out at 1 second (step-level wins)
		result.Status.Should().Be(ExecutionStatus.Failed);
		result.StepResults["step1"].Status.Should().Be(ExecutionStatus.Failed);
		result.StepResults["step1"].ErrorMessage.Should().Contain("timed out");
		result.StepResults["step1"].ErrorMessage.Should().Contain("1 seconds");
	}

	[Fact]
	public async Task ExecuteAsync_StepWithZeroTimeout_IgnoresDefaultStepTimeout()
	{
		// Arrange - step has TimeoutSeconds = 0 (explicit disable), orchestration has DefaultStepTimeoutSeconds = 1
		// The step should NOT time out even though default is 1 second
		var agentBuilder = new MockAgentBuilder();
		agentBuilder.WithHandler((prompt, ct) =>
		{
			var channel = Channel.CreateUnbounded<AgentEvent>();
			var resultTask = Task.Run(async () =>
			{
				await Task.Delay(100, ct); // Slight delay that would exceed timeout if applied
				await channel.Writer.WriteAsync(new AgentEvent
				{
					Type = AgentEventType.MessageDelta,
					Content = "Completed despite default timeout"
				}, ct);
				channel.Writer.Complete();
				return new AgentResult { Content = "Completed despite default timeout" };
			}, ct);

			return new AgentTask(channel.Reader, resultTask);
		});

		var executor = new OrchestrationExecutor(_scheduler, agentBuilder, _reporter, _loggerFactory);
		var orchestration = new Orchestration
		{
			Name = "zero-overrides-default",
			Description = "Zero timeout disables default",
			DefaultStepTimeoutSeconds = 1,
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
					TimeoutSeconds = 0 // Explicit disable — should ignore DefaultStepTimeoutSeconds
				}
			]
		};

		// Act
		var result = await executor.ExecuteAsync(orchestration);

		// Assert — step should succeed (no timeout applied)
		result.Status.Should().Be(ExecutionStatus.Succeeded);
		result.StepResults["step1"].Status.Should().Be(ExecutionStatus.Succeeded);
	}

	#endregion
}
