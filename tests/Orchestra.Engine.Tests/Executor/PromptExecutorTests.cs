using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Orchestra.Engine.Tests.TestHelpers;

namespace Orchestra.Engine.Tests.Executor;

public class PromptExecutorTests
{
	private static readonly OrchestrationInfo s_defaultInfo = new("test-orchestration", "1.0.0", "run123", DateTimeOffset.UtcNow);
	private readonly ILogger<PromptExecutor> _logger = Substitute.For<ILogger<PromptExecutor>>();
	private readonly IPromptFormatter _formatter = DefaultPromptFormatter.Instance;

	#region Basic Execution

	[Fact]
	public async Task ExecuteAsync_SimpleStep_ReturnsSucceededResult()
	{
		// Arrange
		var agentBuilder = new MockAgentBuilder().WithResponse("Hello, world!");
		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new PromptExecutor(agentBuilder, reporter, _formatter, _logger);

		var step = TestOrchestrations.CreatePromptStep("test-step");
		var context = new OrchestrationExecutionContext { Parameters = new Dictionary<string, string>(), OrchestrationInfo = s_defaultInfo };

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
		var executor = new PromptExecutor(agentBuilder, reporter, _formatter, _logger);

		var step = TestOrchestrations.CreatePromptStep("test-step");
		var context = new OrchestrationExecutionContext { Parameters = new Dictionary<string, string>(), OrchestrationInfo = s_defaultInfo };

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
		var executor = new PromptExecutor(agentBuilder, reporter, _formatter, _logger);

		var step = TestOrchestrations.CreatePromptStep("test-step");
		var context = new OrchestrationExecutionContext { Parameters = new Dictionary<string, string>(), OrchestrationInfo = s_defaultInfo };

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
		var executor = new PromptExecutor(agentBuilder, reporter, _formatter, _logger);

		var step = TestOrchestrations.CreatePromptStep("test-step");
		var context = new OrchestrationExecutionContext { Parameters = new Dictionary<string, string>(), OrchestrationInfo = s_defaultInfo };

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
		var executor = new PromptExecutor(agentBuilder, reporter, _formatter, _logger);

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
			},
			OrchestrationInfo = s_defaultInfo
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
		var executor = new PromptExecutor(agentBuilder, reporter, _formatter, _logger);

		var step = TestOrchestrations.CreateStepWithParameterizedPrompt(
			"param-step",
			"Hello {{name}}",
			["name"]);

