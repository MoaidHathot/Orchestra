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
						"dependsOn": [],
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
						"dependsOn": [],
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
						"dependsOn": [],
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
}
