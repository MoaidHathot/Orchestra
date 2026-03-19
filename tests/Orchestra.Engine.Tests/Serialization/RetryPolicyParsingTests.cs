using FluentAssertions;

namespace Orchestra.Engine.Tests.Serialization;

public class RetryPolicyParsingTests
{
	#region Step-Level Retry Parsing

	[Fact]
	public void ParseOrchestration_StepWithRetryPolicy_ParsesAllFields()
	{
		// Arrange
		var json = """
			{
				"name": "retry-test",
				"description": "Test retry parsing",
				"steps": [
					{
						"name": "flaky-step",
						"type": "prompt",
						"dependsOn": [],
						"systemPrompt": "You are a test assistant.",
						"userPrompt": "Test prompt",
						"model": "claude-opus-4.5",
						"retry": {
							"maxRetries": 5,
							"backoffSeconds": 2.0,
							"backoffMultiplier": 3.0,
							"retryOnTimeout": false
						}
					}
				]
			}
			""";

		// Act
		var orchestration = OrchestrationParser.ParseOrchestration(json, []);

		// Assert
		var step = orchestration.Steps[0] as PromptOrchestrationStep;
		step.Should().NotBeNull();
		step!.Retry.Should().NotBeNull();
		step.Retry!.MaxRetries.Should().Be(5);
		step.Retry.BackoffSeconds.Should().Be(2.0);
		step.Retry.BackoffMultiplier.Should().Be(3.0);
		step.Retry.RetryOnTimeout.Should().BeFalse();
	}

	[Fact]
	public void ParseOrchestration_StepWithRetryDefaults_UsesDefaults()
	{
		// Arrange
		var json = """
			{
				"name": "retry-defaults",
				"description": "Test retry defaults",
				"steps": [
					{
						"name": "step1",
						"type": "prompt",
						"dependsOn": [],
						"systemPrompt": "Test",
						"userPrompt": "Test",
						"model": "claude-opus-4.5",
						"retry": {}
					}
				]
			}
			""";

		// Act
		var orchestration = OrchestrationParser.ParseOrchestration(json, []);

		// Assert
		var step = orchestration.Steps[0] as PromptOrchestrationStep;
		step!.Retry.Should().NotBeNull();
		step.Retry!.MaxRetries.Should().Be(3);
		step.Retry.BackoffSeconds.Should().Be(1.0);
		step.Retry.BackoffMultiplier.Should().Be(2.0);
		step.Retry.RetryOnTimeout.Should().BeTrue();
	}

	[Fact]
	public void ParseOrchestration_StepWithoutRetry_RetryIsNull()
	{
		// Arrange
		var json = """
			{
				"name": "no-retry",
				"description": "Test no retry",
				"steps": [
					{
						"name": "step1",
						"type": "prompt",
						"dependsOn": [],
						"systemPrompt": "Test",
						"userPrompt": "Test",
						"model": "claude-opus-4.5"
					}
				]
			}
			""";

		// Act
		var orchestration = OrchestrationParser.ParseOrchestration(json, []);

		// Assert
		var step = orchestration.Steps[0] as PromptOrchestrationStep;
		step!.Retry.Should().BeNull();
	}

	[Fact]
	public void ParseOrchestration_StepWithPartialRetry_MergesWithDefaults()
	{
		// Arrange — only specify maxRetries, rest should be defaults
		var json = """
			{
				"name": "partial-retry",
				"description": "Test partial retry",
				"steps": [
					{
						"name": "step1",
						"type": "prompt",
						"dependsOn": [],
						"systemPrompt": "Test",
						"userPrompt": "Test",
						"model": "claude-opus-4.5",
						"retry": {
							"maxRetries": 10
						}
					}
				]
			}
			""";

		// Act
		var orchestration = OrchestrationParser.ParseOrchestration(json, []);

		// Assert
		var step = orchestration.Steps[0] as PromptOrchestrationStep;
		step!.Retry.Should().NotBeNull();
		step.Retry!.MaxRetries.Should().Be(10);
		step.Retry.BackoffSeconds.Should().Be(1.0);   // default
		step.Retry.BackoffMultiplier.Should().Be(2.0); // default
		step.Retry.RetryOnTimeout.Should().BeTrue();    // default
	}

