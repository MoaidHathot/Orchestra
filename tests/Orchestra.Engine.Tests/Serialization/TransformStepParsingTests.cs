using System.Text.Json;
using FluentAssertions;

namespace Orchestra.Engine.Tests.Serialization;

public class TransformStepParsingTests
{
	private static readonly StepParseContext s_context = new(BaseDirectory: null);
	[Fact]
	public void Parse_MinimalTransformStep_SetsDefaults()
	{
		// Arrange
		var json = JsonSerializer.Deserialize<JsonElement>("""
			{
				"name": "format-output",
				"type": "Transform",
				"template": "Result: {{step1.output}}"
			}
			""");
		var parser = new TransformStepTypeParser();

		// Act
		var step = parser.Parse(json, s_context) as TransformOrchestrationStep;

		// Assert
		step.Should().NotBeNull();
		step!.Name.Should().Be("format-output");
		step.Type.Should().Be(OrchestrationStepType.Transform);
		step.Template.Should().Be("Result: {{step1.output}}");
		step.ContentType.Should().Be("text/plain");
		step.DependsOn.Should().BeEmpty();
		step.TimeoutSeconds.Should().BeNull();
		step.Retry.Should().BeNull();
		step.Parameters.Should().BeEmpty();
	}

	[Fact]
	public void Parse_FullTransformStep_AllPropertiesSet()
	{
		// Arrange
		var json = JsonSerializer.Deserialize<JsonElement>("""
			{
				"name": "full-transform",
				"type": "Transform",
				"template": "Hello {{param.name}}, your result is: {{step1.output}}",
				"contentType": "application/json",
				"dependsOn": ["step1", "step2"],
				"timeoutSeconds": 10,
				"retry": {
					"maxRetries": 2,
					"backoffSeconds": 0.5,
					"backoffMultiplier": 1.5,
					"retryOnTimeout": false
				},
				"parameters": ["name", "format"]
			}
			""");
		var parser = new TransformStepTypeParser();

		// Act
		var step = parser.Parse(json, s_context) as TransformOrchestrationStep;

		// Assert
		step.Should().NotBeNull();
		step!.Name.Should().Be("full-transform");
		step.Type.Should().Be(OrchestrationStepType.Transform);
		step.Template.Should().Be("Hello {{param.name}}, your result is: {{step1.output}}");
		step.ContentType.Should().Be("application/json");
		step.DependsOn.Should().BeEquivalentTo(["step1", "step2"]);
		step.TimeoutSeconds.Should().Be(10);
		step.Retry.Should().NotBeNull();
		step.Retry!.MaxRetries.Should().Be(2);
		step.Retry.BackoffSeconds.Should().Be(0.5);
		step.Retry.BackoffMultiplier.Should().Be(1.5);
		step.Retry.RetryOnTimeout.Should().BeFalse();
		step.Parameters.Should().BeEquivalentTo(["name", "format"]);
	}

	[Fact]
	public void Parse_WithDependsOn_ParsesDependencies()
	{
		// Arrange
		var json = JsonSerializer.Deserialize<JsonElement>("""
			{
				"name": "dependent-transform",
				"type": "Transform",
				"template": "Combined: {{analyze.output}} + {{summarize.output}}",
				"dependsOn": ["analyze", "summarize", "validate"]
			}
			""");
		var parser = new TransformStepTypeParser();

		// Act
		var step = parser.Parse(json, s_context) as TransformOrchestrationStep;

		// Assert
		step.Should().NotBeNull();
		step!.DependsOn.Should().HaveCount(3);
		step.DependsOn.Should().BeEquivalentTo(["analyze", "summarize", "validate"]);
	}

	[Fact]
	public void Parse_WithParameters_ParsesParameters()
	{
		// Arrange
		var json = JsonSerializer.Deserialize<JsonElement>("""
			{
				"name": "parameterized-transform",
				"type": "Transform",
				"template": "Dear {{param.recipientName}}, your order #{{param.orderId}} is ready.",
				"parameters": ["recipientName", "orderId"]
			}
			""");
		var parser = new TransformStepTypeParser();

		// Act
		var step = parser.Parse(json, s_context) as TransformOrchestrationStep;

		// Assert
		step.Should().NotBeNull();
		step!.Parameters.Should().HaveCount(2);
		step.Parameters.Should().BeEquivalentTo(["recipientName", "orderId"]);
	}

	[Fact]
	public void ParseOrchestration_WithTransformStep_ParsesCorrectly()
	{
		// Arrange
		var json = """
			{
				"name": "transform-orchestration",
				"description": "Orchestration with a Transform step",
				"steps": [
					{
						"name": "prompt-step",
						"type": "Prompt",
						"dependsOn": [],
						"systemPrompt": "You are a helpful assistant.",
						"userPrompt": "Summarize the input.",
						"model": "claude-opus-4.5"
					},
					{
						"name": "format-result",
						"type": "Transform",
						"dependsOn": ["prompt-step"],
						"template": "## Summary\n{{prompt-step.output}}\n\nGenerated at: {{param.timestamp}}",
						"contentType": "text/markdown",
						"parameters": ["timestamp"]
					}
				]
			}
			""";

		// Act
		var orchestration = OrchestrationParser.ParseOrchestrationMetadataOnly(json);

		// Assert
		orchestration.Name.Should().Be("transform-orchestration");
		orchestration.Steps.Should().HaveCount(2);

		var promptStep = orchestration.Steps[0] as PromptOrchestrationStep;
		promptStep.Should().NotBeNull();
		promptStep!.Name.Should().Be("prompt-step");

		var transformStep = orchestration.Steps[1] as TransformOrchestrationStep;
		transformStep.Should().NotBeNull();
		transformStep!.Name.Should().Be("format-result");
		transformStep.Type.Should().Be(OrchestrationStepType.Transform);
		transformStep.DependsOn.Should().BeEquivalentTo(["prompt-step"]);
		transformStep.Template.Should().Contain("{{prompt-step.output}}");
		transformStep.ContentType.Should().Be("text/markdown");
		transformStep.Parameters.Should().BeEquivalentTo(["timestamp"]);
	}
}
