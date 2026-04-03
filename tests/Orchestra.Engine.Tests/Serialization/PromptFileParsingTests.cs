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

	#region Variable Expansion in File Paths

	[Fact]
	public void ParseOrchestrationFile_SystemPromptFileWithVarsExpression_ResolvesPath()
	{
		// Arrange — systemPromptFile uses {{vars.promptsDir}} which should be expanded at parse time
		var promptsDir = Path.Combine(_tempDir, "prompts");
		Directory.CreateDirectory(promptsDir);
		File.WriteAllText(Path.Combine(promptsDir, "system.md"), "Resolved system prompt content");

		var orchestrationPath = CreateOrchestrationFile($$$"""
			{
				"name": "vars-file-test",
				"description": "Test",
				"variables": {
					"promptsDir": "{{{promptsDir.Replace("\\", "\\\\")}}}"
				},
				"steps": [
					{
						"name": "step1",
						"type": "prompt",
						"dependsOn": [],
						"systemPromptFile": "{{vars.promptsDir}}/system.md",
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
		step!.SystemPrompt.Should().Be("Resolved system prompt content");
	}

	[Fact]
	public void ParseOrchestrationFile_UserPromptFileWithVarsExpression_ResolvesPath()
	{
		// Arrange
		var promptsDir = Path.Combine(_tempDir, "prompts");
		Directory.CreateDirectory(promptsDir);
		File.WriteAllText(Path.Combine(promptsDir, "user.md"), "Resolved user prompt content");

		var orchestrationPath = CreateOrchestrationFile($$$"""
			{
				"name": "vars-user-file-test",
				"description": "Test",
				"variables": {
					"promptsDir": "{{{promptsDir.Replace("\\", "\\\\")}}}"
				},
				"steps": [
					{
						"name": "step1",
						"type": "prompt",
						"dependsOn": [],
						"systemPrompt": "System prompt",
						"userPromptFile": "{{vars.promptsDir}}/user.md",
						"model": "claude-opus-4.5"
					}
				]
			}
			""");

		// Act
		var orchestration = OrchestrationParser.ParseOrchestrationFile(orchestrationPath, []);

		// Assert
		var step = orchestration.Steps[0] as PromptOrchestrationStep;
		step!.UserPrompt.Should().Be("Resolved user prompt content");
	}

	[Fact]
	public void ParseOrchestrationFile_MultipleVarsInPath_ResolvesAll()
	{
		// Arrange — path uses multiple variables
		var baseDir = Path.Combine(_tempDir, "base");
		var subDir = Path.Combine(baseDir, "sub");
		Directory.CreateDirectory(subDir);
		File.WriteAllText(Path.Combine(subDir, "prompt.md"), "Multi-var resolved content");

		var orchestrationPath = CreateOrchestrationFile($$$"""
			{
				"name": "multi-vars-test",
				"description": "Test",
				"variables": {
					"baseDir": "{{{baseDir.Replace("\\", "\\\\")}}}",
					"subDir": "sub"
				},
				"steps": [
					{
						"name": "step1",
						"type": "prompt",
						"dependsOn": [],
						"systemPromptFile": "{{vars.baseDir}}/{{vars.subDir}}/prompt.md",
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
		step!.SystemPrompt.Should().Be("Multi-var resolved content");
	}

	[Fact]
	public void ParseOrchestrationFile_UndefinedVar_LeavesExpressionAndFailsFileNotFound()
	{
		// Arrange — vars.unknown is not defined, so the expression is left as-is
		var orchestrationPath = CreateOrchestrationFile("""
			{
				"name": "undefined-var-test",
				"description": "Test",
				"variables": {},
				"steps": [
					{
						"name": "step1",
						"type": "prompt",
						"dependsOn": [],
						"systemPromptFile": "{{vars.unknown}}/system.md",
						"userPrompt": "Hello",
						"model": "claude-opus-4.5"
					}
				]
			}
			""");

		// Act
		var act = () => OrchestrationParser.ParseOrchestrationFile(orchestrationPath, []);

		// Assert — the unresolved expression causes a file-not-found error
		act.Should().Throw<System.Text.Json.JsonException>()
			.WithMessage("*File not found*");
	}

	[Fact]
	public void ParseOrchestration_FromString_WithVars_ResolvesFilePathsRelativeToCwd()
	{
		// Arrange — when parsing from string, variables should still be extracted and expanded
		var promptsDir = Path.Combine(_tempDir, "string-prompts");
		Directory.CreateDirectory(promptsDir);
		File.WriteAllText(Path.Combine(promptsDir, "system.md"), "String-parsed prompt content");

		var json = $$$"""
			{
				"name": "string-vars-test",
				"description": "Test",
				"variables": {
					"promptsDir": "{{{promptsDir.Replace("\\", "\\\\")}}}"
				},
				"steps": [
					{
						"name": "step1",
						"type": "prompt",
						"dependsOn": [],
						"systemPromptFile": "{{vars.promptsDir}}/system.md",
						"userPrompt": "Hello",
						"model": "claude-opus-4.5"
					}
				]
			}
			""";

		// Act
		var orchestration = OrchestrationParser.ParseOrchestration(json, []);

		// Assert
		var step = orchestration.Steps[0] as PromptOrchestrationStep;
		step!.SystemPrompt.Should().Be("String-parsed prompt content");
	}

	[Fact]
	public void ParseOrchestrationFile_NoVariables_PromptFileStillWorks()
	{
		// Arrange — no variables block at all; regular file paths should still work
		CreateTempFile("system.md", "No-vars system prompt");
		var orchestrationPath = CreateOrchestrationFile("""
			{
				"name": "no-vars-test",
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
		step!.SystemPrompt.Should().Be("No-vars system prompt");
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

	#region MetadataOnly Parsing — Prompt File References Skipped

	[Fact]
	public void ParseOrchestrationFileMetadataOnly_SystemPromptFileWithMissingFile_Succeeds()
	{
		// Arrange — systemPromptFile references a nonexistent file (simulates template expression paths
		// like {{vars.promptsDir}}/file.md that can't be resolved during metadata scanning).
		var orchestrationPath = CreateOrchestrationFile("""
			{
				"name": "metadata-test",
				"description": "Test metadata parsing with missing prompt files",
				"steps": [
					{
						"name": "step1",
						"type": "prompt",
						"dependsOn": [],
						"systemPromptFile": "{{vars.promptsDir}}/nonexistent.md",
						"userPrompt": "Hello",
						"model": "claude-opus-4.5"
					}
				]
			}
			""");

		// Act — should NOT throw, because metadata-only skips file reads
		var orchestration = OrchestrationParser.ParseOrchestrationFileMetadataOnly(orchestrationPath);

		// Assert
		orchestration.Name.Should().Be("metadata-test");
		orchestration.Steps.Should().HaveCount(1);
		var step = orchestration.Steps[0] as PromptOrchestrationStep;
		step.Should().NotBeNull();
		step!.Name.Should().Be("step1");
		step.SystemPrompt.Should().BeEmpty("prompt file content is not loaded in metadata-only mode");
		step.UserPrompt.Should().Be("Hello");
	}

	[Fact]
	public void ParseOrchestrationFileMetadataOnly_AllPromptFilesWithMissingFiles_Succeeds()
	{
		// Arrange — all four prompt properties use file references to nonexistent files
		var orchestrationPath = CreateOrchestrationFile("""
			{
				"name": "all-files-metadata",
				"description": "Test",
				"steps": [
					{
						"name": "step1",
						"type": "prompt",
						"dependsOn": [],
						"systemPromptFile": "{{vars.dir}}/system.md",
						"userPromptFile": "{{vars.dir}}/user.md",
						"inputHandlerPromptFile": "{{vars.dir}}/input.md",
						"outputHandlerPromptFile": "{{vars.dir}}/output.md",
						"model": "claude-opus-4.5"
					}
				]
			}
			""");

		// Act
		var orchestration = OrchestrationParser.ParseOrchestrationFileMetadataOnly(orchestrationPath);

		// Assert
		var step = orchestration.Steps[0] as PromptOrchestrationStep;
		step!.SystemPrompt.Should().BeEmpty();
		step.UserPrompt.Should().BeEmpty();
		step.InputHandlerPrompt.Should().BeEmpty();
		step.OutputHandlerPrompt.Should().BeEmpty();
	}

	[Fact]
	public void ParseOrchestrationFileMetadataOnly_SubagentPromptFileWithMissingFile_Succeeds()
	{
		// Arrange — subagent promptFile references a nonexistent file
		var orchestrationPath = CreateOrchestrationFile("""
			{
				"name": "subagent-metadata",
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
								"promptFile": "{{vars.agentDir}}/researcher.md"
							}
						]
					}
				]
			}
			""");

		// Act
		var orchestration = OrchestrationParser.ParseOrchestrationFileMetadataOnly(orchestrationPath);

		// Assert
		var step = orchestration.Steps[0] as PromptOrchestrationStep;
		step!.Subagents.Should().HaveCount(1);
		step.Subagents[0].Name.Should().Be("researcher");
		step.Subagents[0].Prompt.Should().BeEmpty();
	}

	[Fact]
	public void ParseOrchestrationMetadataOnly_FromString_SkipsPromptFileReads()
	{
		// Arrange — parsing from raw JSON string with prompt file references
		var json = """
			{
				"name": "string-metadata-test",
				"description": "Test",
				"steps": [
					{
						"name": "step1",
						"type": "prompt",
						"dependsOn": [],
						"systemPromptFile": "nonexistent/system.md",
						"userPromptFile": "nonexistent/user.md",
						"model": "claude-opus-4.5"
					}
				]
			}
			""";

		// Act — should NOT throw
		var orchestration = OrchestrationParser.ParseOrchestrationMetadataOnly(json);

		// Assert
		orchestration.Name.Should().Be("string-metadata-test");
		var step = orchestration.Steps[0] as PromptOrchestrationStep;
		step!.SystemPrompt.Should().BeEmpty();
		step.UserPrompt.Should().BeEmpty();
	}

	[Fact]
	public void ParseOrchestrationFileMetadataOnly_WithExistingFiles_StillSkipsReads()
	{
		// Arrange — even when the files DO exist, metadata-only mode should not read them
		// (consistent behavior: prompts are always empty in metadata mode)
		CreateTempFile("system.md", "Real system prompt content");
		var orchestrationPath = CreateOrchestrationFile("""
			{
				"name": "existing-files-metadata",
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
		var orchestration = OrchestrationParser.ParseOrchestrationFileMetadataOnly(orchestrationPath);

		// Assert — prompt should be empty, NOT the file content
		var step = orchestration.Steps[0] as PromptOrchestrationStep;
		step!.SystemPrompt.Should().BeEmpty();
		step.UserPrompt.Should().Be("Hello", "inline prompts are still parsed normally");
	}

	#endregion
}