	[Fact]
	public void ParseOrchestration_StepWithRetryAndTimeout_BothParsed()
	{
		// Arrange
		var json = """
			{
				"name": "retry-timeout",
				"description": "Test both retry and timeout",
				"steps": [
					{
						"name": "step1",
						"type": "prompt",
						"dependsOn": [],
						"systemPrompt": "Test",
						"userPrompt": "Test",
						"model": "claude-opus-4.5",
						"timeoutSeconds": 30,
						"retry": {
							"maxRetries": 2,
							"retryOnTimeout": false
						}
					}
				]
			}
			""";

		// Act
		var orchestration = OrchestrationParser.ParseOrchestration(json, []);

		// Assert
		var step = orchestration.Steps[0] as PromptOrchestrationStep;
		step!.TimeoutSeconds.Should().Be(30);
		step.Retry.Should().NotBeNull();
		step.Retry!.MaxRetries.Should().Be(2);
		step.Retry.RetryOnTimeout.Should().BeFalse();
	}

	[Fact]
	public void ParseOrchestration_MultipleStepsWithMixedRetry_ParsesCorrectly()
	{
		// Arrange
		var json = """
			{
				"name": "mixed-retry",
				"description": "Test mixed retry",
				"steps": [
					{
						"name": "with-retry",
						"type": "prompt",
						"dependsOn": [],
						"systemPrompt": "Test",
						"userPrompt": "Test",
						"model": "claude-opus-4.5",
						"retry": {
							"maxRetries": 5
						}
					},
					{
						"name": "without-retry",
						"type": "prompt",
						"dependsOn": ["with-retry"],
						"systemPrompt": "Test",
						"userPrompt": "Test",
						"model": "claude-opus-4.5"
					},
					{
						"name": "custom-retry",
						"type": "prompt",
						"dependsOn": ["with-retry"],
						"systemPrompt": "Test",
						"userPrompt": "Test",
						"model": "claude-opus-4.5",
						"retry": {
							"maxRetries": 1,
							"backoffSeconds": 5.0,
							"backoffMultiplier": 1.0
						}
					}
				]
			}
			""";

		// Act
		var orchestration = OrchestrationParser.ParseOrchestration(json, []);

		// Assert
		orchestration.Steps.Should().HaveCount(3);

		var withRetry = orchestration.Steps[0] as PromptOrchestrationStep;
		withRetry!.Retry.Should().NotBeNull();
		withRetry.Retry!.MaxRetries.Should().Be(5);

		var withoutRetry = orchestration.Steps[1] as PromptOrchestrationStep;
		withoutRetry!.Retry.Should().BeNull();

		var customRetry = orchestration.Steps[2] as PromptOrchestrationStep;
		customRetry!.Retry.Should().NotBeNull();
		customRetry.Retry!.MaxRetries.Should().Be(1);
		customRetry.Retry.BackoffSeconds.Should().Be(5.0);
		customRetry.Retry.BackoffMultiplier.Should().Be(1.0);
	}

	#endregion

	#region Orchestration-Level Default Retry Policy

	[Fact]
	public void ParseOrchestration_WithDefaultRetryPolicy_ParsesAllFields()
	{
		// Arrange
		var json = """
			{
				"name": "default-retry",
				"description": "Test default retry policy",
				"defaultRetryPolicy": {
					"maxRetries": 4,
					"backoffSeconds": 0.5,
					"backoffMultiplier": 1.5,
					"retryOnTimeout": false
				},
				"steps": []
			}
			""";

		// Act
		var orchestration = OrchestrationParser.ParseOrchestration(json, []);

		// Assert
		orchestration.DefaultRetryPolicy.Should().NotBeNull();
		orchestration.DefaultRetryPolicy!.MaxRetries.Should().Be(4);
		orchestration.DefaultRetryPolicy.BackoffSeconds.Should().Be(0.5);
		orchestration.DefaultRetryPolicy.BackoffMultiplier.Should().Be(1.5);
		orchestration.DefaultRetryPolicy.RetryOnTimeout.Should().BeFalse();
	}

	[Fact]
	public void ParseOrchestration_WithoutDefaultRetryPolicy_DefaultsToNull()
	{
		// Arrange
		var json = """
			{
				"name": "no-default-retry",
				"description": "Test no default retry",
				"steps": []
			}
			""";

		// Act
		var orchestration = OrchestrationParser.ParseOrchestration(json, []);

		// Assert
		orchestration.DefaultRetryPolicy.Should().BeNull();
	}