		var context = new OrchestrationExecutionContext
		{
			Parameters = new Dictionary<string, string>(), // name not provided
			OrchestrationInfo = s_defaultInfo
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
		var executor = new PromptExecutor(agentBuilder, reporter, _formatter, _logger);

		var step = TestOrchestrations.CreatePromptStep("consumer", dependsOn: ["producer"]);

		var context = new OrchestrationExecutionContext { Parameters = new Dictionary<string, string>(), OrchestrationInfo = s_defaultInfo };
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
		var executor = new PromptExecutor(agentBuilder, reporter, _formatter, _logger);

		var step = TestOrchestrations.CreatePromptStep("consumer", dependsOn: ["dep1", "dep2"]);

		var context = new OrchestrationExecutionContext { Parameters = new Dictionary<string, string>(), OrchestrationInfo = s_defaultInfo };
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
		var executor = new PromptExecutor(agentBuilder, reporter, _formatter, _logger);

		var step = TestOrchestrations.CreatePromptStep("looping-step");

		var context = new OrchestrationExecutionContext { Parameters = new Dictionary<string, string>(), OrchestrationInfo = s_defaultInfo };
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
		var executor = new PromptExecutor(agentBuilder, reporter, _formatter, _logger);

		var step = TestOrchestrations.CreatePromptStep("looping-step");

		var context = new OrchestrationExecutionContext { Parameters = new Dictionary<string, string>(), OrchestrationInfo = s_defaultInfo };
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
		var executor = new PromptExecutor(agentBuilder, reporter, _formatter, _logger);

		var step = TestOrchestrations.CreatePromptStep("traced-step");
		var context = new OrchestrationExecutionContext { Parameters = new Dictionary<string, string>(), OrchestrationInfo = s_defaultInfo };

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
		var executor = new PromptExecutor(agentBuilder, reporter, _formatter, _logger);

		var step = TestOrchestrations.CreatePromptStep("traced-step");
		var context = new OrchestrationExecutionContext { Parameters = new Dictionary<string, string>(), OrchestrationInfo = s_defaultInfo };

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
		var executor = new PromptExecutor(agentBuilder, reporter, _formatter, _logger);

		var step = TestOrchestrations.CreatePromptStep("consumer", dependsOn: ["producer"]);

		var context = new OrchestrationExecutionContext { Parameters = new Dictionary<string, string>(), OrchestrationInfo = s_defaultInfo };
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
		var executor = new PromptExecutor(agentBuilder, reporter, _formatter, _logger);

		var step = TestOrchestrations.CreatePromptStep("tool-step");
		var context = new OrchestrationExecutionContext { Parameters = new Dictionary<string, string>(), OrchestrationInfo = s_defaultInfo };

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
		var executor = new PromptExecutor(agentBuilder, reporter, _formatter, _logger);

		var step = TestOrchestrations.CreatePromptStep("tool-step");
		var context = new OrchestrationExecutionContext { Parameters = new Dictionary<string, string>(), OrchestrationInfo = s_defaultInfo };

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
		var executor = new PromptExecutor(agentBuilder, reporter, _formatter, _logger);

		var step = TestOrchestrations.CreatePromptStep("reasoning-step");
		var context = new OrchestrationExecutionContext { Parameters = new Dictionary<string, string>(), OrchestrationInfo = s_defaultInfo };

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
		var executor = new PromptExecutor(agentBuilder, reporter, _formatter, _logger);

		var step = TestOrchestrations.CreatePromptStep("reasoning-step");
		var context = new OrchestrationExecutionContext { Parameters = new Dictionary<string, string>(), OrchestrationInfo = s_defaultInfo };

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
		var executor = new PromptExecutor(agentBuilder, reporter, _formatter, _logger);

		var step = TestOrchestrations.CreatePromptStep("usage-step");
		var context = new OrchestrationExecutionContext { Parameters = new Dictionary<string, string>(), OrchestrationInfo = s_defaultInfo };

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
		var executor = new PromptExecutor(agentBuilder, reporter, _formatter, _logger);

		var step = TestOrchestrations.CreatePromptStep("usage-step");
		var context = new OrchestrationExecutionContext { Parameters = new Dictionary<string, string>(), OrchestrationInfo = s_defaultInfo };

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
		var executor = new PromptExecutor(agentBuilder, reporter, _formatter, _logger);

		var step = TestOrchestrations.CreatePromptStep("model-step", model: "gpt-4");
		var context = new OrchestrationExecutionContext { Parameters = new Dictionary<string, string>(), OrchestrationInfo = s_defaultInfo };

		// Act
		var result = await executor.ExecuteAsync(step, context);

		// Assert
		result.ActualModel.Should().Be("gpt-4-turbo");
	}

	#endregion

	#region SystemPromptMode

	[Fact]
	public async Task ExecuteAsync_StepSystemPromptMode_OverridesContextDefault()
	{
		// Arrange
		var agentBuilder = new MockAgentBuilder().WithResponse("Response");
		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new PromptExecutor(agentBuilder, reporter, _formatter, _logger);

		var step = TestOrchestrations.CreatePromptStepWithSystemPromptMode(
			"test-step",
			SystemPromptMode.Append); // Step explicitly sets Append

		var context = new OrchestrationExecutionContext
		{
			Parameters = new Dictionary<string, string>(),
			DefaultSystemPromptMode = SystemPromptMode.Replace, // Context default is Replace
			OrchestrationInfo = s_defaultInfo
		};

		// Act
		await executor.ExecuteAsync(step, context);

		// Assert - Step's mode should override context default
		agentBuilder.CapturedSystemPromptMode.Should().Be(SystemPromptMode.Append);
	}

