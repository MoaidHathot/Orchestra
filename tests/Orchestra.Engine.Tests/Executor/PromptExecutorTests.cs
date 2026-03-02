using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Orchestra.Engine.Tests.TestHelpers;

namespace Orchestra.Engine.Tests.Executor;

public class PromptExecutorTests
{
	private readonly ILogger<PromptExecutor> _logger = Substitute.For<ILogger<PromptExecutor>>();

	#region Basic Execution

	[Fact]
	public async Task ExecuteAsync_SimpleStep_ReturnsSucceededResult()
	{
		// Arrange
		var agentBuilder = new MockAgentBuilder().WithResponse("Hello, world!");
		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new PromptExecutor(agentBuilder, reporter, _logger);

		var step = TestOrchestrations.CreatePromptStep("test-step");
		var context = new OrchestrationExecutionContext { Parameters = new Dictionary<string, string>() };

		// Act
		var result = await executor.ExecuteAsync(step, context);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Succeeded);
		result.Content.Should().Be("Hello, world!");
	}

	[Fact]
	public async Task ExecuteAsync_ReportsContentDelta()
	{
		// Arrange
		var agentBuilder = new MockAgentBuilder().WithResponse("Response content");
		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new PromptExecutor(agentBuilder, reporter, _logger);

		var step = TestOrchestrations.CreatePromptStep("test-step");
		var context = new OrchestrationExecutionContext { Parameters = new Dictionary<string, string>() };

		// Act
		await executor.ExecuteAsync(step, context);

		// Assert
		reporter.Received().ReportContentDelta("test-step", Arg.Any<string>());
	}

	[Fact]
	public async Task ExecuteAsync_WithError_ReturnsFailedResult()
	{
		// Arrange
		var agentBuilder = new MockAgentBuilder().WithException(new Exception("Agent error"));
		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new PromptExecutor(agentBuilder, reporter, _logger);

		var step = TestOrchestrations.CreatePromptStep("test-step");
		var context = new OrchestrationExecutionContext { Parameters = new Dictionary<string, string>() };

		// Act
		var result = await executor.ExecuteAsync(step, context);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Failed);
		result.ErrorMessage.Should().Contain("Agent error");
	}

	[Fact]
	public async Task ExecuteAsync_WithError_ReportsError()
	{
		// Arrange
		var agentBuilder = new MockAgentBuilder().WithException(new Exception("Agent error"));
		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new PromptExecutor(agentBuilder, reporter, _logger);

		var step = TestOrchestrations.CreatePromptStep("test-step");
		var context = new OrchestrationExecutionContext { Parameters = new Dictionary<string, string>() };

		// Act
		await executor.ExecuteAsync(step, context);

		// Assert
		reporter.Received().ReportStepError("test-step", Arg.Is<string>(s => s.Contains("Agent error")));
	}

	#endregion

	#region Parameter Injection

	[Fact]
	public async Task ExecuteAsync_InjectsParameters()
	{
		// Arrange
		string? capturedPrompt = null;
		var agentBuilder = new MockAgentBuilder();
		agentBuilder.WithHandler((prompt, ct) =>
		{
			capturedPrompt = prompt;
			return MockAgentBuilderExtensions.CreateWithResponse("response").WithResponse("response")
				.BuildAgentAsync(ct).Result.SendAsync(prompt, ct);
		});

		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new PromptExecutor(agentBuilder, reporter, _logger);

		var step = TestOrchestrations.CreateStepWithParameterizedPrompt(
			"param-step",
			"Hello {{name}}, your id is {{id}}",
			["name", "id"]);

		var context = new OrchestrationExecutionContext
		{
			Parameters = new Dictionary<string, string>
			{
				["name"] = "Alice",
				["id"] = "123"
			}
		};

		// Act
		await executor.ExecuteAsync(step, context);

		// Assert
		capturedPrompt.Should().Contain("Hello Alice, your id is 123");
	}

	[Fact]
	public async Task ExecuteAsync_MissingParameter_LeavesPlaceholder()
	{
		// Arrange
		string? capturedPrompt = null;
		var agentBuilder = new MockAgentBuilder();
		agentBuilder.WithHandler((prompt, ct) =>
		{
			capturedPrompt = prompt;
			return MockAgentBuilderExtensions.CreateWithResponse("response")
				.BuildAgentAsync(ct).Result.SendAsync(prompt, ct);
		});

		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new PromptExecutor(agentBuilder, reporter, _logger);

		var step = TestOrchestrations.CreateStepWithParameterizedPrompt(
			"param-step",
			"Hello {{name}}",
			["name"]);

		var context = new OrchestrationExecutionContext
		{
			Parameters = new Dictionary<string, string>() // name not provided
		};

		// Act
		await executor.ExecuteAsync(step, context);

		// Assert - Placeholder remains if parameter not provided
		capturedPrompt.Should().Contain("{{name}}");
	}

	#endregion

	#region Dependency Outputs

	[Fact]
	public async Task ExecuteAsync_IncludesDependencyOutputs()
	{
		// Arrange
		string? capturedPrompt = null;
		var agentBuilder = new MockAgentBuilder();
		agentBuilder.WithHandler((prompt, ct) =>
		{
			capturedPrompt = prompt;
			return MockAgentBuilderExtensions.CreateWithResponse("response")
				.BuildAgentAsync(ct).Result.SendAsync(prompt, ct);
		});

		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new PromptExecutor(agentBuilder, reporter, _logger);

		var step = TestOrchestrations.CreatePromptStep("consumer", dependsOn: ["producer"]);

		var context = new OrchestrationExecutionContext { Parameters = new Dictionary<string, string>() };
		context.AddResult("producer", ExecutionResult.Succeeded("Producer output content"));

		// Act
		await executor.ExecuteAsync(step, context);

		// Assert
		capturedPrompt.Should().Contain("Producer output content");
	}

	[Fact]
	public async Task ExecuteAsync_MultipleDependencies_FormatsWithHeaders()
	{
		// Arrange
		string? capturedPrompt = null;
		var agentBuilder = new MockAgentBuilder();
		agentBuilder.WithHandler((prompt, ct) =>
		{
			capturedPrompt = prompt;
			return MockAgentBuilderExtensions.CreateWithResponse("response")
				.BuildAgentAsync(ct).Result.SendAsync(prompt, ct);
		});

		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new PromptExecutor(agentBuilder, reporter, _logger);

		var step = TestOrchestrations.CreatePromptStep("consumer", dependsOn: ["dep1", "dep2"]);

		var context = new OrchestrationExecutionContext { Parameters = new Dictionary<string, string>() };
		context.AddResult("dep1", ExecutionResult.Succeeded("Output from dep1"));
		context.AddResult("dep2", ExecutionResult.Succeeded("Output from dep2"));

		// Act
		await executor.ExecuteAsync(step, context);

		// Assert
		capturedPrompt.Should().Contain("Output from dep1");
		capturedPrompt.Should().Contain("Output from dep2");
		capturedPrompt.Should().Contain("dep1");
		capturedPrompt.Should().Contain("dep2");
	}

	#endregion

	#region Loop Feedback

	[Fact]
	public async Task ExecuteAsync_WithLoopFeedback_IncludesFeedbackInPrompt()
	{
		// Arrange
		string? capturedPrompt = null;
		var agentBuilder = new MockAgentBuilder();
		agentBuilder.WithHandler((prompt, ct) =>
		{
			capturedPrompt = prompt;
			return MockAgentBuilderExtensions.CreateWithResponse("response")
				.BuildAgentAsync(ct).Result.SendAsync(prompt, ct);
		});

		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new PromptExecutor(agentBuilder, reporter, _logger);

		var step = TestOrchestrations.CreatePromptStep("looping-step");

		var context = new OrchestrationExecutionContext { Parameters = new Dictionary<string, string>() };
		context.SetLoopFeedback("looping-step", "Please improve the output by adding more details.");

		// Act
		await executor.ExecuteAsync(step, context);

		// Assert
		capturedPrompt.Should().Contain("Please improve the output by adding more details.");
		capturedPrompt.Should().Contain("Feedback from previous attempt");
	}

	[Fact]
	public async Task ExecuteAsync_ConsumesLoopFeedback()
	{
		// Arrange
		var agentBuilder = new MockAgentBuilder().WithResponse("response");
		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new PromptExecutor(agentBuilder, reporter, _logger);

		var step = TestOrchestrations.CreatePromptStep("looping-step");

		var context = new OrchestrationExecutionContext { Parameters = new Dictionary<string, string>() };
		context.SetLoopFeedback("looping-step", "Feedback");

		// Act
		await executor.ExecuteAsync(step, context);

		// Assert - Feedback should be consumed
		context.ConsumeLoopFeedback("looping-step").Should().BeNull();
	}

	#endregion

	#region Execution Trace

	[Fact]
	public async Task ExecuteAsync_BuildsExecutionTrace()
	{
		// Arrange
		var agentBuilder = new MockAgentBuilder().WithResponse("Response content");
		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new PromptExecutor(agentBuilder, reporter, _logger);

		var step = TestOrchestrations.CreatePromptStep("traced-step");
		var context = new OrchestrationExecutionContext { Parameters = new Dictionary<string, string>() };

		// Act
		var result = await executor.ExecuteAsync(step, context);

		// Assert
		result.Trace.Should().NotBeNull();
		result.Trace!.SystemPrompt.Should().Be(step.SystemPrompt);
	}

	[Fact]
	public async Task ExecuteAsync_ReportsStepTrace()
	{
		// Arrange
		var agentBuilder = new MockAgentBuilder().WithResponse("Response content");
		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new PromptExecutor(agentBuilder, reporter, _logger);

		var step = TestOrchestrations.CreatePromptStep("traced-step");
		var context = new OrchestrationExecutionContext { Parameters = new Dictionary<string, string>() };

		// Act
		await executor.ExecuteAsync(step, context);

		// Assert
		reporter.Received().ReportStepTrace("traced-step", Arg.Any<StepExecutionTrace>());
	}

	[Fact]
	public async Task ExecuteAsync_CapturesRawDependencyOutputs()
	{
		// Arrange
		var agentBuilder = new MockAgentBuilder().WithResponse("response");
		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new PromptExecutor(agentBuilder, reporter, _logger);

		var step = TestOrchestrations.CreatePromptStep("consumer", dependsOn: ["producer"]);

		var context = new OrchestrationExecutionContext { Parameters = new Dictionary<string, string>() };
		context.AddResult("producer", ExecutionResult.Succeeded("processed", rawContent: "raw content"));

		// Act
		var result = await executor.ExecuteAsync(step, context);

		// Assert
		result.RawDependencyOutputs.Should().ContainKey("producer");
		result.RawDependencyOutputs["producer"].Should().Be("raw content");
	}

	#endregion

	#region Tool Execution Events

	[Fact]
	public async Task ExecuteAsync_WithToolCalls_ReportsToolExecution()
	{
		// Arrange
		var events = new[]
		{
			new AgentEvent
			{
				Type = AgentEventType.ToolExecutionStart,
				ToolCallId = "call1",
				ToolName = "read_file",
				ToolArguments = "{\"path\": \"/test.txt\"}"
			},
			new AgentEvent
			{
				Type = AgentEventType.ToolExecutionComplete,
				ToolCallId = "call1",
				ToolName = "read_file",
				ToolSuccess = true,
				ToolResult = "file content"
			},
			new AgentEvent
			{
				Type = AgentEventType.MessageDelta,
				Content = "Final response"
			}
		};

		var agentBuilder = new MockAgentBuilder().WithResponse("Final response", events);
		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new PromptExecutor(agentBuilder, reporter, _logger);

		var step = TestOrchestrations.CreatePromptStep("tool-step");
		var context = new OrchestrationExecutionContext { Parameters = new Dictionary<string, string>() };

		// Act
		await executor.ExecuteAsync(step, context);

		// Assert
		reporter.Received().ReportToolExecutionStarted("tool-step", "read_file", Arg.Any<string?>(), Arg.Any<string?>());
		reporter.Received().ReportToolExecutionCompleted("tool-step", "read_file", true, Arg.Any<string?>(), Arg.Any<string?>());
	}

	[Fact]
	public async Task ExecuteAsync_WithToolCalls_IncludesInTrace()
	{
		// Arrange
		var events = new[]
		{
			new AgentEvent
			{
				Type = AgentEventType.ToolExecutionStart,
				ToolCallId = "call1",
				ToolName = "search"
			},
			new AgentEvent
			{
				Type = AgentEventType.ToolExecutionComplete,
				ToolCallId = "call1",
				ToolName = "search",
				ToolSuccess = true,
				ToolResult = "results"
			},
			new AgentEvent
			{
				Type = AgentEventType.MessageDelta,
				Content = "Done"
			}
		};

		var agentBuilder = new MockAgentBuilder().WithResponse("Done", events);
		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new PromptExecutor(agentBuilder, reporter, _logger);

		var step = TestOrchestrations.CreatePromptStep("tool-step");
		var context = new OrchestrationExecutionContext { Parameters = new Dictionary<string, string>() };

		// Act
		var result = await executor.ExecuteAsync(step, context);

		// Assert
		result.Trace!.ToolCalls.Should().HaveCount(1);
		result.Trace.ToolCalls[0].ToolName.Should().Be("search");
		result.Trace.ToolCalls[0].Success.Should().BeTrue();
	}

	#endregion

	#region Reasoning Events

	[Fact]
	public async Task ExecuteAsync_WithReasoning_ReportsReasoningDelta()
	{
		// Arrange
		var events = new[]
		{
			new AgentEvent
			{
				Type = AgentEventType.ReasoningDelta,
				Content = "Let me think..."
			},
			new AgentEvent
			{
				Type = AgentEventType.MessageDelta,
				Content = "Response"
			}
		};

		var agentBuilder = new MockAgentBuilder().WithResponse("Response", events);
		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new PromptExecutor(agentBuilder, reporter, _logger);

		var step = TestOrchestrations.CreatePromptStep("reasoning-step");
		var context = new OrchestrationExecutionContext { Parameters = new Dictionary<string, string>() };

		// Act
		await executor.ExecuteAsync(step, context);

		// Assert
		reporter.Received().ReportReasoningDelta("reasoning-step", "Let me think...");
	}

	[Fact]
	public async Task ExecuteAsync_WithReasoning_IncludesInTrace()
	{
		// Arrange
		var events = new[]
		{
			new AgentEvent
			{
				Type = AgentEventType.ReasoningDelta,
				Content = "First thought. "
			},
			new AgentEvent
			{
				Type = AgentEventType.ReasoningDelta,
				Content = "Second thought."
			},
			new AgentEvent
			{
				Type = AgentEventType.MessageDelta,
				Content = "Response"
			}
		};

		var agentBuilder = new MockAgentBuilder().WithResponse("Response", events);
		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new PromptExecutor(agentBuilder, reporter, _logger);

		var step = TestOrchestrations.CreatePromptStep("reasoning-step");
		var context = new OrchestrationExecutionContext { Parameters = new Dictionary<string, string>() };

		// Act
		var result = await executor.ExecuteAsync(step, context);

		// Assert
		result.Trace!.Reasoning.Should().Be("First thought. Second thought.");
	}

	#endregion

	#region Usage Reporting

	[Fact]
	public async Task ExecuteAsync_WithUsage_ReportsUsage()
	{
		// Arrange
		var usage = new AgentUsage { InputTokens = 100, OutputTokens = 50 };
		var agentBuilder = new MockAgentBuilder().WithResponse("Response", usage: usage, actualModel: "claude-opus-4.5");
		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new PromptExecutor(agentBuilder, reporter, _logger);

		var step = TestOrchestrations.CreatePromptStep("usage-step");
		var context = new OrchestrationExecutionContext { Parameters = new Dictionary<string, string>() };

		// Act
		await executor.ExecuteAsync(step, context);

		// Assert
		reporter.Received().ReportUsage("usage-step", "claude-opus-4.5", Arg.Any<AgentUsage>());
	}

	[Fact]
	public async Task ExecuteAsync_CapturesTokenUsage()
	{
		// Arrange
		var usage = new AgentUsage { InputTokens = 100, OutputTokens = 50 };
		var agentBuilder = new MockAgentBuilder().WithResponse("Response", usage: usage, actualModel: "model");
		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new PromptExecutor(agentBuilder, reporter, _logger);

		var step = TestOrchestrations.CreatePromptStep("usage-step");
		var context = new OrchestrationExecutionContext { Parameters = new Dictionary<string, string>() };

		// Act
		var result = await executor.ExecuteAsync(step, context);

		// Assert
		result.Usage.Should().NotBeNull();
		result.Usage!.InputTokens.Should().Be(100);
		result.Usage.OutputTokens.Should().Be(50);
	}

	#endregion

	#region Model Information

	[Fact]
	public async Task ExecuteAsync_CapturesActualModel()
	{
		// Arrange
		var agentBuilder = new MockAgentBuilder().WithResponse("Response", actualModel: "gpt-4-turbo");
		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new PromptExecutor(agentBuilder, reporter, _logger);

		var step = TestOrchestrations.CreatePromptStep("model-step", model: "gpt-4");
		var context = new OrchestrationExecutionContext { Parameters = new Dictionary<string, string>() };

		// Act
		var result = await executor.ExecuteAsync(step, context);

		// Assert
		result.ActualModel.Should().Be("gpt-4-turbo");
	}

	#endregion
}