	[Fact]
	public void ParseOrchestration_WithEmptyDefaultRetryPolicy_UsesDefaults()
	{
		// Arrange
		var json = """
			{
				"name": "empty-default-retry",
				"description": "Test empty default retry",
				"defaultRetryPolicy": {},
				"steps": []
			}
			""";

		// Act
		var orchestration = OrchestrationParser.ParseOrchestration(json, []);

		// Assert
		orchestration.DefaultRetryPolicy.Should().NotBeNull();
		orchestration.DefaultRetryPolicy!.MaxRetries.Should().Be(3);
		orchestration.DefaultRetryPolicy.BackoffSeconds.Should().Be(1.0);
		orchestration.DefaultRetryPolicy.BackoffMultiplier.Should().Be(2.0);
		orchestration.DefaultRetryPolicy.RetryOnTimeout.Should().BeTrue();
	}

	#endregion

	#region Orchestration-Level TimeoutSeconds

	[Fact]
	public void ParseOrchestration_WithTimeoutSeconds_ParsesValue()
	{
		// Arrange
		var json = """
			{
				"name": "orch-timeout",
				"description": "Test orchestration timeout",
				"timeoutSeconds": 120,
				"steps": []
			}
			""";

		// Act
		var orchestration = OrchestrationParser.ParseOrchestration(json, []);

		// Assert
		orchestration.TimeoutSeconds.Should().Be(120);
	}

	[Fact]
	public void ParseOrchestration_WithoutTimeoutSeconds_DefaultsTo3600()
	{
		// Arrange
		var json = """
			{
				"name": "no-orch-timeout",
				"description": "Test default timeout",
				"steps": []
			}
			""";

		// Act
		var orchestration = OrchestrationParser.ParseOrchestration(json, []);

		// Assert
		orchestration.TimeoutSeconds.Should().Be(3600);
	}

	[Fact]
	public void ParseOrchestration_WithZeroTimeoutSeconds_ParsesZero()
	{
		// Arrange
		var json = """
			{
				"name": "zero-orch-timeout",
				"description": "Test zero timeout disables it",
				"timeoutSeconds": 0,
				"steps": []
			}
			""";

		// Act
		var orchestration = OrchestrationParser.ParseOrchestration(json, []);

		// Assert
		orchestration.TimeoutSeconds.Should().Be(0);
	}

	[Fact]
	public void ParseOrchestration_WithNullTimeoutSeconds_ParsesNull()
	{
		// Arrange
		var json = """
			{
				"name": "null-orch-timeout",
				"description": "Test null timeout",
				"timeoutSeconds": null,
				"steps": []
			}
			""";

		// Act
		var orchestration = OrchestrationParser.ParseOrchestration(json, []);

		// Assert
		orchestration.TimeoutSeconds.Should().BeNull();
	}

	#endregion

	#region Combined Parsing

	[Fact]
	public void ParseOrchestration_AllRetryAndTimeoutFields_ParsedTogether()
	{
		// Arrange
		var json = """
			{
				"name": "full-config",
				"description": "All retry and timeout fields",
				"timeoutSeconds": 7200,
				"defaultRetryPolicy": {
					"maxRetries": 2,
					"backoffSeconds": 0.5
				},
				"steps": [
					{
						"name": "step-with-override",
						"type": "prompt",
						"dependsOn": [],
						"systemPrompt": "Test",
						"userPrompt": "Test",
						"model": "claude-opus-4.5",
						"timeoutSeconds": 30,
						"retry": {
							"maxRetries": 5,
							"backoffSeconds": 1.0,
							"retryOnTimeout": false
						}
					},
					{
						"name": "step-uses-default",
						"type": "prompt",
						"dependsOn": [],
						"systemPrompt": "Test",
						"userPrompt": "Test",
						"model": "claude-opus-4.5"
					}
				]
			}
			""";

		// Act
		var orchestration = OrchestrationParser.ParseOrchestration(json, []);

		// Assert
		orchestration.TimeoutSeconds.Should().Be(7200);
		orchestration.DefaultRetryPolicy.Should().NotBeNull();
		orchestration.DefaultRetryPolicy!.MaxRetries.Should().Be(2);
		orchestration.DefaultRetryPolicy.BackoffSeconds.Should().Be(0.5);

		var stepWithOverride = orchestration.Steps[0] as PromptOrchestrationStep;
		stepWithOverride!.TimeoutSeconds.Should().Be(30);
		stepWithOverride.Retry.Should().NotBeNull();
		stepWithOverride.Retry!.MaxRetries.Should().Be(5);
		stepWithOverride.Retry.RetryOnTimeout.Should().BeFalse();

		var stepUsesDefault = orchestration.Steps[1] as PromptOrchestrationStep;
		stepUsesDefault!.TimeoutSeconds.Should().BeNull();
		stepUsesDefault.Retry.Should().BeNull();
	}

	#endregion
}
