using FluentAssertions;

namespace Orchestra.Engine.Tests.Serialization;

public class OrchestrationParserTests
{
	#region Basic Orchestration Parsing

	[Fact]
	public void ParseOrchestration_ValidJson_ReturnsOrchestration()
	{
		// Arrange
		var json = """
			{
				"name": "test-orchestration",
				"description": "Test description",
				"steps": [
					{
						"name": "step1",
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
		orchestration.Name.Should().Be("test-orchestration");
		orchestration.Description.Should().Be("Test description");
		orchestration.Steps.Should().HaveCount(1);
		orchestration.Steps[0].Name.Should().Be("step1");
	}

	[Fact]
	public void ParseOrchestration_WithVersion_ParsesVersion()
	{
		// Arrange
		var json = """
			{
				"name": "versioned",
				"description": "Test",
				"version": "2.0.0",
				"steps": []
			}
			""";

		// Act
		var orchestration = OrchestrationParser.ParseOrchestration(json, []);

		// Assert
		orchestration.Version.Should().Be("2.0.0");
	}

	[Fact]
	public void ParseOrchestration_WithoutVersion_DefaultsTo100()
	{
		// Arrange
		var json = """
			{
				"name": "no-version",
				"description": "Test",
				"steps": []
			}
			""";

		// Act
		var orchestration = OrchestrationParser.ParseOrchestration(json, []);

		// Assert
		orchestration.Version.Should().Be("1.0.0");
	}

	[Fact]
	public void ParseOrchestration_WithDefaultSystemPromptMode_ParsesMode()
	{
		// Arrange
		var json = """
			{
				"name": "with-default-mode",
				"description": "Test",
				"defaultSystemPromptMode": "replace",
				"steps": []
			}
			""";

		// Act
		var orchestration = OrchestrationParser.ParseOrchestration(json, []);

		// Assert
		orchestration.DefaultSystemPromptMode.Should().Be(SystemPromptMode.Replace);
	}

	[Fact]
	public void ParseOrchestration_WithDefaultSystemPromptModeAppend_ParsesMode()
	{
		// Arrange
		var json = """
			{
				"name": "with-append-mode",
				"description": "Test",
				"defaultSystemPromptMode": "append",
				"steps": []
			}
			""";

		// Act
		var orchestration = OrchestrationParser.ParseOrchestration(json, []);

		// Assert
		orchestration.DefaultSystemPromptMode.Should().Be(SystemPromptMode.Append);
	}

	[Fact]
	public void ParseOrchestration_WithoutDefaultSystemPromptMode_DefaultsToNull()
	{
		// Arrange
		var json = """
			{
				"name": "no-default-mode",
				"description": "Test",
				"steps": []
			}
			""";

		// Act
		var orchestration = OrchestrationParser.ParseOrchestration(json, []);

		// Assert
		orchestration.DefaultSystemPromptMode.Should().BeNull();
	}

	#endregion

	#region Step Parsing

	[Fact]
	public void ParseOrchestration_PromptStep_ParsesAllFields()
	{
		// Arrange
		var json = """
			{
				"name": "test",
				"description": "Test",
				"steps": [
					{
						"name": "full-step",
						"type": "prompt",
						"dependsOn": ["step1", "step2"],
						"systemPrompt": "System prompt here",
						"userPrompt": "User prompt with {{param1}}",
						"model": "gpt-4",
						"parameters": ["param1", "param2"],
						"inputHandlerPrompt": "Input handler",
						"outputHandlerPrompt": "Output handler",
						"reasoningLevel": "high",
						"systemPromptMode": "replace"
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
		step.DependsOn.Should().BeEquivalentTo(["step1", "step2"]);
		step.SystemPrompt.Should().Be("System prompt here");
		step.UserPrompt.Should().Be("User prompt with {{param1}}");
		step.Model.Should().Be("gpt-4");
		step.Parameters.Should().BeEquivalentTo(["param1", "param2"]);
		step.InputHandlerPrompt.Should().Be("Input handler");
		step.OutputHandlerPrompt.Should().Be("Output handler");
		step.ReasoningLevel.Should().Be(ReasoningLevel.High);
		step.SystemPromptMode.Should().Be(SystemPromptMode.Replace);
	}

	[Fact]
	public void ParseOrchestration_StepWithLoop_ParsesLoopConfig()
	{
		// Arrange
		var json = """
			{
				"name": "looping",
				"description": "Test",
				"steps": [
					{
						"name": "checker",
						"type": "prompt",
						"dependsOn": [],
						"systemPrompt": "Check result",
						"userPrompt": "Check",
						"model": "claude-opus-4.5",
						"loop": {
							"target": "generator",
							"maxIterations": 5,
							"exitPattern": "APPROVED"
						}
					}
				]
			}
			""";

		// Act
		var orchestration = OrchestrationParser.ParseOrchestration(json, []);

		// Assert
		var step = orchestration.Steps[0] as PromptOrchestrationStep;
		step!.Loop.Should().NotBeNull();
		step.Loop!.Target.Should().Be("generator");
		step.Loop.MaxIterations.Should().Be(5);
		step.Loop.ExitPattern.Should().Be("APPROVED");
	}

	[Fact]
	public void ParseOrchestration_MultipleSteps_ParsesAll()
	{
		// Arrange
		var json = """
			{
				"name": "multi",
				"description": "Test",
				"steps": [
					{
						"name": "A",
						"type": "prompt",
						"dependsOn": [],
						"systemPrompt": "S1",
						"userPrompt": "U1",
						"model": "model1"
					},
					{
						"name": "B",
						"type": "prompt",
						"dependsOn": ["A"],
						"systemPrompt": "S2",
						"userPrompt": "U2",
						"model": "model2"
					}
				]
			}
			""";

		// Act
		var orchestration = OrchestrationParser.ParseOrchestration(json, []);

		// Assert
		orchestration.Steps.Should().HaveCount(2);
		orchestration.Steps[0].Name.Should().Be("A");
		orchestration.Steps[1].Name.Should().Be("B");
		orchestration.Steps[1].DependsOn.Should().Contain("A");
	}

	#endregion

	#region MCP Resolution

	[Fact]
	public void ParseOrchestration_WithExternalMcps_ResolvesMcpReferences()
	{
		// Arrange
		var json = """
			{
				"name": "mcp-test",
				"description": "Test",
				"steps": [
					{
						"name": "step1",
						"type": "prompt",
						"dependsOn": [],
						"systemPrompt": "Test",
						"userPrompt": "Test",
						"model": "claude-opus-4.5",
						"mcps": ["filesystem"]
					}
				]
			}
			""";

		var externalMcps = new Mcp[]
		{
			new LocalMcp
			{
				Name = "filesystem",
				Type = McpType.Local,
				Command = "npx",
				Arguments = ["-y", "@anthropic/mcp-filesystem"]
			}
		};

		// Act
		var orchestration = OrchestrationParser.ParseOrchestration(json, externalMcps);

		// Assert
		var step = orchestration.Steps[0] as PromptOrchestrationStep;
		step!.Mcps.Should().HaveCount(1);
		step.Mcps[0].Name.Should().Be("filesystem");
		step.Mcps[0].Should().BeOfType<LocalMcp>();
	}

	[Fact]
	public void ParseOrchestration_WithInlineMcps_ResolvesMcpReferences()
	{
		// Arrange
		var json = """
			{
				"name": "inline-mcp-test",
				"description": "Test",
				"mcps": [
					{
						"name": "inline-tool",
						"type": "local",
						"command": "node",
						"arguments": ["tool.js"]
					}
				],
				"steps": [
					{
						"name": "step1",
						"type": "prompt",
						"dependsOn": [],
						"systemPrompt": "Test",
						"userPrompt": "Test",
						"model": "claude-opus-4.5",
						"mcps": ["inline-tool"]
					}
				]
			}
			""";

		// Act
		var orchestration = OrchestrationParser.ParseOrchestration(json, []);

		// Assert
		var step = orchestration.Steps[0] as PromptOrchestrationStep;
		step!.Mcps.Should().HaveCount(1);
		step.Mcps[0].Name.Should().Be("inline-tool");
	}

	[Fact]
	public void ParseOrchestration_ExternalMcpsOverrideInline()
	{
		// Arrange
		var json = """
			{
				"name": "override-test",
				"description": "Test",
				"mcps": [
					{
						"name": "tool",
						"type": "local",
						"command": "inline-command"
					}
				],
				"steps": [
					{
						"name": "step1",
						"type": "prompt",
						"dependsOn": [],
						"systemPrompt": "Test",
						"userPrompt": "Test",
						"model": "claude-opus-4.5",
						"mcps": ["tool"]
					}
				]
			}
			""";

		var externalMcps = new Mcp[]
		{
			new LocalMcp
			{
				Name = "tool",
				Type = McpType.Local,
				Command = "external-command",
				Arguments = []
			}
		};

		// Act
		var orchestration = OrchestrationParser.ParseOrchestration(json, externalMcps);

		// Assert
		var step = orchestration.Steps[0] as PromptOrchestrationStep;
		var localMcp = step!.Mcps[0] as LocalMcp;
		localMcp!.Command.Should().Be("external-command");
	}

	[Fact]
	public void ParseOrchestration_MissingMcp_ThrowsInvalidOperationException()
	{
		// Arrange
		var json = """
			{
				"name": "missing-mcp",
				"description": "Test",
				"steps": [
					{
						"name": "step1",
						"type": "prompt",
						"dependsOn": [],
						"systemPrompt": "Test",
						"userPrompt": "Test",
						"model": "claude-opus-4.5",
						"mcps": ["nonexistent"]
					}
				]
			}
			""";

		// Act
		var act = () => OrchestrationParser.ParseOrchestration(json, []);

		// Assert
		act.Should().Throw<InvalidOperationException>()
			.WithMessage("*'nonexistent'*not defined*");
	}

	#endregion

	#region Trigger Parsing

	[Fact]
	public void ParseOrchestration_SchedulerTrigger_ParsesCorrectly()
	{
		// Arrange
		var json = """
			{
				"name": "scheduled",
				"description": "Test",
				"trigger": {
					"type": "scheduler",
					"cron": "0 * * * *",
					"enabled": true
				},
				"steps": []
			}
			""";

		// Act
		var orchestration = OrchestrationParser.ParseOrchestration(json, []);

		// Assert
		orchestration.Trigger.Should().NotBeNull();
		orchestration.Trigger.Should().BeOfType<SchedulerTriggerConfig>();
		var trigger = orchestration.Trigger as SchedulerTriggerConfig;
		trigger!.Cron.Should().Be("0 * * * *");
		trigger.Enabled.Should().BeTrue();
	}

	[Fact]
	public void ParseOrchestration_WebhookTrigger_ParsesCorrectly()
	{
		// Arrange
		var json = """
			{
				"name": "webhook-test",
				"description": "Test",
				"trigger": {
					"type": "webhook",
					"enabled": true,
					"maxConcurrent": 5
				},
				"steps": []
			}
			""";

		// Act
		var orchestration = OrchestrationParser.ParseOrchestration(json, []);

		// Assert
		orchestration.Trigger.Should().BeOfType<WebhookTriggerConfig>();
		var trigger = orchestration.Trigger as WebhookTriggerConfig;
		trigger!.MaxConcurrent.Should().Be(5);
	}

	[Fact]
	public void ParseOrchestration_LoopTrigger_ParsesCorrectly()
	{
		// Arrange
		var json = """
			{
				"name": "loop-test",
				"description": "Test",
				"trigger": {
					"type": "loop",
					"delaySeconds": 30,
					"maxIterations": 10,
					"continueOnFailure": true
				},
				"steps": []
			}
			""";

		// Act
		var orchestration = OrchestrationParser.ParseOrchestration(json, []);

		// Assert
		orchestration.Trigger.Should().BeOfType<LoopTriggerConfig>();
		var trigger = orchestration.Trigger as LoopTriggerConfig;
		trigger!.DelaySeconds.Should().Be(30);
		trigger.MaxIterations.Should().Be(10);
		trigger.ContinueOnFailure.Should().BeTrue();
	}

	[Fact]
	public void ParseOrchestration_EmailTrigger_ParsesCorrectly()
	{
		// Arrange
		var json = """
			{
				"name": "email-test",
				"description": "Test",
				"trigger": {
					"type": "email",
					"folderPath": "Inbox/Test",
					"pollIntervalSeconds": 120,
					"subjectContains": "Important"
				},
				"steps": []
			}
			""";

		// Act
		var orchestration = OrchestrationParser.ParseOrchestration(json, []);

		// Assert
		orchestration.Trigger.Should().BeOfType<EmailTriggerConfig>();
		var trigger = orchestration.Trigger as EmailTriggerConfig;
		trigger!.FolderPath.Should().Be("Inbox/Test");
		trigger.PollIntervalSeconds.Should().Be(120);
		trigger.SubjectContains.Should().Be("Important");
	}

	#endregion

	#region MCP Parsing

	[Fact]
	public void ParseMcps_LocalMcp_ParsesCorrectly()
	{
		// Arrange
		var json = """
			{
				"mcps": [
					{
						"name": "local-tool",
						"type": "local",
						"command": "node",
						"arguments": ["server.js", "--port", "3000"],
						"workingDirectory": "/app"
					}
				]
			}
			""";

		// Act
		var mcps = OrchestrationParser.ParseMcps(json);

		// Assert
		mcps.Should().HaveCount(1);
		mcps[0].Should().BeOfType<LocalMcp>();
		var local = mcps[0] as LocalMcp;
		local!.Name.Should().Be("local-tool");
		local.Command.Should().Be("node");
		local.Arguments.Should().BeEquivalentTo(["server.js", "--port", "3000"]);
		local.WorkingDirectory.Should().Be("/app");
	}

	[Fact]
	public void ParseMcps_RemoteMcp_ParsesCorrectly()
	{
		// Arrange
		var json = """
			{
				"mcps": [
					{
						"name": "remote-tool",
						"type": "remote",
						"endpoint": "https://api.example.com/mcp",
						"headers": {
							"Authorization": "Bearer token123"
						}
					}
				]
			}
			""";

		// Act
		var mcps = OrchestrationParser.ParseMcps(json);

		// Assert
		mcps.Should().HaveCount(1);
		mcps[0].Should().BeOfType<RemoteMcp>();
		var remote = mcps[0] as RemoteMcp;
		remote!.Name.Should().Be("remote-tool");
		remote.Endpoint.Should().Be("https://api.example.com/mcp");
		remote.Headers.Should().ContainKey("Authorization");
		remote.Headers["Authorization"].Should().Be("Bearer token123");
	}

	#endregion

	#region Metadata-Only Parsing

	[Fact]
	public void ParseOrchestrationMetadataOnly_DoesNotResolveMcps()
	{
		// Arrange
		var json = """
			{
				"name": "metadata-only",
				"description": "Test",
				"steps": [
					{
						"name": "step1",
						"type": "prompt",
						"dependsOn": [],
						"systemPrompt": "Test",
						"userPrompt": "Test",
						"model": "claude-opus-4.5",
						"mcps": ["nonexistent-mcp"]
					}
				]
			}
			""";

		// Act - Should not throw even though MCP doesn't exist
		var act = () => OrchestrationParser.ParseOrchestrationMetadataOnly(json);

		// Assert
		act.Should().NotThrow();
		var orchestration = act();
		orchestration.Name.Should().Be("metadata-only");
	}

	#endregion

	#region Subagent Parsing

	[Fact]
	public void ParseOrchestration_WithSubagents_ParsesAllFields()
	{
		// Arrange
		var json = """
			{
				"name": "subagent-test",
				"description": "Test orchestration with subagents",
				"steps": [
					{
						"name": "coordinator",
						"type": "prompt",
						"dependsOn": [],
						"systemPrompt": "You are a coordinator that delegates to subagents.",
						"userPrompt": "Process this request",
						"model": "claude-opus-4.5",
						"subagents": [
							{
								"name": "researcher",
								"displayName": "Research Agent",
								"description": "Specializes in finding information",
								"prompt": "You are a researcher. Find relevant information.",
								"tools": ["web_search", "read_file"],
								"infer": true
							},
							{
								"name": "writer",
								"displayName": "Writer Agent",
								"description": "Specializes in writing content",
								"prompt": "You are a writer. Create polished content.",
								"infer": false
							}
						]
					}
				]
			}
			""";

		// Act
		var orchestration = OrchestrationParser.ParseOrchestration(json, []);

		// Assert
		var step = orchestration.Steps[0] as PromptOrchestrationStep;
		step.Should().NotBeNull();
		step!.Subagents.Should().HaveCount(2);

		// First subagent
		var researcher = step.Subagents[0];
		researcher.Name.Should().Be("researcher");
		researcher.DisplayName.Should().Be("Research Agent");
		researcher.Description.Should().Be("Specializes in finding information");
		researcher.Prompt.Should().Be("You are a researcher. Find relevant information.");
		researcher.Tools.Should().BeEquivalentTo(["web_search", "read_file"]);
		researcher.Infer.Should().BeTrue();

		// Second subagent
		var writer = step.Subagents[1];
		writer.Name.Should().Be("writer");
		writer.DisplayName.Should().Be("Writer Agent");
		writer.Description.Should().Be("Specializes in writing content");
		writer.Prompt.Should().Be("You are a writer. Create polished content.");
		writer.Tools.Should().BeNull(); // Not specified
		writer.Infer.Should().BeFalse();
	}

	[Fact]
	public void ParseOrchestration_SubagentWithMinimalFields_UsesDefaults()
	{
		// Arrange
		var json = """
			{
				"name": "minimal-subagent",
				"description": "Test",
				"steps": [
					{
						"name": "step1",
						"type": "prompt",
						"dependsOn": [],
						"systemPrompt": "Test",
						"userPrompt": "Test",
						"model": "claude-opus-4.5",
						"subagents": [
							{
								"name": "minimal",
								"prompt": "Minimal prompt"
							}
						]
					}
				]
			}
			""";

		// Act
		var orchestration = OrchestrationParser.ParseOrchestration(json, []);

		// Assert
		var step = orchestration.Steps[0] as PromptOrchestrationStep;
		step!.Subagents.Should().HaveCount(1);

		var subagent = step.Subagents[0];
		subagent.Name.Should().Be("minimal");
		subagent.Prompt.Should().Be("Minimal prompt");
		subagent.DisplayName.Should().BeNull();
		subagent.Description.Should().BeNull();
		subagent.Tools.Should().BeNull();
		subagent.Mcps.Should().BeEmpty();
		subagent.Infer.Should().BeTrue(); // Default value
	}

	[Fact]
	public void ParseOrchestration_SubagentWithMcps_ResolvesMcpReferences()
	{
		// Arrange
		var json = """
			{
				"name": "subagent-mcp-test",
				"description": "Test",
				"steps": [
					{
						"name": "step1",
						"type": "prompt",
						"dependsOn": [],
						"systemPrompt": "Test",
						"userPrompt": "Test",
						"model": "claude-opus-4.5",
						"subagents": [
							{
								"name": "file-handler",
								"prompt": "Handle files",
								"mcps": ["filesystem"]
							}
						]
					}
				]
			}
			""";

		var externalMcps = new Mcp[]
		{
			new LocalMcp
			{
				Name = "filesystem",
				Type = McpType.Local,
				Command = "npx",
				Arguments = ["-y", "@anthropic/mcp-filesystem"]
			}
		};

		// Act
		var orchestration = OrchestrationParser.ParseOrchestration(json, externalMcps);

		// Assert
		var step = orchestration.Steps[0] as PromptOrchestrationStep;
		var subagent = step!.Subagents[0];
		subagent.Mcps.Should().HaveCount(1);
		subagent.Mcps[0].Name.Should().Be("filesystem");
		subagent.Mcps[0].Should().BeOfType<LocalMcp>();
	}

	[Fact]
	public void ParseOrchestration_SubagentWithMissingMcp_ThrowsInvalidOperationException()
	{
		// Arrange
		var json = """
			{
				"name": "subagent-missing-mcp",
				"description": "Test",
				"steps": [
					{
						"name": "step1",
						"type": "prompt",
						"dependsOn": [],
						"systemPrompt": "Test",
						"userPrompt": "Test",
						"model": "claude-opus-4.5",
						"subagents": [
							{
								"name": "broken",
								"prompt": "Test",
								"mcps": ["nonexistent-mcp"]
							}
						]
					}
				]
			}
			""";

		// Act
		var act = () => OrchestrationParser.ParseOrchestration(json, []);

		// Assert
		act.Should().Throw<InvalidOperationException>()
			.WithMessage("*'nonexistent-mcp'*not defined*");
	}

	[Fact]
	public void ParseOrchestration_WithoutSubagents_HasEmptySubagentsArray()
	{
		// Arrange
		var json = """
			{
				"name": "no-subagents",
				"description": "Test",
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
		step!.Subagents.Should().BeEmpty();
	}

	[Fact]
	public void ParseOrchestration_SubagentWithInlineMcps_ResolvesMcpReferences()
	{
		// Arrange
		var json = """
			{
				"name": "subagent-inline-mcp",
				"description": "Test",
				"mcps": [
					{
						"name": "inline-tool",
						"type": "local",
						"command": "node",
						"arguments": ["tool.js"]
					}
				],
				"steps": [
					{
						"name": "step1",
						"type": "prompt",
						"dependsOn": [],
						"systemPrompt": "Test",
						"userPrompt": "Test",
						"model": "claude-opus-4.5",
						"subagents": [
							{
								"name": "tool-user",
								"prompt": "Use the tool",
								"mcps": ["inline-tool"]
							}
						]
					}
				]
			}
			""";

		// Act
		var orchestration = OrchestrationParser.ParseOrchestration(json, []);

		// Assert
		var step = orchestration.Steps[0] as PromptOrchestrationStep;
		var subagent = step!.Subagents[0];
		subagent.Mcps.Should().HaveCount(1);
		subagent.Mcps[0].Name.Should().Be("inline-tool");
	}

	#endregion

	#region Error Handling

	[Fact]
	public void ParseOrchestration_InvalidJson_ThrowsJsonException()
	{
		// Arrange
		var json = "{ invalid json }";

		// Act
		var act = () => OrchestrationParser.ParseOrchestration(json, []);

		// Assert
		act.Should().Throw<System.Text.Json.JsonException>();
	}

	[Fact]
	public void ParseOrchestration_MissingType_ThrowsJsonException()
	{
		// Arrange
		var json = """
			{
				"name": "missing-type",
				"description": "Test",
				"steps": [
					{
						"name": "step1",
						"dependsOn": [],
						"systemPrompt": "Test",
						"userPrompt": "Test",
						"model": "claude-opus-4.5"
					}
				]
			}
			""";

		// Act
		var act = () => OrchestrationParser.ParseOrchestration(json, []);

		// Assert
		act.Should().Throw<System.Text.Json.JsonException>()
			.WithMessage("*type*");
	}

	[Fact]
	public void ParseOrchestration_UnknownStepType_ThrowsJsonException()
	{
		// Arrange
		var json = """
			{
				"name": "unknown-type",
				"description": "Test",
				"steps": [
					{
						"name": "step1",
						"type": "unknown",
						"dependsOn": []
					}
				]
			}
			""";

		// Act
		var act = () => OrchestrationParser.ParseOrchestration(json, []);

		// Assert - JsonException is thrown when no parser is registered for the unknown step type
		act.Should().Throw<System.Text.Json.JsonException>();
	}

	#endregion

	#region Example File Parsing

	[Theory]
	[MemberData(nameof(GetExampleFiles))]
	public void ParseOrchestration_ExampleFile_ParsesSuccessfully(string filePath)
	{
		// Arrange
		var json = File.ReadAllText(filePath);

		// Act
		var orchestration = OrchestrationParser.ParseOrchestration(json, []);

		// Assert
		orchestration.Should().NotBeNull();
		orchestration.Name.Should().NotBeNullOrWhiteSpace();
		orchestration.Description.Should().NotBeNullOrWhiteSpace();
		orchestration.Steps.Should().NotBeEmpty();
	}

	public static TheoryData<string> GetExampleFiles()
	{
		var data = new TheoryData<string>();
		var examplesDir = Path.GetFullPath(
			Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "examples"));

		if (Directory.Exists(examplesDir))
		{
			foreach (var file in Directory.GetFiles(examplesDir, "*.json"))
			{
				// Skip mcp.json — it's not an orchestration file
				if (Path.GetFileName(file).Equals("mcp.json", StringComparison.OrdinalIgnoreCase))
					continue;

				data.Add(file);
			}
		}

		return data;
	}

	#endregion
}
