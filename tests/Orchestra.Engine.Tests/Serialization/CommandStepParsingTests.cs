using System.Text.Json;
using FluentAssertions;

namespace Orchestra.Engine.Tests.Serialization;

public class CommandStepParsingTests
{
	private static readonly StepParseContext s_context = new(BaseDirectory: null);
	[Fact]
	public void Parse_MinimalCommandStep_SetsDefaults()
	{
		// Arrange
		var json = JsonSerializer.Deserialize<JsonElement>("""
			{
				"name": "run-cmd",
				"type": "Command",
				"command": "dotnet"
			}
			""");
		var parser = new CommandStepTypeParser();

		// Act
		var step = parser.Parse(json, s_context) as CommandOrchestrationStep;

		// Assert
		step.Should().NotBeNull();
		step!.Name.Should().Be("run-cmd");
		step.Type.Should().Be(OrchestrationStepType.Command);
		step.Command.Should().Be("dotnet");
		step.Arguments.Should().BeEmpty();
		step.WorkingDirectory.Should().BeNull();
		step.Environment.Should().BeEmpty();
		step.IncludeStdErr.Should().BeFalse();
		step.DependsOn.Should().BeEmpty();
		step.TimeoutSeconds.Should().BeNull();
		step.Retry.Should().BeNull();
		step.Parameters.Should().BeEmpty();
	}

	[Fact]
	public void Parse_FullCommandStep_AllPropertiesSet()
	{
		// Arrange
		var json = JsonSerializer.Deserialize<JsonElement>("""
			{
				"name": "full-cmd",
				"type": "Command",
				"command": "python",
				"arguments": ["script.py", "--verbose", "--output", "result.json"],
				"workingDirectory": "/home/user/project",
				"environment": {
					"PYTHONPATH": "/custom/path",
					"DEBUG": "true"
				},
				"includeStdErr": true,
				"dependsOn": ["step1", "step2"],
				"timeoutSeconds": 60,
				"retry": {
					"maxRetries": 3,
					"backoffSeconds": 2.0,
					"backoffMultiplier": 1.5,
					"retryOnTimeout": true
				},
				"parameters": ["inputFile", "outputDir"]
			}
			""");
		var parser = new CommandStepTypeParser();

		// Act
		var step = parser.Parse(json, s_context) as CommandOrchestrationStep;

		// Assert
		step.Should().NotBeNull();
		step!.Name.Should().Be("full-cmd");
		step.Type.Should().Be(OrchestrationStepType.Command);
		step.Command.Should().Be("python");
		step.Arguments.Should().BeEquivalentTo(["script.py", "--verbose", "--output", "result.json"]);
		step.WorkingDirectory.Should().Be("/home/user/project");
		step.Environment.Should().HaveCount(2);
		step.Environment["PYTHONPATH"].Should().Be("/custom/path");
		step.Environment["DEBUG"].Should().Be("true");
		step.IncludeStdErr.Should().BeTrue();
		step.DependsOn.Should().BeEquivalentTo(["step1", "step2"]);
		step.TimeoutSeconds.Should().Be(60);
		step.Retry.Should().NotBeNull();
		step.Retry!.MaxRetries.Should().Be(3);
		step.Retry.BackoffSeconds.Should().Be(2.0);
		step.Retry.BackoffMultiplier.Should().Be(1.5);
		step.Retry.RetryOnTimeout.Should().BeTrue();
		step.Parameters.Should().BeEquivalentTo(["inputFile", "outputDir"]);
	}

	[Fact]
	public void Parse_WithArguments_DeserializesArguments()
	{
		// Arrange
		var json = JsonSerializer.Deserialize<JsonElement>("""
			{
				"name": "args-cmd",
				"type": "Command",
				"command": "git",
				"arguments": ["log", "--oneline", "-5"]
			}
			""");
		var parser = new CommandStepTypeParser();

		// Act
		var step = parser.Parse(json, s_context) as CommandOrchestrationStep;

		// Assert
		step.Should().NotBeNull();
		step!.Command.Should().Be("git");
		step.Arguments.Should().BeEquivalentTo(["log", "--oneline", "-5"]);
	}