	[Fact]
	public async Task ExecuteAsync_NoStepSystemPromptMode_UsesContextDefault()
	{
		// Arrange
		var agentBuilder = new MockAgentBuilder().WithResponse("Response");
		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new PromptExecutor(agentBuilder, reporter, _formatter, _logger);

		var step = TestOrchestrations.CreatePromptStep("test-step"); // No SystemPromptMode set

		var context = new OrchestrationExecutionContext
		{
			Parameters = new Dictionary<string, string>(),
			DefaultSystemPromptMode = SystemPromptMode.Replace, // Context default is Replace
			OrchestrationInfo = s_defaultInfo
		};

		// Act
		await executor.ExecuteAsync(step, context);

		// Assert - Should use context's default
		agentBuilder.CapturedSystemPromptMode.Should().Be(SystemPromptMode.Replace);
	}

	[Fact]
	public async Task ExecuteAsync_NoStepModeNoContextDefault_UsesNull()
	{
		// Arrange
		var agentBuilder = new MockAgentBuilder().WithResponse("Response");
		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new PromptExecutor(agentBuilder, reporter, _formatter, _logger);

		var step = TestOrchestrations.CreatePromptStep("test-step"); // No SystemPromptMode set

		var context = new OrchestrationExecutionContext
		{
			Parameters = new Dictionary<string, string>(),
			OrchestrationInfo = s_defaultInfo
			// No DefaultSystemPromptMode set
		};

		// Act
		await executor.ExecuteAsync(step, context);

		// Assert - Should be null (SDK default behavior)
		agentBuilder.CapturedSystemPromptMode.Should().BeNull();
	}

	[Fact]
	public async Task ExecuteAsync_StepModeReplace_PassesReplace()
	{
		// Arrange
		var agentBuilder = new MockAgentBuilder().WithResponse("Response");
		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new PromptExecutor(agentBuilder, reporter, _formatter, _logger);

		var step = TestOrchestrations.CreatePromptStepWithSystemPromptMode(
			"test-step",
			SystemPromptMode.Replace);

		var context = new OrchestrationExecutionContext { Parameters = new Dictionary<string, string>(), OrchestrationInfo = s_defaultInfo };

		// Act
		await executor.ExecuteAsync(step, context);

		// Assert
		agentBuilder.CapturedSystemPromptMode.Should().Be(SystemPromptMode.Replace);
	}

	[Fact]
	public async Task ExecuteAsync_ContextDefaultAppend_StepNullMode_UsesAppend()
	{
		// Arrange
		var agentBuilder = new MockAgentBuilder().WithResponse("Response");
		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new PromptExecutor(agentBuilder, reporter, _formatter, _logger);

		var step = TestOrchestrations.CreatePromptStep("test-step"); // No mode

		var context = new OrchestrationExecutionContext
		{
			Parameters = new Dictionary<string, string>(),
			DefaultSystemPromptMode = SystemPromptMode.Append,
			OrchestrationInfo = s_defaultInfo
		};

		// Act
		await executor.ExecuteAsync(step, context);

		// Assert
		agentBuilder.CapturedSystemPromptMode.Should().Be(SystemPromptMode.Append);
	}

	#endregion

	#region Template Resolution in UserPrompt

	[Fact]
	public async Task ExecuteAsync_ResolvesInlineStepOutputTemplates()
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
		var executor = new PromptExecutor(agentBuilder, reporter, _formatter, _logger);

		// Step references {{step-a.output}} inline in its userPrompt but depends on step-b
		var step = TestOrchestrations.CreatePromptStep(
			"consumer",
			dependsOn: ["step-b"],
			userPrompt: "Data from A: {{step-a.output}}\n\nPlease process.");

		var context = new OrchestrationExecutionContext { Parameters = new Dictionary<string, string>(), OrchestrationInfo = s_defaultInfo };
		context.AddResult("step-a", ExecutionResult.Succeeded("incident data from step A"));
		context.AddResult("step-b", ExecutionResult.Succeeded("check passed"));

		// Act
		await executor.ExecuteAsync(step, context);

