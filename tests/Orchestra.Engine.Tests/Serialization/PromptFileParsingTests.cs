using FluentAssertions;

namespace Orchestra.Engine.Tests.Serialization;

public class PromptFileParsingTests : IDisposable
{
	private readonly string _tempDir;

	public PromptFileParsingTests()
	{
		_tempDir = Path.Combine(Path.GetTempPath(), "orchestra-tests-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_tempDir);
	}

	public void Dispose()
	{
		if (Directory.Exists(_tempDir))
			Directory.Delete(_tempDir, recursive: true);
	}

	private string CreateTempFile(string fileName, string content)
	{
		var path = Path.Combine(_tempDir, fileName);
		var dir = Path.GetDirectoryName(path)!;
		if (!Directory.Exists(dir))
			Directory.CreateDirectory(dir);
		File.WriteAllText(path, content);
		return path;
	}

	private string CreateOrchestrationFile(string json)
	{
		return CreateTempFile("orchestration.json", json);
	}

	#region SystemPromptFile

	[Fact]
	public void ParseOrchestration_SystemPromptFile_ReadsFileContent()
	{
		// Arrange
		CreateTempFile("system.md", "You are a helpful assistant from a file.");
		var orchestrationPath = CreateOrchestrationFile("""
			{
				"name": "file-test",
				"description": "Test",
				"steps": [
					{
						"name": "step1",
						"type": "prompt",
						"dependsOn": [],
						"systemPromptFile": "system.md",
						"userPrompt": "Hello",
						"model": "claude-opus-4.5"
					}
				]
			}
			""");

		// Act
		var orchestration = OrchestrationParser.ParseOrchestrationFile(orchestrationPath, []);

		// Assert
		var step = orchestration.Steps[0] as PromptOrchestrationStep;
		step.Should().NotBeNull();
		step!.SystemPrompt.Should().Be("You are a helpful assistant from a file.");
	}

	[Fact]
	public void ParseOrchestration_BothSystemPromptAndFile_ThrowsJsonException()
	{
		// Arrange
		CreateTempFile("system.md", "File content");
		var orchestrationPath = CreateOrchestrationFile("""
			{
				"name": "conflict-test",
				"description": "Test",
				"steps": [
					{
						"name": "step1",
						"type": "prompt",
						"dependsOn": [],
						"systemPrompt": "Inline content",
						"systemPromptFile": "system.md",
						"userPrompt": "Hello",
						"model": "claude-opus-4.5"
					}
				]
			}
			""");

		// Act
		var act = () => OrchestrationParser.ParseOrchestrationFile(orchestrationPath, []);

		// Assert
		act.Should().Throw<System.Text.Json.JsonException>()
			.WithMessage("*Cannot specify both*systemPrompt*systemPromptFile*");
	}

	[Fact]
	public void ParseOrchestration_SystemPromptFileMissing_ThrowsJsonException()
	{
		// Arrange
		var orchestrationPath = CreateOrchestrationFile("""
			{
				"name": "missing-file-test",
				"description": "Test",
				"steps": [
					{
						"name": "step1",
						"type": "prompt",
						"dependsOn": [],
						"systemPromptFile": "nonexistent.md",
						"userPrompt": "Hello",
						"model": "claude-opus-4.5"
					}
				]
			}
			""");

		// Act
		var act = () => OrchestrationParser.ParseOrchestrationFile(orchestrationPath, []);

		// Assert
		act.Should().Throw<System.Text.Json.JsonException>()
			.WithMessage("*File not found*systemPromptFile*nonexistent*");
	}

	#endregion

	#region UserPromptFile

	[Fact]
	public void ParseOrchestration_UserPromptFile_ReadsFileContent()
	{
		// Arrange
		CreateTempFile("user.md", "Analyze the following data: {{data}}");
		var orchestrationPath = CreateOrchestrationFile("""
			{
				"name": "user-file-test",
				"description": "Test",
				"steps": [
					{
						"name": "step1",
						"type": "prompt",
						"dependsOn": [],
						"systemPrompt": "You are a test assistant.",
						"userPromptFile": "user.md",
						"model": "claude-opus-4.5",
						"parameters": ["data"]
					}
				]
			}
			""");

		// Act
		var orchestration = OrchestrationParser.ParseOrchestrationFile(orchestrationPath, []);

		// Assert
		var step = orchestration.Steps[0] as PromptOrchestrationStep;
		step.Should().NotBeNull();
		step!.UserPrompt.Should().Be("Analyze the following data: {{data}}");
	}

	[Fact]
	public void ParseOrchestration_BothUserPromptAndFile_ThrowsJsonException()
	{
		// Arrange
		CreateTempFile("user.md", "File content");
		var orchestrationPath = CreateOrchestrationFile("""
			{
				"name": "conflict-test",
				"description": "Test",
				"steps": [
					{
						"name": "step1",
						"type": "prompt",
						"dependsOn": [],
						"systemPrompt": "System prompt",
						"userPrompt": "Inline user prompt",
						"userPromptFile": "user.md",
						"model": "claude-opus-4.5"
					}
				]
			}
			""");

		// Act
		var act = () => OrchestrationParser.ParseOrchestrationFile(orchestrationPath, []);

		// Assert
		act.Should().Throw<System.Text.Json.JsonException>()
			.WithMessage("*Cannot specify both*userPrompt*userPromptFile*");
	}

	#endregion

	#region Optional Prompt Files (InputHandler / OutputHandler)

	[Fact]
	public void ParseOrchestration_InputHandlerPromptFile_ReadsFileContent()
	{
		// Arrange
		CreateTempFile("input-handler.md", "Extract key fields from the input.");
		var orchestrationPath = CreateOrchestrationFile("""
			{
				"name": "handler-file-test",
				"description": "Test",
				"steps": [
					{
						"name": "step1",
						"type": "prompt",
						"dependsOn": [],
						"systemPrompt": "System prompt",
						"userPrompt": "User prompt",
						"inputHandlerPromptFile": "input-handler.md",
						"model": "claude-opus-4.5"
					}
				]
			}
			""");

		// Act
		var orchestration = OrchestrationParser.ParseOrchestrationFile(orchestrationPath, []);

		// Assert
		var step = orchestration.Steps[0] as PromptOrchestrationStep;
		step!.InputHandlerPrompt.Should().Be("Extract key fields from the input.");
	}

	[Fact]
	public void ParseOrchestration_OutputHandlerPromptFile_ReadsFileContent()
	{
		// Arrange
		CreateTempFile("output-handler.md", "Format as markdown report.");
		var orchestrationPath = CreateOrchestrationFile("""
			{
				"name": "handler-file-test",
				"description": "Test",
				"steps": [
					{
						"name": "step1",
						"type": "prompt",
						"dependsOn": [],
						"systemPrompt": "System prompt",
						"userPrompt": "User prompt",
						"outputHandlerPromptFile": "output-handler.md",
						"model": "claude-opus-4.5"
					}
				]
			}
			""");

		// Act
		var orchestration = OrchestrationParser.ParseOrchestrationFile(orchestrationPath, []);

		// Assert
		var step = orchestration.Steps[0] as PromptOrchestrationStep;
		step!.OutputHandlerPrompt.Should().Be("Format as markdown report.");
	}

	[Fact]
	public void ParseOrchestration_BothInputHandlerPromptAndFile_ThrowsJsonException()
	{
		// Arrange
		CreateTempFile("handler.md", "File content");
		var orchestrationPath = CreateOrchestrationFile("""
			{
				"name": "conflict-test",
				"description": "Test",
				"steps": [
					{
						"name": "step1",
						"type": "prompt",
						"dependsOn": [],
						"systemPrompt": "System prompt",
						"userPrompt": "User prompt",
						"inputHandlerPrompt": "Inline handler",
						"inputHandlerPromptFile": "handler.md",
						"model": "claude-opus-4.5"
					}
				]
			}
			""");

		// Act
		var act = () => OrchestrationParser.ParseOrchestrationFile(orchestrationPath, []);

		// Assert
		act.Should().Throw<System.Text.Json.JsonException>()
			.WithMessage("*Cannot specify both*inputHandlerPrompt*inputHandlerPromptFile*");
	}

	[Fact]
	public void ParseOrchestration_BothOutputHandlerPromptAndFile_ThrowsJsonException()
	{
		// Arrange
		CreateTempFile("handler.md", "File content");
		var orchestrationPath = CreateOrchestrationFile("""
			{
				"name": "conflict-test",
				"description": "Test",
				"steps": [
					{
						"name": "step1",
						"type": "prompt",
						"dependsOn": [],
						"systemPrompt": "System prompt",
						"userPrompt": "User prompt",
						"outputHandlerPrompt": "Inline handler",
						"outputHandlerPromptFile": "handler.md",
						"model": "claude-opus-4.5"
					}
				]
			}
			""");

		// Act
		var act = () => OrchestrationParser.ParseOrchestrationFile(orchestrationPath, []);

		// Assert
		act.Should().Throw<System.Text.Json.JsonException>()
			.WithMessage("*Cannot specify both*outputHandlerPrompt*outputHandlerPromptFile*");
	}

	#endregion

	#region Subagent PromptFile

	[Fact]
	public void ParseOrchestration_SubagentPromptFile_ReadsFileContent()
	{
		// Arrange
		CreateTempFile("researcher-prompt.md", "You are a research specialist.");
		var orchestrationPath = CreateOrchestrationFile("""
			{
				"name": "subagent-file-test",
				"description": "Test",
				"steps": [
					{
						"name": "coordinator",
						"type": "prompt",
						"dependsOn": [],
						"systemPrompt": "You are a coordinator.",
						"userPrompt": "Do the thing",
						"model": "claude-opus-4.5",
						"subagents": [
							{
								"name": "researcher",
								"promptFile": "researcher-prompt.md"
							}
						]
					}
				]
			}
			""");

		// Act
		var orchestration = OrchestrationParser.ParseOrchestrationFile(orchestrationPath, []);

		// Assert
		var step = orchestration.Steps[0] as PromptOrchestrationStep;
		step!.Subagents.Should().HaveCount(1);
		step.Subagents[0].Prompt.Should().Be("You are a research specialist.");
	}

	[Fact]
	public void ParseOrchestration_SubagentBothPromptAndFile_ThrowsJsonException()
	{
		// Arrange
		CreateTempFile("agent.md", "File content");
		var orchestrationPath = CreateOrchestrationFile("""
			{
				"name": "subagent-conflict-test",
				"description": "Test",
				"steps": [
					{
						"name": "coordinator",
						"type": "prompt",
						"dependsOn": [],
						"systemPrompt": "Coordinator prompt",
						"userPrompt": "Do the thing",
						"model": "claude-opus-4.5",
						"subagents": [
							{
								"name": "writer",
								"prompt": "Inline prompt",
								"promptFile": "agent.md"
							}
						]
					}
				]
			}
			""");

		// Act
		var act = () => OrchestrationParser.ParseOrchestrationFile(orchestrationPath, []);

		// Assert
		act.Should().Throw<System.Text.Json.JsonException>()
			.WithMessage("*Cannot specify both*prompt*promptFile*");
	}

	[Fact]
	public void ParseOrchestration_SubagentPromptFileMissing_ThrowsJsonException()
	{
		// Arrange
		var orchestrationPath = CreateOrchestrationFile("""
			{
				"name": "subagent-missing-file",
				"description": "Test",
				"steps": [
					{
						"name": "coordinator",
						"type": "prompt",
						"dependsOn": [],
						"systemPrompt": "Coordinator prompt",
						"userPrompt": "Do the thing",
						"model": "claude-opus-4.5",
						"subagents": [
							{
								"name": "broken",
								"promptFile": "missing.md"
							}
						]
					}
				]
			}
			""");

		// Act
		var act = () => OrchestrationParser.ParseOrchestrationFile(orchestrationPath, []);

		// Assert
		act.Should().Throw<System.Text.Json.JsonException>()
			.WithMessage("*File not found*promptFile*missing*");
	}

	#endregion

	#region Mixed Inline and File

	[Fact]
	public void ParseOrchestration_MixedInlineAndFile_SomeFromFileSomeInline()
	{
		// Arrange
		CreateTempFile("system.md", "System prompt from file");
		CreateTempFile("output-handler.md", "Output handler from file");
		var orchestrationPath = CreateOrchestrationFile("""
			{
				"name": "mixed-test",
				"description": "Test",
				"steps": [
					{
						"name": "step1",
						"type": "prompt",
						"dependsOn": [],
						"systemPromptFile": "system.md",
						"userPrompt": "Inline user prompt",
						"inputHandlerPrompt": "Inline input handler",
						"outputHandlerPromptFile": "output-handler.md",
						"model": "claude-opus-4.5"
					}
				]
			}
			""");

		// Act
		var orchestration = OrchestrationParser.ParseOrchestrationFile(orchestrationPath, []);

		// Assert
		var step = orchestration.Steps[0] as PromptOrchestrationStep;
		step!.SystemPrompt.Should().Be("System prompt from file");
		step.UserPrompt.Should().Be("Inline user prompt");
		step.InputHandlerPrompt.Should().Be("Inline input handler");
		step.OutputHandlerPrompt.Should().Be("Output handler from file");
	}

	#endregion

	#region Relative Path Resolution

	[Fact]
	public void ParseOrchestration_RelativePathFromOrchestrationDir_ResolvesCorrectly()
	{
		// Arrange — put prompt in a subdirectory relative to orchestration file
		CreateTempFile("prompts/system.md", "System from subdirectory");
		var orchestrationPath = CreateOrchestrationFile("""
			{
				"name": "relative-path-test",
				"description": "Test",
				"steps": [
					{
						"name": "step1",
						"type": "prompt",
						"dependsOn": [],
						"systemPromptFile": "prompts/system.md",
						"userPrompt": "Hello",
						"model": "claude-opus-4.5"
					}
				]
			}
			""");

		// Act
		var orchestration = OrchestrationParser.ParseOrchestrationFile(orchestrationPath, []);

		// Assert
		var step = orchestration.Steps[0] as PromptOrchestrationStep;
		step!.SystemPrompt.Should().Be("System from subdirectory");
	}

	[Fact]
	public void ParseOrchestration_AbsolutePath_ResolvesCorrectly()
	{
		// Arrange — use absolute path
		var absolutePromptPath = CreateTempFile("absolute-system.md", "System from absolute path");
		var orchestrationPath = CreateOrchestrationFile($$"""
			{
				"name": "absolute-path-test",
				"description": "Test",
				"steps": [
					{
						"name": "step1",
						"type": "prompt",
						"dependsOn": [],
						"systemPromptFile": "{{absolutePromptPath.Replace("\\", "\\\\")}}",
						"userPrompt": "Hello",
						"model": "claude-opus-4.5"
					}
				]
			}
			""");

		// Act
		var orchestration = OrchestrationParser.ParseOrchestrationFile(orchestrationPath, []);

		// Assert
		var step = orchestration.Steps[0] as PromptOrchestrationStep;
		step!.SystemPrompt.Should().Be("System from absolute path");
	}

	#endregion

	#region All Files

	[Fact]
	public void ParseOrchestration_AllPromptsFromFiles_ReadsAllCorrectly()
	{
		// Arrange
		CreateTempFile("system.md", "System prompt content");
		CreateTempFile("user.md", "User prompt content");
		CreateTempFile("input-handler.md", "Input handler content");
		CreateTempFile("output-handler.md", "Output handler content");
		var orchestrationPath = CreateOrchestrationFile("""
			{
				"name": "all-files-test",
				"description": "Test",
				"steps": [
					{
						"name": "step1",
						"type": "prompt",
						"dependsOn": [],
						"systemPromptFile": "system.md",
						"userPromptFile": "user.md",
						"inputHandlerPromptFile": "input-handler.md",
						"outputHandlerPromptFile": "output-handler.md",
						"model": "claude-opus-4.5"
					}
				]
			}
			""");

		// Act
		var orchestration = OrchestrationParser.ParseOrchestrationFile(orchestrationPath, []);

		// Assert
		var step = orchestration.Steps[0] as PromptOrchestrationStep;
		step!.SystemPrompt.Should().Be("System prompt content");
		step.UserPrompt.Should().Be("User prompt content");
		step.InputHandlerPrompt.Should().Be("Input handler content");
		step.OutputHandlerPrompt.Should().Be("Output handler content");
	}

	#endregion

	#region Multiline File Content

	[Fact]
	public void ParseOrchestration_MultilineFileContent_PreservesFormatting()
	{
		// Arrange
		var multilineContent = "You are a helpful assistant.\n\nRules:\n- Be concise\n- Be accurate\n- Cite sources";
		CreateTempFile("system.md", multilineContent);
		var orchestrationPath = CreateOrchestrationFile("""
			{
				"name": "multiline-test",
				"description": "Test",
				"steps": [
					{
						"name": "step1",
						"type": "prompt",
						"dependsOn": [],
						"systemPromptFile": "system.md",
						"userPrompt": "Hello",
						"model": "claude-opus-4.5"
					}
				]
			}
			""");

		// Act
		var orchestration = OrchestrationParser.ParseOrchestrationFile(orchestrationPath, []);

		// Assert
		var step = orchestration.Steps[0] as PromptOrchestrationStep;
		step!.SystemPrompt.Should().Be(multilineContent);
	}

	#endregion

	#region No Handler Prompts Specified

	[Fact]
	public void ParseOrchestration_NoHandlerPrompts_ReturnsNull()
	{
		// Arrange — neither inline nor file specified for optional fields
		var orchestrationPath = CreateOrchestrationFile("""
			{
				"name": "no-handlers-test",
				"description": "Test",
				"steps": [
					{
						"name": "step1",
						"type": "prompt",
						"dependsOn": [],
						"systemPrompt": "System prompt",
						"userPrompt": "User prompt",
						"model": "claude-opus-4.5"
					}
				]
			}
			""");

		// Act
		var orchestration = OrchestrationParser.ParseOrchestrationFile(orchestrationPath, []);

		// Assert
		var step = orchestration.Steps[0] as PromptOrchestrationStep;
		step!.InputHandlerPrompt.Should().BeNull();
		step.OutputHandlerPrompt.Should().BeNull();
	}

	#endregion

	#region ParseOrchestration from String (no file path)

	[Fact]
	public void ParseOrchestration_FromString_InlinePromptsStillWork()
	{
		// Arrange — parsing from raw string should still work with inline prompts
		var json = """
			{
				"name": "string-test",
				"description": "Test",
				"steps": [
					{
						"name": "step1",
						"type": "prompt",
						"dependsOn": [],
						"systemPrompt": "Inline system prompt",
						"userPrompt": "Inline user prompt",
						"model": "claude-opus-4.5"
					}
				]
			}
			""";

		// Act
		var orchestration = OrchestrationParser.ParseOrchestration(json, []);

		// Assert
		var step = orchestration.Steps[0] as PromptOrchestrationStep;
		step!.SystemPrompt.Should().Be("Inline system prompt");
		step.UserPrompt.Should().Be("Inline user prompt");
	}

	#endregion
}