	[Fact]
	public void Parse_WithEnvironment_DeserializesEnvironmentVariables()
	{
		// Arrange
		var json = JsonSerializer.Deserialize<JsonElement>("""
			{
				"name": "env-cmd",
				"type": "Command",
				"command": "node",
				"arguments": ["app.js"],
				"environment": {
					"NODE_ENV": "production",
					"PORT": "{{param.port}}",
					"API_KEY": "{{param.apiKey}}"
				}
			}
			""");
		var parser = new CommandStepTypeParser();

		// Act
		var step = parser.Parse(json, s_context) as CommandOrchestrationStep;

		// Assert
		step.Should().NotBeNull();
		step!.Environment.Should().HaveCount(3);
		step.Environment["NODE_ENV"].Should().Be("production");
		step.Environment["PORT"].Should().Be("{{param.port}}");
		step.Environment["API_KEY"].Should().Be("{{param.apiKey}}");
	}

	[Fact]
	public void Parse_WithRetry_DeserializesRetryPolicy()
	{
		// Arrange
		var json = JsonSerializer.Deserialize<JsonElement>("""
			{
				"name": "retry-cmd",
				"type": "Command",
				"command": "curl",
				"arguments": ["https://api.example.com/health"],
				"retry": {
					"maxRetries": 5,
					"backoffSeconds": 1.0,
					"backoffMultiplier": 2.0,
					"retryOnTimeout": false
				}
			}
			""");
		var parser = new CommandStepTypeParser();

		// Act
		var step = parser.Parse(json, s_context) as CommandOrchestrationStep;

		// Assert
		step.Should().NotBeNull();
		step!.Retry.Should().NotBeNull();
		step.Retry!.MaxRetries.Should().Be(5);
		step.Retry.BackoffSeconds.Should().Be(1.0);
		step.Retry.BackoffMultiplier.Should().Be(2.0);
		step.Retry.RetryOnTimeout.Should().BeFalse();
	}

	[Fact]
	public void ParseOrchestration_WithCommandStep_ParsesCorrectly()
	{
		// Arrange
		var json = """
			{
				"name": "command-orchestration",
				"description": "Orchestration with a Command step",
				"steps": [
					{
						"name": "run-build",
						"type": "Command",
						"command": "dotnet",
						"arguments": ["build", "--no-restore"],
						"workingDirectory": "/src/project",
						"dependsOn": [],
						"timeoutSeconds": 120,
						"includeStdErr": true
					}
				]
			}
			""";

		// Act
		var orchestration = OrchestrationParser.ParseOrchestrationMetadataOnly(json);

		// Assert
		orchestration.Name.Should().Be("command-orchestration");
		orchestration.Description.Should().Be("Orchestration with a Command step");
		orchestration.Steps.Should().HaveCount(1);

		var step = orchestration.Steps[0] as CommandOrchestrationStep;
		step.Should().NotBeNull();
		step!.Name.Should().Be("run-build");
		step.Type.Should().Be(OrchestrationStepType.Command);
		step.Command.Should().Be("dotnet");
		step.Arguments.Should().BeEquivalentTo(["build", "--no-restore"]);
		step.WorkingDirectory.Should().Be("/src/project");
		step.DependsOn.Should().BeEmpty();
		step.TimeoutSeconds.Should().Be(120);
		step.IncludeStdErr.Should().BeTrue();
	}

	[Fact]
	public void ParseOrchestration_CommandAndPromptStepsTogether_ParsesBoth()
	{
		// Arrange
		var json = """
			{
				"name": "mixed-orchestration",
				"description": "Orchestration mixing Command and Prompt steps",
				"steps": [
					{
						"name": "run-tests",
						"type": "Command",
						"command": "dotnet",
						"arguments": ["test", "--verbosity", "minimal"],
						"dependsOn": [],
						"timeoutSeconds": 300
					},
					{
						"name": "analyze-results",
						"type": "Prompt",
						"dependsOn": ["run-tests"],
						"systemPrompt": "You analyze test results.",
						"userPrompt": "Analyze: {{run-tests.output}}",
						"model": "claude-opus-4-5-20250514",
						"mcps": []
					}
				]
			}
			""";

		// Act
		var orchestration = OrchestrationParser.ParseOrchestrationMetadataOnly(json);

		// Assert
		orchestration.Steps.Should().HaveCount(2);
		orchestration.Steps[0].Should().BeOfType<CommandOrchestrationStep>();
		orchestration.Steps[1].Should().BeOfType<PromptOrchestrationStep>();

		var cmdStep = (CommandOrchestrationStep)orchestration.Steps[0];
		cmdStep.Command.Should().Be("dotnet");

		var promptStep = (PromptOrchestrationStep)orchestration.Steps[1];
		promptStep.DependsOn.Should().Contain("run-tests");
	}
}
