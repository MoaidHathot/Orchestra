using FluentAssertions;

namespace Orchestra.Engine.Tests.Serialization;

public class TimeoutParsingTests
{
	[Fact]
	public void ParseOrchestration_WithTimeoutSeconds_ParsesValue()
	{
		// Arrange
		var json = """
			{
				"name": "timeout-test",
				"description": "Test timeout parsing",
				"steps": [
					{
						"name": "slow-step",
						"type": "prompt",
						"systemPrompt": "You are a test assistant.",
						"userPrompt": "Test prompt",
						"model": "claude-opus-4.5",
						"timeoutSeconds": 30
					}
				]
			}
			""";

		// Act
		var orchestration = OrchestrationParser.ParseOrchestration(json, []);

		// Assert
		var step = orchestration.Steps[0] as PromptOrchestrationStep;
		step.Should().NotBeNull();
		step!.TimeoutSeconds.Should().Be(30);
		step.DependsOn.Should().BeEmpty();
	}

	[Fact]
	public void ParseOrchestration_WithoutTimeoutSeconds_DefaultsToNull()
	{
		// Arrange
		var json = """
			{
				"name": "no-timeout",
				"description": "Test no timeout",
				"steps": [
					{
						"name": "normal-step",
						"type": "prompt",
						"systemPrompt": "You are a test assistant.",
						"userPrompt": "Test prompt",
						"model": "claude-opus-4.5"
					}
				]
			}
			""";

		// Act
		var orchestration = OrchestrationParser.ParseOrchestration(json, []);

		// Assert
		var step = orchestration.Steps[0] as PromptOrchestrationStep;
		step.Should().NotBeNull();
		step!.TimeoutSeconds.Should().BeNull();
		step.DependsOn.Should().BeEmpty();
	}

	[Fact]
	public void ParseOrchestration_WithZeroTimeoutSeconds_ParsesZero()
	{
		// Arrange
		var json = """
			{
				"name": "zero-timeout",
				"description": "Test zero timeout",
				"steps": [
					{
						"name": "step1",
						"type": "prompt",
						"systemPrompt": "You are a test assistant.",
						"userPrompt": "Test prompt",
						"model": "claude-opus-4.5",
						"timeoutSeconds": 0
					}
				]
			}
			""";

		// Act
		var orchestration = OrchestrationParser.ParseOrchestration(json, []);

		// Assert
		var step = orchestration.Steps[0] as PromptOrchestrationStep;
		step.Should().NotBeNull();
		step!.TimeoutSeconds.Should().Be(0);
		step.DependsOn.Should().BeEmpty();
	}

	[Fact]
	public void ParseOrchestration_WithLargeTimeoutSeconds_ParsesValue()
	{
		// Arrange
		var json = """
			{
				"name": "large-timeout",
				"description": "Test large timeout",
				"steps": [
					{
						"name": "step1",
						"type": "prompt",
						"dependsOn": [],
						"systemPrompt": "You are a test assistant.",
						"userPrompt": "Test prompt",
						"model": "claude-opus-4.5",
						"timeoutSeconds": 3600
					}
				]
			}
			""";

		// Act
		var orchestration = OrchestrationParser.ParseOrchestration(json, []);

		// Assert
		var step = orchestration.Steps[0] as PromptOrchestrationStep;
		step.Should().NotBeNull();
		step!.TimeoutSeconds.Should().Be(3600);
	}

	[Fact]
	public void ParseOrchestration_MultipleStepsWithMixedTimeouts_ParsesCorrectly()
	{
		// Arrange
		var json = """
			{
				"name": "mixed-timeouts",
				"description": "Test mixed timeouts",
				"steps": [
					{
						"name": "fast",
						"type": "prompt",
						"dependsOn": [],
						"systemPrompt": "Fast assistant",
						"userPrompt": "Quick task",
						"model": "claude-opus-4.5",
						"timeoutSeconds": 10
					},
					{
						"name": "slow",
						"type": "prompt",
						"dependsOn": ["fast"],
						"systemPrompt": "Slow assistant",
						"userPrompt": "Long task",
						"model": "claude-opus-4.5",
						"timeoutSeconds": 120
					},
					{
						"name": "unlimited",
						"type": "prompt",
						"dependsOn": ["fast"],
						"systemPrompt": "No timeout",
						"userPrompt": "Unlimited task",
						"model": "claude-opus-4.5"
					}
				]
			}
			""";

		// Act
		var orchestration = OrchestrationParser.ParseOrchestration(json, []);

		// Assert
		orchestration.Steps.Should().HaveCount(3);

		var fast = orchestration.Steps[0] as PromptOrchestrationStep;
		fast!.TimeoutSeconds.Should().Be(10);

		var slow = orchestration.Steps[1] as PromptOrchestrationStep;
		slow!.TimeoutSeconds.Should().Be(120);

		var unlimited = orchestration.Steps[2] as PromptOrchestrationStep;
		unlimited!.TimeoutSeconds.Should().BeNull();
	}