		// Assert - The {{step-a.output}} should be resolved inline, not left as literal text
		capturedPrompt.Should().Contain("Data from A: incident data from step A");
		capturedPrompt.Should().NotContain("{{step-a.output}}");
	}

	[Fact]
	public async Task ExecuteAsync_ResolvesInlineTemplateFromDirectDependency()
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
		var executor = new PromptExecutor(agentBuilder, reporter, _formatter, _logger);

		// Step references {{dep.output}} inline and dep IS in dependsOn
		var step = TestOrchestrations.CreatePromptStep(
			"consumer",
			dependsOn: ["dep"],
			userPrompt: "Result: {{dep.output}}");

		var context = new OrchestrationExecutionContext { Parameters = new Dictionary<string, string>(), OrchestrationInfo = s_defaultInfo };
		context.AddResult("dep", ExecutionResult.Succeeded("dependency output value"));

		// Act
		await executor.ExecuteAsync(step, context);

		// Assert - Template resolved inline
		capturedPrompt.Should().Contain("Result: dependency output value");
		capturedPrompt.Should().NotContain("{{dep.output}}");
	}

	[Fact]
	public async Task ExecuteAsync_ResolvesMultipleInlineTemplates()
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
		var executor = new PromptExecutor(agentBuilder, reporter, _formatter, _logger);

		// Step references multiple outputs inline, some from direct deps, some transitive
		var step = TestOrchestrations.CreatePromptStep(
			"consumer",
			dependsOn: ["dep-b"],
			userPrompt: "A output: {{dep-a.output}}\n\nB output: {{dep-b.output}}");

		var context = new OrchestrationExecutionContext { Parameters = new Dictionary<string, string>(), OrchestrationInfo = s_defaultInfo };
		context.AddResult("dep-a", ExecutionResult.Succeeded("output from A"));
		context.AddResult("dep-b", ExecutionResult.Succeeded("output from B"));

		// Act
		await executor.ExecuteAsync(step, context);

		// Assert
		capturedPrompt.Should().Contain("A output: output from A");
		capturedPrompt.Should().Contain("B output: output from B");
		capturedPrompt.Should().NotContain("{{dep-a.output}}");
		capturedPrompt.Should().NotContain("{{dep-b.output}}");
	}

	[Fact]
	public async Task ExecuteAsync_UnresolvedTemplate_RemainsLiteral()
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
		var executor = new PromptExecutor(agentBuilder, reporter, _formatter, _logger);

		// Step references a step that doesn't exist in the context
		var step = TestOrchestrations.CreatePromptStep(
			"consumer",
			dependsOn: [],
			userPrompt: "Data: {{nonexistent-step.output}}");

		var context = new OrchestrationExecutionContext { Parameters = new Dictionary<string, string>(), OrchestrationInfo = s_defaultInfo };

		// Act
		await executor.ExecuteAsync(step, context);

		// Assert - Unresolvable template remains as-is
		capturedPrompt.Should().Contain("{{nonexistent-step.output}}");
	}

	[Fact]
	public async Task ExecuteAsync_ResolvesRawOutputTemplateInline()
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
		var executor = new PromptExecutor(agentBuilder, reporter, _formatter, _logger);

		var step = TestOrchestrations.CreatePromptStep(
			"consumer",
			dependsOn: ["dep"],
			userPrompt: "Raw: {{dep.rawOutput}}");

		var context = new OrchestrationExecutionContext { Parameters = new Dictionary<string, string>(), OrchestrationInfo = s_defaultInfo };
		context.AddResult("dep", ExecutionResult.Succeeded("processed", rawContent: "raw content here"));

		// Act
		await executor.ExecuteAsync(step, context);

		// Assert
		capturedPrompt.Should().Contain("Raw: raw content here");
		capturedPrompt.Should().NotContain("{{dep.rawOutput}}");
	}

	[Fact]
	public async Task ExecuteAsync_IcmAcknowledgeScenario_ResolvesTransitiveDependencyOutput()
	{
		// Arrange - This test simulates the exact icm-acknowledge.json bug scenario:
		// acknowledge-incidents depends on check-incidents, but references {{fetch-active-incidents.output}}
		// which is a transitive dependency (check-incidents depends on fetch-active-incidents)
		string? capturedPrompt = null;
		var agentBuilder = new MockAgentBuilder();
		agentBuilder.WithHandler((prompt, ct) =>
		{
			capturedPrompt = prompt;
			return MockAgentBuilderExtensions.CreateWithResponse("[12345, 67890]")
				.BuildAgentAsync(ct).Result.SendAsync(prompt, ct);
		});

		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new PromptExecutor(agentBuilder, reporter, _formatter, _logger);

		var step = TestOrchestrations.CreatePromptStep(
			"acknowledge-incidents",
			dependsOn: ["check-incidents"],
			userPrompt: "The following is the list of currently active IcM incidents:\n\n{{fetch-active-incidents.output}}\n\nFor each incident, acknowledge it.");

		var context = new OrchestrationExecutionContext { Parameters = new Dictionary<string, string>(), OrchestrationInfo = s_defaultInfo };
		context.AddResult("fetch-active-incidents", ExecutionResult.Succeeded("[{\"id\": 12345, \"title\": \"Server Down\"}, {\"id\": 67890, \"title\": \"High CPU\"}]"));
		context.AddResult("check-incidents", ExecutionResult.Succeeded("Proceeding with acknowledgment"));

		// Act
		await executor.ExecuteAsync(step, context);

		// Assert - The {{fetch-active-incidents.output}} must be resolved, not sent as literal
		capturedPrompt.Should().Contain("Server Down");
		capturedPrompt.Should().Contain("High CPU");
		capturedPrompt.Should().NotContain("{{fetch-active-incidents.output}}");
	}

	#endregion

	#region MCP Server Failure Detection

	[Fact]
	public async Task ExecuteAsync_RequiredMcpServerFailed_ReturnsFailedResult()
	{
		// Arrange - Mock agent that emits an MCP server failure event
		var mcpFailedEvents = new[]
		{
			new AgentEvent
			{
				Type = AgentEventType.McpServersLoaded,
				McpServerStatuses = new List<McpServerStatusInfo>
				{
					new("icm", "Failed", Error: "Connection timeout")
				}
			},
			new AgentEvent { Type = AgentEventType.MessageDelta, Content = "No IcM MCP tools are available." }
		};
		var agentBuilder = new MockAgentBuilder().WithResponse("No IcM MCP tools are available.", mcpFailedEvents);
		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new PromptExecutor(agentBuilder, reporter, _formatter, _logger);

		var step = TestOrchestrations.CreatePromptStep("acknowledge-incidents");
		step.Mcps = [new LocalMcp { Name = "icm", Type = McpType.Local, Command = "dnx", Arguments = ["IcM.Mcp"] }];

		var context = new OrchestrationExecutionContext { Parameters = new Dictionary<string, string>(), OrchestrationInfo = s_defaultInfo };

		// Act
		var result = await executor.ExecuteAsync(step, context);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Failed);
		result.ErrorMessage.Should().Contain("icm");
		result.ErrorMessage.Should().Contain("failed to start");
	}

	[Fact]
	public async Task ExecuteAsync_RequiredMcpServerFailed_ReportsError()
	{
		// Arrange
		var mcpFailedEvents = new[]
		{
			new AgentEvent
			{
				Type = AgentEventType.McpServersLoaded,
				McpServerStatuses = new List<McpServerStatusInfo>
				{
					new("icm", "Failed", Error: "Process exited")
				}
			},
			new AgentEvent { Type = AgentEventType.MessageDelta, Content = "No tools available." }
		};
		var agentBuilder = new MockAgentBuilder().WithResponse("No tools available.", mcpFailedEvents);
		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new PromptExecutor(agentBuilder, reporter, _formatter, _logger);

		var step = TestOrchestrations.CreatePromptStep("test-step");
		step.Mcps = [new LocalMcp { Name = "icm", Type = McpType.Local, Command = "dnx", Arguments = ["IcM.Mcp"] }];

		var context = new OrchestrationExecutionContext { Parameters = new Dictionary<string, string>(), OrchestrationInfo = s_defaultInfo };

		// Act
		await executor.ExecuteAsync(step, context);

		// Assert
		reporter.Received().ReportStepError("test-step", Arg.Is<string>(s => s.Contains("icm") && s.Contains("failed to start")));
	}

	[Fact]
	public async Task ExecuteAsync_NonRequiredMcpServerFailed_SucceedsNormally()
	{
		// Arrange - "graph" MCP failed but step only requires "icm" which connected
		var mcpEvents = new[]
		{
			new AgentEvent
			{
				Type = AgentEventType.McpServersLoaded,
				McpServerStatuses = new List<McpServerStatusInfo>
				{
					new("icm", "Connected"),
					new("graph", "Failed", Error: "Connection refused")
				}
			},
			new AgentEvent { Type = AgentEventType.MessageDelta, Content = "Acknowledged incidents." }
		};
		var agentBuilder = new MockAgentBuilder().WithResponse("Acknowledged incidents.", mcpEvents);
		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new PromptExecutor(agentBuilder, reporter, _formatter, _logger);

		var step = TestOrchestrations.CreatePromptStep("test-step");
		step.Mcps = [new LocalMcp { Name = "icm", Type = McpType.Local, Command = "dnx", Arguments = ["IcM.Mcp"] }];

		var context = new OrchestrationExecutionContext { Parameters = new Dictionary<string, string>(), OrchestrationInfo = s_defaultInfo };

		// Act
		var result = await executor.ExecuteAsync(step, context);

		// Assert - Step should succeed because its required MCP ("icm") connected
		result.Status.Should().Be(ExecutionStatus.Succeeded);
		result.Content.Should().Be("Acknowledged incidents.");
	}

	[Fact]
	public async Task ExecuteAsync_NoMcpServersConfigured_IgnoresFailedServers()
	{
		// Arrange - MCP servers failed but step doesn't require any
		var mcpEvents = new[]
		{
			new AgentEvent
			{
				Type = AgentEventType.McpServersLoaded,
				McpServerStatuses = new List<McpServerStatusInfo>
				{
					new("graph", "Failed", Error: "Connection refused")
				}
			},
			new AgentEvent { Type = AgentEventType.MessageDelta, Content = "Success." }
		};
		var agentBuilder = new MockAgentBuilder().WithResponse("Success.", mcpEvents);
		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new PromptExecutor(agentBuilder, reporter, _formatter, _logger);

		var step = TestOrchestrations.CreatePromptStep("test-step");
		// No MCPs configured on this step

		var context = new OrchestrationExecutionContext { Parameters = new Dictionary<string, string>(), OrchestrationInfo = s_defaultInfo };

		// Act
		var result = await executor.ExecuteAsync(step, context);

		// Assert - Step should succeed because it doesn't require any MCPs
		result.Status.Should().Be(ExecutionStatus.Succeeded);
	}

	[Fact]
	public async Task ExecuteAsync_RequiredMcpServerFailed_IncludesTraceInResult()
	{
		// Arrange
		var mcpFailedEvents = new[]
		{
			new AgentEvent
			{
				Type = AgentEventType.McpServersLoaded,
				McpServerStatuses = new List<McpServerStatusInfo>
				{
					new("icm", "Failed", Error: "Server crashed")
				}
			},
			new AgentEvent { Type = AgentEventType.MessageDelta, Content = "No tools." }
		};
		var agentBuilder = new MockAgentBuilder().WithResponse("No tools.", mcpFailedEvents);
		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new PromptExecutor(agentBuilder, reporter, _formatter, _logger);

		var step = TestOrchestrations.CreatePromptStep("test-step");
		step.Mcps = [new LocalMcp { Name = "icm", Type = McpType.Local, Command = "dnx", Arguments = ["IcM.Mcp"] }];

		var context = new OrchestrationExecutionContext { Parameters = new Dictionary<string, string>(), OrchestrationInfo = s_defaultInfo };

		// Act
		var result = await executor.ExecuteAsync(step, context);

		// Assert - Trace should be included even on MCP failure
		result.Trace.Should().NotBeNull();
		result.Trace!.SystemPrompt.Should().NotBeNull();
	}

	[Fact]
	public async Task ExecuteAsync_MultipleMcpServersAllFailed_ErrorListsAllServers()
	{
		// Arrange
		var mcpFailedEvents = new[]
		{
			new AgentEvent
			{
				Type = AgentEventType.McpServersLoaded,
				McpServerStatuses = new List<McpServerStatusInfo>
				{
					new("icm", "Failed", Error: "Timeout"),
					new("graph", "Failed", Error: "Connection refused")
				}
			},
			new AgentEvent { Type = AgentEventType.MessageDelta, Content = "No tools." }
		};
		var agentBuilder = new MockAgentBuilder().WithResponse("No tools.", mcpFailedEvents);
		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new PromptExecutor(agentBuilder, reporter, _formatter, _logger);

		var step = TestOrchestrations.CreatePromptStep("test-step");
		step.Mcps =
		[
			new LocalMcp { Name = "icm", Type = McpType.Local, Command = "dnx", Arguments = ["IcM.Mcp"] },
			new LocalMcp { Name = "graph", Type = McpType.Local, Command = "dnx", Arguments = ["Graph.Mcp"] }
		];

		var context = new OrchestrationExecutionContext { Parameters = new Dictionary<string, string>(), OrchestrationInfo = s_defaultInfo };

		// Act
		var result = await executor.ExecuteAsync(step, context);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Failed);
		result.ErrorMessage.Should().Contain("icm");
		result.ErrorMessage.Should().Contain("graph");
	}

	#endregion

	#region Skill Directories

	[Fact]
	public async Task ExecuteAsync_WithSkillDirectories_PassesSkillDirectoriesToBuilder()
	{
		// Arrange
		var agentBuilder = new MockAgentBuilder().WithResponse("Response");
		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new PromptExecutor(agentBuilder, reporter, _formatter, _logger);

		var step = TestOrchestrations.CreatePromptStep("skill-step");
		step.SkillDirectories = ["./skills/coding", "/absolute/skills/devops"];

		var context = new OrchestrationExecutionContext { Parameters = new Dictionary<string, string>(), OrchestrationInfo = s_defaultInfo };

		// Act
		await executor.ExecuteAsync(step, context);

		// Assert
		agentBuilder.CapturedConfig.Should().NotBeNull();
		agentBuilder.CapturedConfig!.SkillDirectories.Should().HaveCount(2);
		agentBuilder.CapturedConfig.SkillDirectories.Should().Contain("./skills/coding");
		agentBuilder.CapturedConfig.SkillDirectories.Should().Contain("/absolute/skills/devops");
	}

	[Fact]
	public async Task ExecuteAsync_WithSkillDirectories_ResolvesTemplateVariables()
	{
		// Arrange
		var agentBuilder = new MockAgentBuilder().WithResponse("Response");
		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new PromptExecutor(agentBuilder, reporter, _formatter, _logger);

		var step = TestOrchestrations.CreatePromptStep("skill-step");
		step.SkillDirectories = ["{{vars.skillsDir}}", "./relative/skills"];

		var context = new OrchestrationExecutionContext
		{
			Parameters = new Dictionary<string, string>(),
			OrchestrationInfo = s_defaultInfo,
			Variables = new Dictionary<string, string>
			{
				["skillsDir"] = @"P:\Github\OrcStra-Uruk\Skills"
			}
		};

		// Act
		await executor.ExecuteAsync(step, context);

		// Assert - Template variable should be resolved, relative path should remain as-is
		agentBuilder.CapturedConfig.Should().NotBeNull();
		agentBuilder.CapturedConfig!.SkillDirectories.Should().HaveCount(2);
		agentBuilder.CapturedConfig.SkillDirectories[0].Should().Be(@"P:\Github\OrcStra-Uruk\Skills");
		agentBuilder.CapturedConfig.SkillDirectories[1].Should().Be("./relative/skills");
	}

	[Fact]
	public async Task ExecuteAsync_WithoutSkillDirectories_PassesEmptyArray()
	{
		// Arrange
		var agentBuilder = new MockAgentBuilder().WithResponse("Response");
		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new PromptExecutor(agentBuilder, reporter, _formatter, _logger);

		var step = TestOrchestrations.CreatePromptStep("no-skills-step");

		var context = new OrchestrationExecutionContext { Parameters = new Dictionary<string, string>(), OrchestrationInfo = s_defaultInfo };

		// Act
		await executor.ExecuteAsync(step, context);

		// Assert
		agentBuilder.CapturedConfig.Should().NotBeNull();
		agentBuilder.CapturedConfig!.SkillDirectories.Should().BeEmpty();
	}

	#endregion

	#region Template Resolution in Model

	[Fact]
	public async Task ExecuteAsync_WithModelTemplateVariable_ResolvesBeforePassingToBuilder()
	{
		// Arrange
		var agentBuilder = new MockAgentBuilder().WithResponse("Response");
		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new PromptExecutor(agentBuilder, reporter, _formatter, _logger);

		var step = TestOrchestrations.CreatePromptStep("model-var-step", model: "{{vars.defaultModel}}");

		var context = new OrchestrationExecutionContext
		{
			Parameters = new Dictionary<string, string>(),
			OrchestrationInfo = s_defaultInfo,
			Variables = new Dictionary<string, string>
			{
				["defaultModel"] = "claude-opus-4.5"
			}
		};

		// Act
		await executor.ExecuteAsync(step, context);

		// Assert - Model should be resolved from the variable, not the literal template string
		agentBuilder.CapturedConfig.Should().NotBeNull();
		agentBuilder.CapturedConfig!.Model.Should().Be("claude-opus-4.5");
	}

	[Fact]
	public async Task ExecuteAsync_WithModelParameterTemplate_ResolvesBeforePassingToBuilder()
	{
		// Arrange
		var agentBuilder = new MockAgentBuilder().WithResponse("Response");
		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new PromptExecutor(agentBuilder, reporter, _formatter, _logger);

		var step = TestOrchestrations.CreatePromptStep("model-param-step", model: "{{param.model}}");

		var context = new OrchestrationExecutionContext
		{
			Parameters = new Dictionary<string, string>
			{
				["model"] = "gpt-4o"
			},
			OrchestrationInfo = s_defaultInfo,
		};

		// Act
		await executor.ExecuteAsync(step, context);

		// Assert
		agentBuilder.CapturedConfig.Should().NotBeNull();
		agentBuilder.CapturedConfig!.Model.Should().Be("gpt-4o");
	}

	[Fact]
	public async Task ExecuteAsync_WithLiteralModel_PassesModelUnchanged()
	{
		// Arrange
		var agentBuilder = new MockAgentBuilder().WithResponse("Response");
		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new PromptExecutor(agentBuilder, reporter, _formatter, _logger);

		var step = TestOrchestrations.CreatePromptStep("literal-model-step", model: "claude-opus-4.5");

		var context = new OrchestrationExecutionContext
		{
			Parameters = new Dictionary<string, string>(),
			OrchestrationInfo = s_defaultInfo,
		};

		// Act
		await executor.ExecuteAsync(step, context);

		// Assert
		agentBuilder.CapturedConfig.Should().NotBeNull();
		agentBuilder.CapturedConfig!.Model.Should().Be("claude-opus-4.5");
	}

	#endregion

	#region Template Resolution in SystemPrompt

	[Fact]
	public async Task ExecuteAsync_WithSystemPromptTemplateVariable_ResolvesBeforePassingToBuilder()
	{
		// Arrange
		var agentBuilder = new MockAgentBuilder().WithResponse("Response");
		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new PromptExecutor(agentBuilder, reporter, _formatter, _logger);

		var step = TestOrchestrations.CreatePromptStep("sysprompt-step",
			systemPrompt: "You are reviewing code for {{vars.project}}.");

		var context = new OrchestrationExecutionContext
		{
			Parameters = new Dictionary<string, string>(),
			OrchestrationInfo = s_defaultInfo,
			Variables = new Dictionary<string, string>
			{
				["project"] = "Orchestra"
			}
		};

		// Act
		await executor.ExecuteAsync(step, context);

		// Assert
		agentBuilder.CapturedConfig.Should().NotBeNull();
		agentBuilder.CapturedConfig!.SystemPrompt.Should().Be("You are reviewing code for Orchestra.");
	}

	[Fact]
	public async Task ExecuteAsync_WithSystemPromptStepOutputTemplate_ResolvesBeforePassingToBuilder()
	{
		// Arrange
		var agentBuilder = new MockAgentBuilder().WithResponse("Response");
		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new PromptExecutor(agentBuilder, reporter, _formatter, _logger);

		var step = TestOrchestrations.CreatePromptStep("sysprompt-step",
			dependsOn: ["context-step"],
			systemPrompt: "You are a reviewer. Context: {{context-step.output}}");

		var context = new OrchestrationExecutionContext
		{
			Parameters = new Dictionary<string, string>(),
			OrchestrationInfo = s_defaultInfo,
		};
		context.AddResult("context-step", ExecutionResult.Succeeded("important context data"));

		// Act
		await executor.ExecuteAsync(step, context);

		// Assert
		agentBuilder.CapturedConfig.Should().NotBeNull();
		agentBuilder.CapturedConfig!.SystemPrompt.Should().Be("You are a reviewer. Context: important context data");
	}

	#endregion
}
