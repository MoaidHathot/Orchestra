using System.Text.Json;
using FluentAssertions;

namespace Orchestra.Engine.Tests.Serialization;

public class ScriptStepParsingTests
{
	private static readonly StepParseContext s_context = new(BaseDirectory: null);

	[Fact]
	public void Parse_MinimalInlineScript_SetsDefaults()
	{
		// Arrange
		var json = JsonSerializer.Deserialize<JsonElement>("""
			{
				"name": "run-script",
				"type": "Script",
				"shell": "pwsh",
				"script": "Write-Output 'hello'"
			}
			""");
		var parser = new ScriptStepTypeParser();

		// Act
		var step = parser.Parse(json, s_context) as ScriptOrchestrationStep;

		// Assert
		step.Should().NotBeNull();
		step!.Name.Should().Be("run-script");
		step.Type.Should().Be(OrchestrationStepType.Script);
		step.Shell.Should().Be("pwsh");
		step.Script.Should().Be("Write-Output 'hello'");
		step.ScriptFile.Should().BeNull();
		step.Arguments.Should().BeEmpty();
		step.WorkingDirectory.Should().BeNull();
		step.Environment.Should().BeEmpty();
		step.IncludeStdErr.Should().BeFalse();
		step.DependsOn.Should().BeEmpty();
		step.TimeoutSeconds.Should().BeNull();
		step.Retry.Should().BeNull();
		step.Parameters.Should().BeEmpty();
		step.Stdin.Should().BeNull();
	}