	[Fact]
	public void ParseOrchestration_TimeoutWithOtherFields_AllFieldsParsed()
	{
		// Arrange
		var json = """
			{
				"name": "timeout-all-fields",
				"description": "Test timeout with all fields",
				"steps": [
					{
						"name": "full-step",
						"type": "prompt",
						"dependsOn": [],
						"systemPrompt": "System prompt",
						"userPrompt": "User prompt with {{topic}}",
						"model": "gpt-4",
						"parameters": ["topic"],
						"reasoningLevel": "high",
						"systemPromptMode": "replace",
						"timeoutSeconds": 60
					}
				]
			}
			""";

		// Act
		var orchestration = OrchestrationParser.ParseOrchestration(json, []);

		// Assert
		var step = orchestration.Steps[0] as PromptOrchestrationStep;
		step.Should().NotBeNull();
		step!.Name.Should().Be("full-step");
		step.Model.Should().Be("gpt-4");
		step.ReasoningLevel.Should().Be(ReasoningLevel.High);
		step.SystemPromptMode.Should().Be(SystemPromptMode.Replace);
		step.TimeoutSeconds.Should().Be(60);
		step.Parameters.Should().Contain("topic");
	}

	#region DefaultStepTimeoutSeconds Parsing

	[Fact]
	public void ParseOrchestration_WithDefaultStepTimeoutSeconds_ParsesValue()
	{
		// Arrange
		var json = """
			{
				"name": "default-step-timeout",
				"description": "Test default step timeout",
				"defaultStepTimeoutSeconds": 30,
				"steps": [
					{
						"name": "step1",
						"type": "prompt",
						"systemPrompt": "You are a test assistant.",
						"userPrompt": "Test prompt",
						"model": "claude-opus-4.5"
					}
				]
			}
			""";

		// Act
		var orchestration = OrchestrationParser.ParseOrchestration(json, []);

		// Assert
		orchestration.DefaultStepTimeoutSeconds.Should().Be(30);
	}

	[Fact]
	public void ParseOrchestration_WithoutDefaultStepTimeoutSeconds_DefaultsToNull()
	{
		// Arrange
		var json = """
			{
				"name": "no-default-step-timeout",
				"description": "Test no default step timeout",
				"steps": [
					{
						"name": "step1",
						"type": "prompt",
						"systemPrompt": "You are a test assistant.",
						"userPrompt": "Test prompt",
						"model": "claude-opus-4.5"
					}
				]
			}
			""";

		// Act
		var orchestration = OrchestrationParser.ParseOrchestration(json, []);

		// Assert
		orchestration.DefaultStepTimeoutSeconds.Should().BeNull();
	}

	[Fact]
	public void ParseOrchestration_WithZeroDefaultStepTimeoutSeconds_ParsesZero()
	{
		// Arrange
		var json = """
			{
				"name": "zero-default-step-timeout",
				"description": "Test zero default step timeout",
				"defaultStepTimeoutSeconds": 0,
				"steps": []
			}
			""";

		// Act
		var orchestration = OrchestrationParser.ParseOrchestration(json, []);

		// Assert
		orchestration.DefaultStepTimeoutSeconds.Should().Be(0);
	}

	[Fact]
	public void ParseOrchestration_WithNullDefaultStepTimeoutSeconds_ParsesNull()
	{
		// Arrange
		var json = """
			{
				"name": "null-default-step-timeout",
				"description": "Test null default step timeout",
				"defaultStepTimeoutSeconds": null,
				"steps": []
			}
			""";

		// Act
		var orchestration = OrchestrationParser.ParseOrchestration(json, []);

		// Assert
		orchestration.DefaultStepTimeoutSeconds.Should().BeNull();
	}

	[Fact]
	public void ParseOrchestration_DefaultStepTimeoutWithStepTimeout_BothParsed()
	{
		// Arrange
		var json = """
			{
				"name": "both-timeouts",
				"description": "Test both default and step-level timeout",
				"defaultStepTimeoutSeconds": 60,
				"timeoutSeconds": 7200,
				"steps": [
					{
						"name": "with-timeout",
						"type": "prompt",
						"systemPrompt": "System",
						"userPrompt": "User",
						"model": "claude-opus-4.5",
						"timeoutSeconds": 30
					},
					{
						"name": "without-timeout",
						"type": "prompt",
						"systemPrompt": "System",
						"userPrompt": "User",
						"model": "claude-opus-4.5"
					}
				]
			}
			""";

		// Act
		var orchestration = OrchestrationParser.ParseOrchestration(json, []);

		// Assert
		orchestration.DefaultStepTimeoutSeconds.Should().Be(60);
		orchestration.TimeoutSeconds.Should().Be(7200);

		var stepWithTimeout = orchestration.Steps[0] as PromptOrchestrationStep;
		stepWithTimeout!.TimeoutSeconds.Should().Be(30);

		var stepWithoutTimeout = orchestration.Steps[1] as PromptOrchestrationStep;
		stepWithoutTimeout!.TimeoutSeconds.Should().BeNull();
	}

	#endregion
}
