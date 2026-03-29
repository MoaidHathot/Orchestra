using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Orchestra.Engine.Tests.TestHelpers;

namespace Orchestra.Engine.Tests.Executor;

public class PromptExecutorEngineToolTests
{
	private readonly ILogger<PromptExecutor> _logger = Substitute.For<ILogger<PromptExecutor>>();
	private readonly IPromptFormatter _formatter = DefaultPromptFormatter.Instance;

	[Fact]
	public async Task ExecuteAsync_LlmCallsSetStatusFailed_ReturnsFailedResult()
	{
		// Arrange
		var agentBuilder = new MockAgentBuilder()
			.WithEngineToolCall(
				"orchestra_set_status",
				"""{"status": "failed", "reason": "MCP tools are unavailable"}""",
				"I cannot complete this task because MCP tools are unavailable.");

		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new PromptExecutor(agentBuilder, reporter, _formatter, _logger);

		var step = TestOrchestrations.CreatePromptStep("test-step");
		var context = new OrchestrationExecutionContext { Parameters = new Dictionary<string, string>() };

		// Act
		var result = await executor.ExecuteAsync(step, context);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Failed);
		result.ErrorMessage.Should().Be("MCP tools are unavailable");
	}

	[Fact]
	public async Task ExecuteAsync_LlmCallsSetStatusFailed_ReportsError()
	{
		// Arrange
		var agentBuilder = new MockAgentBuilder()
			.WithEngineToolCall(
				"orchestra_set_status",
				"""{"status": "failed", "reason": "Cannot proceed"}""",
				"Unable to complete.");

		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new PromptExecutor(agentBuilder, reporter, _formatter, _logger);

		var step = TestOrchestrations.CreatePromptStep("test-step");
		var context = new OrchestrationExecutionContext { Parameters = new Dictionary<string, string>() };

		// Act
		await executor.ExecuteAsync(step, context);

		// Assert
		reporter.Received().ReportStepError("test-step", "Cannot proceed");
	}

	[Fact]
	public async Task ExecuteAsync_LlmDoesNotCallSetStatus_ReturnsSucceededResult()
	{
		// Arrange - Normal response without engine tool calls
		var agentBuilder = new MockAgentBuilder().WithResponse("Task completed successfully.");
		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new PromptExecutor(agentBuilder, reporter, _formatter, _logger);

		var step = TestOrchestrations.CreatePromptStep("test-step");
		var context = new OrchestrationExecutionContext { Parameters = new Dictionary<string, string>() };

		// Act
		var result = await executor.ExecuteAsync(step, context);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Succeeded);
		result.Content.Should().Be("Task completed successfully.");
	}

	[Fact]
	public async Task ExecuteAsync_LlmCallsSetStatusFailed_PreservesTrace()
	{
		// Arrange
		var agentBuilder = new MockAgentBuilder()
			.WithEngineToolCall(
				"orchestra_set_status",
				"""{"status": "failed", "reason": "No tools available"}""",
				"Failed.");

		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new PromptExecutor(agentBuilder, reporter, _formatter, _logger);

		var step = TestOrchestrations.CreatePromptStep("test-step");
		var context = new OrchestrationExecutionContext { Parameters = new Dictionary<string, string>() };

		// Act
		var result = await executor.ExecuteAsync(step, context);

		// Assert - Trace should still be present even on engine-tool-driven failure
		result.Trace.Should().NotBeNull();
	}

	[Fact]
	public async Task ExecuteAsync_LlmCallsSetStatusFailed_PreservesRawDependencyOutputs()
	{
		// Arrange
		var agentBuilder = new MockAgentBuilder()
			.WithEngineToolCall(
				"orchestra_set_status",
				"""{"status": "failed", "reason": "Cannot process"}""",
				"Failed.");

		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new PromptExecutor(agentBuilder, reporter, _formatter, _logger);

		var step = TestOrchestrations.CreatePromptStep("consumer", dependsOn: ["producer"]);
		var context = new OrchestrationExecutionContext { Parameters = new Dictionary<string, string>() };
		context.AddResult("producer", ExecutionResult.Succeeded("Producer output"));

		// Act
		var result = await executor.ExecuteAsync(step, context);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Failed);
		result.RawDependencyOutputs.Should().ContainKey("producer");
	}

	[Fact]
	public async Task ExecuteAsync_WithCustomEngineToolRegistry_UsesProvidedRegistry()
	{
		// Arrange - Use empty registry (no engine tools)
		var emptyRegistry = new EngineToolRegistry();
		var agentBuilder = new MockAgentBuilder().WithResponse("Response");
		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new PromptExecutor(agentBuilder, reporter, _formatter, _logger, emptyRegistry);

		var step = TestOrchestrations.CreatePromptStep("test-step");
		var context = new OrchestrationExecutionContext { Parameters = new Dictionary<string, string>() };

		// Act
		var result = await executor.ExecuteAsync(step, context);

		// Assert - Should succeed normally (no engine tools to override)
		result.Status.Should().Be(ExecutionStatus.Succeeded);
	}

	[Fact]
	public async Task ExecuteAsync_EngineToolsPassedToBuilder()
	{
		// Arrange
		var agentBuilder = new MockAgentBuilder().WithResponse("Response");
		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new PromptExecutor(agentBuilder, reporter, _formatter, _logger);

		var step = TestOrchestrations.CreatePromptStep("test-step");
		var context = new OrchestrationExecutionContext { Parameters = new Dictionary<string, string>() };

		// Act
		await executor.ExecuteAsync(step, context);

		// Assert - Engine tools should have been passed to the builder
		agentBuilder.CapturedEngineTools.Should().NotBeEmpty();
		agentBuilder.CapturedEngineToolContext.Should().NotBeNull();
	}

	[Fact]
	public async Task ExecuteAsync_LlmCallsSetStatusSuccess_ReturnsSucceededResult()
	{
		// Arrange
		var agentBuilder = new MockAgentBuilder()
			.WithEngineToolCall(
				"orchestra_set_status",
				"""{"status": "success", "reason": "All tasks completed successfully"}""",
				"I have completed the task.");

		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new PromptExecutor(agentBuilder, reporter, _formatter, _logger);

		var step = TestOrchestrations.CreatePromptStep("test-step");
		var context = new OrchestrationExecutionContext { Parameters = new Dictionary<string, string>() };

		// Act
		var result = await executor.ExecuteAsync(step, context);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Succeeded);
		result.Content.Should().Be("I have completed the task.");
	}

	[Fact]
	public async Task ExecuteAsync_LlmCallsSetStatusSuccess_DoesNotReportError()
	{
		// Arrange
		var agentBuilder = new MockAgentBuilder()
			.WithEngineToolCall(
				"orchestra_set_status",
				"""{"status": "success", "reason": "Done"}""",
				"Completed.");

		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new PromptExecutor(agentBuilder, reporter, _formatter, _logger);

		var step = TestOrchestrations.CreatePromptStep("test-step");
		var context = new OrchestrationExecutionContext { Parameters = new Dictionary<string, string>() };

		// Act
		await executor.ExecuteAsync(step, context);

		// Assert - Success should not report any errors
		reporter.DidNotReceive().ReportStepError(Arg.Any<string>(), Arg.Any<string>());
	}

	[Fact]
	public async Task ExecuteAsync_EachExecutionGetsFreshContext()
	{
		// Arrange - First call will set status to failed, second should start fresh
		var callCount = 0;
		var agentBuilder = new MockAgentBuilder();
		agentBuilder.WithHandler((prompt, ct) =>
		{
			callCount++;
			if (callCount == 1)
			{
				// First call: simulate LLM calling set_status tool
				// We need to manually invoke the engine tool on the context
				if (agentBuilder.CapturedEngineToolContext is not null)
				{
					foreach (var tool in agentBuilder.CapturedEngineTools)
					{
						if (tool.Name == "orchestra_set_status")
						{
							tool.Execute("""{"status": "failed", "reason": "First call fails"}""",
								agentBuilder.CapturedEngineToolContext);
						}
					}
				}
			}
			// Second call: no tool invocation, should succeed
			return MockAgentBuilderExtensions.CreateWithResponse("Response")
				.BuildAgentAsync(ct).Result.SendAsync(prompt, ct);
		});

		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new PromptExecutor(agentBuilder, reporter, _formatter, _logger);

		var step = TestOrchestrations.CreatePromptStep("test-step");
		var context = new OrchestrationExecutionContext { Parameters = new Dictionary<string, string>() };

		// Act
		var result1 = await executor.ExecuteAsync(step, context);
		var result2 = await executor.ExecuteAsync(step, context);

		// Assert - First call should fail, second should succeed (fresh context)
		result1.Status.Should().Be(ExecutionStatus.Failed);
		result2.Status.Should().Be(ExecutionStatus.Succeeded);
	}

	[Fact]
	public async Task ExecuteAsync_LlmCallsSetStatusNoAction_ReturnsNoActionResult()
	{
		// Arrange
		var agentBuilder = new MockAgentBuilder()
			.WithEngineToolCall(
				"orchestra_set_status",
				"""{"status": "no_action", "reason": "No incidents to process"}""",
				"There are no unacknowledged incidents.");

		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new PromptExecutor(agentBuilder, reporter, _formatter, _logger);

		var step = TestOrchestrations.CreatePromptStep("test-step");
		var context = new OrchestrationExecutionContext { Parameters = new Dictionary<string, string>() };

		// Act
		var result = await executor.ExecuteAsync(step, context);

		// Assert
		result.Status.Should().Be(ExecutionStatus.NoAction);
		result.Content.Should().Be("No incidents to process");
	}

	[Fact]
	public async Task ExecuteAsync_LlmCallsSetStatusNoAction_DoesNotReportError()
	{
		// Arrange
		var agentBuilder = new MockAgentBuilder()
			.WithEngineToolCall(
				"orchestra_set_status",
				"""{"status": "no_action", "reason": "Nothing to do"}""",
				"No action needed.");

		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new PromptExecutor(agentBuilder, reporter, _formatter, _logger);

		var step = TestOrchestrations.CreatePromptStep("test-step");
		var context = new OrchestrationExecutionContext { Parameters = new Dictionary<string, string>() };

		// Act
		await executor.ExecuteAsync(step, context);

		// Assert - NoAction should not report errors
		reporter.DidNotReceive().ReportStepError(Arg.Any<string>(), Arg.Any<string>());
	}

	[Fact]
	public async Task ExecuteAsync_LlmCallsOrchestraComplete_ReturnsResultWithOrchestrationCompleteFlag()
	{
		// Arrange
		var agentBuilder = new MockAgentBuilder()
			.WithEngineToolCall(
				"orchestra_complete",
				"""{"status": "success", "reason": "No incidents to process"}""",
				"Orchestration halted — nothing to do.");

		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new PromptExecutor(agentBuilder, reporter, _formatter, _logger);

		var step = TestOrchestrations.CreatePromptStep("test-step");
		var context = new OrchestrationExecutionContext { Parameters = new Dictionary<string, string>() };

		// Act
		var result = await executor.ExecuteAsync(step, context);

		// Assert
		result.OrchestrationCompleteRequested.Should().BeTrue();
		result.OrchestrationCompleteStatus.Should().Be(ExecutionStatus.Succeeded);
		result.OrchestrationCompleteReason.Should().Be("No incidents to process");
	}

	[Fact]
	public async Task ExecuteAsync_LlmCallsOrchestraCompleteFailed_ReturnsFailedResultWithOrchestrationCompleteFlag()
	{
		// Arrange
		var agentBuilder = new MockAgentBuilder()
			.WithEngineToolCall(
				"orchestra_complete",
				"""{"status": "failed", "reason": "Critical issue detected"}""",
				"Halting orchestration due to critical issue.");

		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new PromptExecutor(agentBuilder, reporter, _formatter, _logger);

		var step = TestOrchestrations.CreatePromptStep("test-step");
		var context = new OrchestrationExecutionContext { Parameters = new Dictionary<string, string>() };

		// Act
		var result = await executor.ExecuteAsync(step, context);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Failed);
		result.OrchestrationCompleteRequested.Should().BeTrue();
		result.OrchestrationCompleteStatus.Should().Be(ExecutionStatus.Failed);
		result.OrchestrationCompleteReason.Should().Be("Critical issue detected");
	}

	[Fact]
	public async Task ExecuteAsync_LlmCallsOrchestraCompleteSuccess_SetsStepStatusToSucceeded()
	{
		// Arrange
		var agentBuilder = new MockAgentBuilder()
			.WithEngineToolCall(
				"orchestra_complete",
				"""{"status": "success", "reason": "All done early"}""",
				"Done.");

		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new PromptExecutor(agentBuilder, reporter, _formatter, _logger);

		var step = TestOrchestrations.CreatePromptStep("test-step");
		var context = new OrchestrationExecutionContext { Parameters = new Dictionary<string, string>() };

		// Act
		var result = await executor.ExecuteAsync(step, context);

		// Assert - The step itself should succeed
		result.Status.Should().Be(ExecutionStatus.Succeeded);
	}
}