	[Fact]
	public void Parse_FullInlineScript_AllPropertiesSet()
	{
		// Arrange
		var json = JsonSerializer.Deserialize<JsonElement>("""
			{
				"name": "full-script",
				"type": "Script",
				"shell": "bash",
				"script": "echo $GREETING && ls -la",
				"arguments": ["--verbose", "--output", "result.txt"],
				"workingDirectory": "/home/user/project",
				"environment": {
					"GREETING": "hello",
					"DEBUG": "true"
				},
				"includeStdErr": true,
				"stdin": "input-data",
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
		var parser = new ScriptStepTypeParser();

		// Act
		var step = parser.Parse(json, s_context) as ScriptOrchestrationStep;

		// Assert
		step.Should().NotBeNull();
		step!.Name.Should().Be("full-script");
		step.Type.Should().Be(OrchestrationStepType.Script);
		step.Shell.Should().Be("bash");
		step.Script.Should().Be("echo $GREETING && ls -la");
		step.ScriptFile.Should().BeNull();
		step.Arguments.Should().BeEquivalentTo(["--verbose", "--output", "result.txt"]);
		step.WorkingDirectory.Should().Be("/home/user/project");
		step.Environment.Should().HaveCount(2);
		step.Environment["GREETING"].Should().Be("hello");
		step.Environment["DEBUG"].Should().Be("true");
		step.IncludeStdErr.Should().BeTrue();
		step.Stdin.Should().Be("input-data");
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
	public void Parse_WithScriptFile_SetsScriptFilePath()
	{
		// Arrange
		var json = JsonSerializer.Deserialize<JsonElement>("""
			{
				"name": "file-script",
				"type": "Script",
				"shell": "pwsh",
				"scriptFile": "scripts/deploy.ps1"
			}
			""");
		var parser = new ScriptStepTypeParser();

		// Act — no base directory, so relative path is kept as-is
		var step = parser.Parse(json, s_context) as ScriptOrchestrationStep;

		// Assert
		step.Should().NotBeNull();
		step!.Script.Should().BeNull();
		step.ScriptFile.Should().Be("scripts/deploy.ps1");
	}

	[Fact]
	public void Parse_WithScriptFileAndBaseDirectory_ResolvesRelativePath()
	{
		// Arrange
		var json = JsonSerializer.Deserialize<JsonElement>("""
			{
				"name": "file-script",
				"type": "Script",
				"shell": "bash",
				"scriptFile": "scripts/run.sh"
			}
			""");
		var parser = new ScriptStepTypeParser();
		var contextWithDir = new StepParseContext(BaseDirectory: Path.GetTempPath());

		// Act
		var step = parser.Parse(json, contextWithDir) as ScriptOrchestrationStep;

		// Assert
		step.Should().NotBeNull();
		step!.ScriptFile.Should().NotBeNull();
		step.ScriptFile.Should().StartWith(Path.GetTempPath());
		step.ScriptFile.Should().EndWith("run.sh");
		Path.IsPathRooted(step.ScriptFile!).Should().BeTrue();
	}

	[Fact]
	public void Parse_MissingShell_ThrowsJsonException()
	{
		// Arrange
		var json = JsonSerializer.Deserialize<JsonElement>("""
			{
				"name": "no-shell",
				"type": "Script",
				"script": "echo hello"
			}
			""");
		var parser = new ScriptStepTypeParser();

		// Act
		var act = () => parser.Parse(json, s_context);

		// Assert
		act.Should().Throw<JsonException>().WithMessage("*shell*");
	}

	[Fact]
	public void Parse_MissingBothScriptAndScriptFile_ThrowsJsonException()
	{
		// Arrange
		var json = JsonSerializer.Deserialize<JsonElement>("""
			{
				"name": "no-script",
				"type": "Script",
				"shell": "pwsh"
			}
			""");
		var parser = new ScriptStepTypeParser();

		// Act
		var act = () => parser.Parse(json, s_context);

		// Assert
		act.Should().Throw<JsonException>().WithMessage("*script*scriptFile*");
	}

	[Fact]
	public void Parse_BothScriptAndScriptFile_ThrowsJsonException()
	{
		// Arrange
		var json = JsonSerializer.Deserialize<JsonElement>("""
			{
				"name": "both-script",
				"type": "Script",
				"shell": "pwsh",
				"script": "Write-Output 'hello'",
				"scriptFile": "scripts/deploy.ps1"
			}
			""");
		var parser = new ScriptStepTypeParser();

		// Act
		var act = () => parser.Parse(json, s_context);

		// Assert
		act.Should().Throw<JsonException>().WithMessage("*both*");
	}

	[Fact]
	public void Parse_WithTemplateExpressions_PreservesTemplates()
	{
		// Arrange
		var json = JsonSerializer.Deserialize<JsonElement>("""
			{
				"name": "template-script",
				"type": "Script",
				"shell": "pwsh",
				"script": "$path = '{{param.targetDir}}'; Get-ChildItem $path",
				"arguments": ["{{param.flag}}"],
				"workingDirectory": "{{vars.projectRoot}}",
				"environment": {
					"BUILD_CONFIG": "{{param.config}}"
				},
				"stdin": "{{prev-step.output}}",
				"parameters": ["targetDir", "flag", "config"],
				"dependsOn": ["prev-step"]
			}
			""");
		var parser = new ScriptStepTypeParser();

		// Act
		var step = parser.Parse(json, s_context) as ScriptOrchestrationStep;

		// Assert
		step.Should().NotBeNull();
		step!.Script.Should().Contain("{{param.targetDir}}");
		step.Arguments.Should().Contain("{{param.flag}}");
		step.WorkingDirectory.Should().Be("{{vars.projectRoot}}");
		step.Environment["BUILD_CONFIG"].Should().Be("{{param.config}}");
		step.Stdin.Should().Be("{{prev-step.output}}");
	}

	[Fact]
	public void ParseOrchestration_WithScriptStep_ParsesCorrectly()
	{
		// Arrange
		var json = """
			{
				"name": "script-orchestration",
				"description": "Orchestration with a Script step",
				"steps": [
					{
						"name": "run-ps",
						"type": "Script",
						"shell": "pwsh",
						"script": "$ErrorActionPreference = 'Stop'\nGet-ChildItem | Write-Output",
						"workingDirectory": "/src/project",
						"timeoutSeconds": 120,
						"includeStdErr": true
					}
				]
			}
			""";

		// Act
		var orchestration = OrchestrationParser.ParseOrchestrationMetadataOnly(json);

		// Assert
		orchestration.Name.Should().Be("script-orchestration");
		orchestration.Description.Should().Be("Orchestration with a Script step");
		orchestration.Steps.Should().HaveCount(1);

		var step = orchestration.Steps[0] as ScriptOrchestrationStep;
		step.Should().NotBeNull();
		step!.Name.Should().Be("run-ps");
		step.Type.Should().Be(OrchestrationStepType.Script);
		step.Shell.Should().Be("pwsh");
		step.Script.Should().Contain("Get-ChildItem");
		step.WorkingDirectory.Should().Be("/src/project");
		step.TimeoutSeconds.Should().Be(120);
		step.IncludeStdErr.Should().BeTrue();
	}

	[Fact]
	public void ParseOrchestration_ScriptAndCommandStepsTogether_ParsesBoth()
	{
		// Arrange
		var json = """
			{
				"name": "mixed-orchestration",
				"description": "Orchestration mixing Script and Command steps",
				"steps": [
					{
						"name": "run-script",
						"type": "Script",
						"shell": "pwsh",
						"script": "Get-Process | Select-Object -First 5 | ConvertTo-Json",
						"timeoutSeconds": 30
					},
					{
						"name": "run-cmd",
						"type": "Command",
						"dependsOn": ["run-script"],
						"command": "echo",
						"arguments": ["{{run-script.output}}"]
					}
				]
			}
			""";

		// Act
		var orchestration = OrchestrationParser.ParseOrchestrationMetadataOnly(json);

		// Assert
		orchestration.Steps.Should().HaveCount(2);
		orchestration.Steps[0].Should().BeOfType<ScriptOrchestrationStep>();
		orchestration.Steps[1].Should().BeOfType<CommandOrchestrationStep>();

		var scriptStep = (ScriptOrchestrationStep)orchestration.Steps[0];
		scriptStep.Shell.Should().Be("pwsh");

		var cmdStep = (CommandOrchestrationStep)orchestration.Steps[1];
		cmdStep.DependsOn.Should().Contain("run-script");
	}

	[Fact]
	public void ParseOrchestration_ScriptAndPromptStepsTogether_ParsesBoth()
	{
		// Arrange
		var json = """
			{
				"name": "script-prompt-orchestration",
				"description": "Script feeding into a Prompt step",
				"steps": [
					{
						"name": "gather-data",
						"type": "Script",
						"shell": "bash",
						"script": "df -h / | tail -1"
					},
					{
						"name": "analyze",
						"type": "Prompt",
						"dependsOn": ["gather-data"],
						"systemPrompt": "You analyze system data.",
						"userPrompt": "Analyze: {{gather-data.output}}",
						"model": "claude-opus-4.6",
						"mcps": []
					}
				]
			}
			""";

		// Act
		var orchestration = OrchestrationParser.ParseOrchestrationMetadataOnly(json);

		// Assert
		orchestration.Steps.Should().HaveCount(2);
		orchestration.Steps[0].Should().BeOfType<ScriptOrchestrationStep>();
		orchestration.Steps[1].Should().BeOfType<PromptOrchestrationStep>();
	}

	[Fact]
	public void Parse_StepTypeProperty_ReturnsScript()
	{
		var parser = new ScriptStepTypeParser();
		parser.TypeName.Should().Be("Script");
	}

	[Fact]
	public void Parse_DisabledStep_SetsEnabledFalse()
	{
		// Arrange
		var json = JsonSerializer.Deserialize<JsonElement>("""
			{
				"name": "disabled-script",
				"type": "Script",
				"shell": "pwsh",
				"script": "Write-Output 'skipped'",
				"enabled": false
			}
			""");
		var parser = new ScriptStepTypeParser();

		// Act
		var step = parser.Parse(json, s_context) as ScriptOrchestrationStep;

		// Assert
		step.Should().NotBeNull();
		step!.Enabled.Should().BeFalse();
	}

	[Fact]
	public void Parse_AbsoluteScriptFilePath_PreservesAbsolutePath()
	{
		// Arrange
		var absolutePath = OperatingSystem.IsWindows()
			? "C:\\scripts\\deploy.ps1"
			: "/opt/scripts/deploy.sh";

		// Build JSON manually to handle Windows backslashes (which need escaping in JSON)
		var jsonString = $$"""
			{
				"name": "abs-path-script",
				"type": "Script",
				"shell": "pwsh",
				"scriptFile": {{JsonSerializer.Serialize(absolutePath)}}
			}
			""";
		var json = JsonSerializer.Deserialize<JsonElement>(jsonString);
		var parser = new ScriptStepTypeParser();
		var contextWithDir = new StepParseContext(BaseDirectory: Path.GetTempPath());

		// Act
		var step = parser.Parse(json, contextWithDir) as ScriptOrchestrationStep;

		// Assert
		step.Should().NotBeNull();
		step!.ScriptFile.Should().Be(absolutePath);
	}
}
