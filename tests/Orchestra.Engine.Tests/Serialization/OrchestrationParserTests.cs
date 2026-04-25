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
	public void ParseOrchestration_WithHooks_ParsesHookDefinition()
	{
		var json = """
			{
				"name": "hooked",
				"description": "Test",
				"hooks": [
					{
						"name": "notify-build-failure",
						"on": "step.failure",
						"when": {
							"steps": {
								"names": ["build", "deploy"],
								"status": "failed",
								"match": "any"
							}
						},
						"payload": {
							"detail": "standard",
							"steps": "current",
							"includeRefs": true
						},
						"action": {
							"type": "script",
							"shell": "pwsh",
							"script": "param($input) $input | Out-File hook.json"
						}
					}
				],
				"steps": []
			}
			""";

		var orchestration = OrchestrationParser.ParseOrchestration(json, []);

		orchestration.Hooks.Should().HaveCount(1);
		var hook = orchestration.Hooks[0];
		hook.Name.Should().Be("notify-build-failure");
		hook.On.Should().Be(HookEventType.StepFailure);
		hook.When.Should().NotBeNull();
		hook.When!.Steps!.Names.Should().BeEquivalentTo(["build", "deploy"]);
		hook.When.Steps.Status.Should().Be(HookStepStatusFilter.Failed);
		hook.Payload.Detail.Should().Be(HookPayloadDetail.Standard);
		hook.Payload.Steps!.Selector.Should().Be(HookStepSelector.Current);
		hook.Payload.IncludeRefs.Should().BeTrue();
		hook.Action.Type.Should().Be(HookActionType.Script);
	}

	[Fact]
	public void ParseOrchestrationFile_WithHookScriptFile_ResolvesRelativePath()
	{
		var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "examples", "hooks-orchestration-failure.json"));

		var orchestration = OrchestrationParser.ParseOrchestrationFile(path, []);

		orchestration.Hooks.Should().HaveCount(1);
		orchestration.Hooks[0].Action.ScriptFile.Should().Be(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "hooks", "write-hook-payload.ps1")));
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
	public void ParseOrchestration_InlineMcpsOverrideExternal()
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

		// Assert — inline MCPs should override external MCPs with the same name
		var step = orchestration.Steps[0] as PromptOrchestrationStep;
		var localMcp = step!.Mcps[0] as LocalMcp;
		localMcp!.Command.Should().Be("inline-command");
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
	public void ParseOrchestration_TriggerWithInputHandlerModel_ParsesCorrectly()
	{
		// Arrange
		var json = """
			{
				"name": "handler-model-test",
				"description": "Test",
				"trigger": {
					"type": "webhook",
					"enabled": true,
					"inputHandlerPrompt": "Extract the fields",
					"inputHandlerModel": "claude-sonnet-4"
				},
				"steps": []
			}
			""";

		// Act
		var orchestration = OrchestrationParser.ParseOrchestration(json, []);

		// Assert
		orchestration.Trigger.Should().BeOfType<WebhookTriggerConfig>();
		var trigger = orchestration.Trigger as WebhookTriggerConfig;
		trigger!.InputHandlerPrompt.Should().Be("Extract the fields");
		trigger.InputHandlerModel.Should().Be("claude-sonnet-4");
	}

	[Fact]
	public void ParseOrchestration_TriggerWithoutInputHandlerModel_DefaultsToNull()
	{
		// Arrange
		var json = """
			{
				"name": "no-handler-model-test",
				"description": "Test",
				"trigger": {
					"type": "manual",
					"inputHandlerPrompt": "Transform params"
				},
				"steps": []
			}
			""";

		// Act
		var orchestration = OrchestrationParser.ParseOrchestration(json, []);

		// Assert
		orchestration.Trigger!.InputHandlerPrompt.Should().Be("Transform params");
		orchestration.Trigger.InputHandlerModel.Should().BeNull();
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

	#region Skill Directories Parsing

	[Fact]
	public void ParseOrchestration_WithSkillDirectories_ParsesDirectories()
	{
		// Arrange
		var json = """
			{
				"name": "skills-test",
				"description": "Test with skill directories",
				"steps": [
					{
						"name": "step1",
						"type": "prompt",
						"dependsOn": [],
						"systemPrompt": "Test",
						"userPrompt": "Test",
						"model": "claude-opus-4.5",
						"skillDirectories": ["./skills/coding", "/absolute/path/to/skills"]
					}
				]
			}
			""";

		// Act
		var orchestration = OrchestrationParser.ParseOrchestration(json, []);

		// Assert
		var step = orchestration.Steps[0] as PromptOrchestrationStep;
		step.Should().NotBeNull();
		step!.SkillDirectories.Should().HaveCount(2);
		step.SkillDirectories[0].Should().Be("./skills/coding");
		step.SkillDirectories[1].Should().Be("/absolute/path/to/skills");
	}

	[Fact]
	public void ParseOrchestration_WithEmptySkillDirectories_ParsesAsEmptyArray()
	{
		// Arrange
		var json = """
			{
				"name": "empty-skills-test",
				"description": "Test",
				"steps": [
					{
						"name": "step1",
						"type": "prompt",
						"dependsOn": [],
						"systemPrompt": "Test",
						"userPrompt": "Test",
						"model": "claude-opus-4.5",
						"skillDirectories": []
					}
				]
			}
			""";

		// Act
		var orchestration = OrchestrationParser.ParseOrchestration(json, []);

		// Assert
		var step = orchestration.Steps[0] as PromptOrchestrationStep;
		step!.SkillDirectories.Should().BeEmpty();
	}

	[Fact]
	public void ParseOrchestration_WithoutSkillDirectories_DefaultsToEmptyArray()
	{
		// Arrange
		var json = """
			{
				"name": "no-skills",
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
		step!.SkillDirectories.Should().BeEmpty();
	}

	[Fact]
	public void ParseOrchestration_SkillDirectoriesWithMcpsAndSubagents_AllParsed()
	{
		// Arrange
		var json = """
			{
				"name": "combined-test",
				"description": "Test with skills, MCPs, and subagents",
				"steps": [
					{
						"name": "coordinator",
						"type": "prompt",
						"dependsOn": [],
						"systemPrompt": "You are a coordinator.",
						"userPrompt": "Process this",
						"model": "claude-opus-4.5",
						"skillDirectories": ["./skills/devops"],
						"subagents": [
							{
								"name": "helper",
								"prompt": "You are a helper."
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
		step!.SkillDirectories.Should().ContainSingle().Which.Should().Be("./skills/devops");
		step.Subagents.Should().ContainSingle().Which.Name.Should().Be("helper");
	}

	#endregion

	#region Variables Parsing

	[Fact]
	public void ParseOrchestration_WithVariables_ParsesVariablesDictionary()
	{
		// Arrange
		var json = """
			{
				"name": "vars-test",
				"description": "Test with variables",
				"variables": {
					"outputDir": "/reports/daily",
					"logLevel": "debug",
					"greeting": "Hello from {{param.user}}"
				},
				"steps": []
			}
			""";

		// Act
		var orchestration = OrchestrationParser.ParseOrchestration(json, []);

		// Assert
		orchestration.Variables.Should().HaveCount(3);
		orchestration.Variables["outputDir"].Should().Be("/reports/daily");
		orchestration.Variables["logLevel"].Should().Be("debug");
		orchestration.Variables["greeting"].Should().Be("Hello from {{param.user}}");
	}

	[Fact]
	public void ParseOrchestration_WithoutVariables_DefaultsToEmptyDictionary()
	{
		// Arrange
		var json = """
			{
				"name": "no-vars",
				"description": "Test without variables",
				"steps": []
			}
			""";

		// Act
		var orchestration = OrchestrationParser.ParseOrchestration(json, []);

		// Assert
		orchestration.Variables.Should().NotBeNull();
		orchestration.Variables.Should().BeEmpty();
	}

	#endregion

	#region Step Enabled Parsing

	[Fact]
	public void ParseOrchestration_StepWithEnabledTrue_ParsesAsEnabled()
	{
		// Arrange
		var json = """
			{
				"name": "enabled-test",
				"description": "Test",
				"steps": [
					{
						"name": "step1",
						"type": "prompt",
						"dependsOn": [],
						"systemPrompt": "Test",
						"userPrompt": "Test",
						"model": "claude-opus-4.5",
						"enabled": true
					}
				]
			}
			""";

		// Act
		var orchestration = OrchestrationParser.ParseOrchestration(json, []);

		// Assert
		orchestration.Steps[0].Enabled.Should().BeTrue();
	}

	[Fact]
	public void ParseOrchestration_StepWithEnabledFalse_ParsesAsDisabled()
	{
		// Arrange
		var json = """
			{
				"name": "disabled-test",
				"description": "Test",
				"steps": [
					{
						"name": "step1",
						"type": "prompt",
						"dependsOn": [],
						"systemPrompt": "Test",
						"userPrompt": "Test",
						"model": "claude-opus-4.5",
						"enabled": false
					}
				]
			}
			""";

		// Act
		var orchestration = OrchestrationParser.ParseOrchestration(json, []);

		// Assert
		orchestration.Steps[0].Enabled.Should().BeFalse();
	}

	[Fact]
	public void ParseOrchestration_StepWithoutEnabled_DefaultsToTrue()
	{
		// Arrange
		var json = """
			{
				"name": "no-enabled-test",
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
		orchestration.Steps[0].Enabled.Should().BeTrue();
	}

	[Fact]
	public void ParseOrchestration_CommandStepWithEnabledFalse_ParsesAsDisabled()
	{
		// Arrange
		var json = """
			{
				"name": "disabled-command",
				"description": "Test",
				"steps": [
					{
						"name": "step1",
						"type": "command",
						"dependsOn": [],
						"command": "echo",
						"arguments": ["hello"],
						"enabled": false
					}
				]
			}
			""";

		// Act
		var orchestration = OrchestrationParser.ParseOrchestration(json, []);

		// Assert
		orchestration.Steps[0].Enabled.Should().BeFalse();
		orchestration.Steps[0].Should().BeOfType<CommandOrchestrationStep>();
	}

	[Fact]
	public void ParseOrchestration_HttpStepWithEnabledFalse_ParsesAsDisabled()
	{
		// Arrange
		var json = """
			{
				"name": "disabled-http",
				"description": "Test",
				"steps": [
					{
						"name": "step1",
						"type": "http",
						"dependsOn": [],
						"url": "https://example.com",
						"enabled": false
					}
				]
			}
			""";

		// Act
		var orchestration = OrchestrationParser.ParseOrchestration(json, []);

		// Assert
		orchestration.Steps[0].Enabled.Should().BeFalse();
		orchestration.Steps[0].Should().BeOfType<HttpOrchestrationStep>();
	}

	[Fact]
	public void ParseOrchestration_TransformStepWithEnabledFalse_ParsesAsDisabled()
	{
		// Arrange
		var json = """
			{
				"name": "disabled-transform",
				"description": "Test",
				"steps": [
					{
						"name": "step1",
						"type": "transform",
						"dependsOn": [],
						"template": "{{step1.output}}",
						"enabled": false
					}
				]
			}
			""";

		// Act
		var orchestration = OrchestrationParser.ParseOrchestration(json, []);

		// Assert
		orchestration.Steps[0].Enabled.Should().BeFalse();
		orchestration.Steps[0].Should().BeOfType<TransformOrchestrationStep>();
	}

	#endregion

	#region InfiniteSession Parsing

	[Fact]
	public void ParseOrchestration_WithInfiniteSessions_ParsesConfig()
	{
		// Arrange
		var json = """
			{
				"name": "test",
				"description": "Test",
				"steps": [{
					"name": "step1",
					"type": "prompt",
					"model": "gpt-5",
					"systemPrompt": "test",
					"userPrompt": "test",
					"infiniteSessions": {
						"enabled": true,
						"backgroundCompactionThreshold": 0.85,
						"bufferExhaustionThreshold": 0.97
					}
				}]
			}
			""";

		// Act
		var orchestration = OrchestrationParser.ParseOrchestration(json, []);

		// Assert
		var step = orchestration.Steps[0] as PromptOrchestrationStep;
		step!.InfiniteSessions.Should().NotBeNull();
		step.InfiniteSessions!.Enabled.Should().BeTrue();
		step.InfiniteSessions.BackgroundCompactionThreshold.Should().Be(0.85);
		step.InfiniteSessions.BufferExhaustionThreshold.Should().Be(0.97);
	}

	#endregion

	#region SystemPromptMode Customize Parsing

	[Fact]
	public void ParseOrchestration_WithCustomizeModeAndSections_ParsesSections()
	{
		// Arrange
		var json = """
			{
				"name": "test",
				"description": "Test",
				"steps": [{
					"name": "step1",
					"type": "prompt",
					"model": "gpt-5",
					"systemPrompt": "test",
					"userPrompt": "test",
					"systemPromptMode": "customize",
					"systemPromptSections": {
						"tone": { "action": "replace", "content": "Be concise" },
						"code_change_rules": { "action": "remove" }
					}
				}]
			}
			""";

		// Act
		var orchestration = OrchestrationParser.ParseOrchestration(json, []);

		// Assert
		var step = orchestration.Steps[0] as PromptOrchestrationStep;
		step!.SystemPromptMode.Should().Be(SystemPromptMode.Customize);
		step.SystemPromptSections.Should().HaveCount(2);
		step.SystemPromptSections!["tone"].Action.Should().Be(SystemPromptSectionAction.Replace);
		step.SystemPromptSections["tone"].Content.Should().Be("Be concise");
		step.SystemPromptSections["code_change_rules"].Action.Should().Be(SystemPromptSectionAction.Remove);
	}

	#endregion

	#region Attachments Parsing

	[Fact]
	public void ParseOrchestration_WithAttachments_ParsesFileAndBlobTypes()
	{
		// Arrange
		var json = """
			{
				"name": "test",
				"description": "Test",
				"steps": [{
					"name": "step1",
					"type": "prompt",
					"model": "gpt-5",
					"systemPrompt": "test",
					"userPrompt": "test",
					"attachments": [
						{ "type": "file", "path": "/path/to/image.png", "displayName": "screenshot" },
						{ "type": "blob", "data": "base64data", "mimeType": "image/png" }
					]
				}]
			}
			""";

		// Act
		var orchestration = OrchestrationParser.ParseOrchestration(json, []);

		// Assert
		var step = orchestration.Steps[0] as PromptOrchestrationStep;
		step!.Attachments.Should().HaveCount(2);

		var fileAttachment = step.Attachments[0].Should().BeOfType<FileImageAttachment>().Subject;
		fileAttachment.Path.Should().Be("/path/to/image.png");
		fileAttachment.DisplayName.Should().Be("screenshot");

		var blobAttachment = step.Attachments[1].Should().BeOfType<BlobImageAttachment>().Subject;
		blobAttachment.Data.Should().Be("base64data");
		blobAttachment.MimeType.Should().Be("image/png");
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
		// Act — use ParseOrchestrationFile to support both JSON and YAML example files
		var orchestration = OrchestrationParser.ParseOrchestrationFile(filePath, []);

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
			foreach (var file in OrchestrationParser.GetOrchestrationFiles(examplesDir))
			{
				// Skip orchestra.mcp.json — it's not an orchestration file
				if (Path.GetFileName(file).Equals("orchestra.mcp.json", StringComparison.OrdinalIgnoreCase))
					continue;

				// Skip orchestra.services.json — it's not an orchestration file
				if (Path.GetFileName(file).Equals("orchestra.services.json", StringComparison.OrdinalIgnoreCase))
					continue;

				data.Add(file);
			}
		}

		return data;
	}

	#endregion

	#region Inputs Parsing

	[Fact]
	public void ParseOrchestration_WithInputs_ParsesTypedInputDefinitions()
	{
		var json = """
			{
				"name": "with-inputs",
				"description": "Test",
				"inputs": {
					"serviceName": {
						"type": "string",
						"description": "Name of the service to deploy",
						"required": true
					},
					"environment": {
						"type": "string",
						"description": "Target environment",
						"enum": ["staging", "production"]
					},
					"dryRun": {
						"type": "boolean",
						"description": "Simulate without deploying",
						"required": false,
						"default": "false"
					},
					"retryCount": {
						"type": "number",
						"description": "Number of retries",
						"required": false,
						"default": "3"
					}
				},
				"steps": [
					{
						"name": "step1",
						"type": "prompt",
						"systemPrompt": "Deploy {{param.serviceName}}",
						"userPrompt": "Deploy to {{param.environment}}",
						"model": "claude-opus-4.5",
						"parameters": ["serviceName", "environment", "dryRun", "retryCount"]
					}
				]
			}
			""";

		var orchestration = OrchestrationParser.ParseOrchestration(json, []);

		orchestration.Inputs.Should().NotBeNull();
		orchestration.Inputs.Should().HaveCount(4);

		orchestration.Inputs!["serviceName"].Type.Should().Be(InputType.String);
		orchestration.Inputs["serviceName"].Description.Should().Be("Name of the service to deploy");
		orchestration.Inputs["serviceName"].Required.Should().BeTrue();

		orchestration.Inputs["environment"].Type.Should().Be(InputType.String);
		orchestration.Inputs["environment"].Enum.Should().BeEquivalentTo("staging", "production");

		orchestration.Inputs["dryRun"].Type.Should().Be(InputType.Boolean);
		orchestration.Inputs["dryRun"].Required.Should().BeFalse();
		orchestration.Inputs["dryRun"].Default.Should().Be("false");

		orchestration.Inputs["retryCount"].Type.Should().Be(InputType.Number);
		orchestration.Inputs["retryCount"].Required.Should().BeFalse();
		orchestration.Inputs["retryCount"].Default.Should().Be("3");
	}

	[Fact]
	public void ParseOrchestration_WithoutInputs_InputsIsNull()
	{
		var json = """
			{
				"name": "no-inputs",
				"description": "Test",
				"steps": [
					{
						"name": "step1",
						"type": "prompt",
						"systemPrompt": "Test",
						"userPrompt": "Test",
						"model": "claude-opus-4.5",
						"parameters": ["param1"]
					}
				]
			}
			""";

		var orchestration = OrchestrationParser.ParseOrchestration(json, []);

		orchestration.Inputs.Should().BeNull();
		orchestration.Steps[0].Parameters.Should().Contain("param1");
	}

	[Fact]
	public void ParseOrchestration_WithMinimalInputs_UsesDefaults()
	{
		var json = """
			{
				"name": "minimal-inputs",
				"description": "Test",
				"inputs": {
					"name": {}
				},
				"steps": []
			}
			""";

		var orchestration = OrchestrationParser.ParseOrchestration(json, []);

		orchestration.Inputs.Should().NotBeNull();
		orchestration.Inputs!["name"].Type.Should().Be(InputType.String);
		orchestration.Inputs["name"].Required.Should().BeTrue();
		orchestration.Inputs["name"].Description.Should().BeNull();
		orchestration.Inputs["name"].Default.Should().BeNull();
		orchestration.Inputs["name"].Enum.Should().BeEmpty();
	}

	[Fact]
	public void ParseOrchestration_WithEmptyInputs_ParsesEmptyDictionary()
	{
		var json = """
			{
				"name": "empty-inputs",
				"description": "Test",
				"inputs": {},
				"steps": []
			}
			""";

		var orchestration = OrchestrationParser.ParseOrchestration(json, []);

		orchestration.Inputs.Should().NotBeNull();
		orchestration.Inputs.Should().BeEmpty();
	}

	#endregion

	#region Meta-Orchestration Parsing

	[Fact]
	public void ParseOrchestration_GenerateOrchestration_ParsesWithCorrectStructure()
	{
		// Arrange
		var examplesDir = Path.GetFullPath(
			Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "examples"));
		var filePath = Path.Combine(examplesDir, "generate-orchestration.yaml");

		// Act
		var orchestration = OrchestrationParser.ParseOrchestrationFile(filePath, []);

		// Assert
		orchestration.Name.Should().Be("generate-orchestration");
		orchestration.Tags.Should().Contain("meta");
		orchestration.Tags.Should().Contain("generator");

		// Typed inputs
		orchestration.Inputs.Should().NotBeNull();
		orchestration.Inputs.Should().ContainKey("description");
		orchestration.Inputs!["description"].Type.Should().Be(InputType.String);
		orchestration.Inputs["description"].Required.Should().BeTrue();

		orchestration.Inputs.Should().ContainKey("register");
		orchestration.Inputs["register"].Type.Should().Be(InputType.Boolean);
		orchestration.Inputs["register"].Required.Should().BeFalse();
		orchestration.Inputs["register"].Default.Should().Be("false");

		orchestration.Inputs.Should().ContainKey("outputPath");
		orchestration.Inputs["outputPath"].Type.Should().Be(InputType.String);
		orchestration.Inputs["outputPath"].Required.Should().BeFalse();

		// Steps — generate, validate, save-orchestration, register-orchestration, format-output
		orchestration.Steps.Should().HaveCount(5);

		// Generate step with subagents
		var generateStep = orchestration.Steps[0].Should().BeOfType<PromptOrchestrationStep>().Subject;
		generateStep.Name.Should().Be("generate");
		generateStep.Model.Should().Be("claude-opus-4.6");
		generateStep.SkillDirectories.Should().ContainMatch("*skills*orchestration-authoring*");
		generateStep.Mcps.Should().HaveCount(1);
		generateStep.OutputHandlerPrompt.Should().NotBeNullOrWhiteSpace();
		generateStep.Subagents.Should().HaveCount(2);
		generateStep.Subagents[0].Name.Should().Be("intent-validator");
		generateStep.Subagents[1].Name.Should().Be("best-practices-expert");

		// Validate step (checker/loop)
		var validateStep = orchestration.Steps[1].Should().BeOfType<PromptOrchestrationStep>().Subject;
		validateStep.Name.Should().Be("validate");
		validateStep.DependsOn.Should().Contain("generate");
		validateStep.Loop.Should().NotBeNull();
		validateStep.Loop!.Target.Should().Be("generate");
		validateStep.Loop.MaxIterations.Should().Be(2);
		validateStep.Loop.ExitPattern.Should().Be("VALID");
		validateStep.SkillDirectories.Should().ContainMatch("*skills*orchestration-authoring*");

		// Save step
		var saveStep = orchestration.Steps[2].Should().BeOfType<PromptOrchestrationStep>().Subject;
		saveStep.Name.Should().Be("save-orchestration");
		saveStep.DependsOn.Should().Contain("validate");
		saveStep.Mcps.Should().HaveCount(1);

		// Register step
		var registerStep = orchestration.Steps[3].Should().BeOfType<PromptOrchestrationStep>().Subject;
		registerStep.Name.Should().Be("register-orchestration");
		registerStep.DependsOn.Should().Contain("save-orchestration");
		registerStep.Mcps.Should().HaveCount(1);

		// Format output (Transform step)
		orchestration.Steps[4].Name.Should().Be("format-output");
		orchestration.Steps[4].DependsOn.Should().Contain("save-orchestration");
		orchestration.Steps[4].DependsOn.Should().Contain("register-orchestration");

		// MCP definitions — orchestra-control and filesystem
		orchestration.Mcps.Should().HaveCount(2);
		var controlMcp = orchestration.Mcps.FirstOrDefault(m => m.Name == "orchestra-control");
		controlMcp.Should().NotBeNull();
		controlMcp.Should().BeOfType<RemoteMcp>();
		var fsMcp = orchestration.Mcps.FirstOrDefault(m => m.Name == "filesystem");
		fsMcp.Should().NotBeNull();
		fsMcp.Should().BeOfType<LocalMcp>();
	}

	[Fact]
	public void ParseOrchestration_UpdateOrchestrationDigest_ParsesWithCorrectStructure()
	{
		// Arrange
		var examplesDir = Path.GetFullPath(
			Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "examples"));
		var filePath = Path.Combine(examplesDir, "update-orchestration-digest.json");
		var json = File.ReadAllText(filePath);

		// Act
		var orchestration = OrchestrationParser.ParseOrchestration(json, []);

		// Assert
		orchestration.Name.Should().Be("update-orchestration-digest");
		orchestration.Tags.Should().Contain("meta");
		orchestration.Tags.Should().Contain("maintenance");

		// Variables
		orchestration.Variables.Should().ContainKey("repoPath");
		orchestration.Variables.Should().ContainKey("skillPath");

		// Trigger (scheduler)
		orchestration.Trigger.Type.Should().Be(TriggerType.Scheduler);
		orchestration.Trigger.Enabled.Should().BeFalse();
		var schedulerTrigger = orchestration.Trigger.Should().BeOfType<SchedulerTriggerConfig>().Subject;
		schedulerTrigger.IntervalSeconds.Should().Be(86400);

		// Steps — check count and key step names
		orchestration.Steps.Should().HaveCountGreaterThanOrEqualTo(7);
		orchestration.Steps.Select(s => s.Name).Should().Contain("check-changes");
		orchestration.Steps.Select(s => s.Name).Should().Contain("gate");
		orchestration.Steps.Select(s => s.Name).Should().Contain("regenerate-digest");
		orchestration.Steps.Select(s => s.Name).Should().Contain("write-digest");

		// Gate step is a prompt step
		var gateStep = orchestration.Steps.First(s => s.Name == "gate")
			.Should().BeOfType<PromptOrchestrationStep>().Subject;
		gateStep.DependsOn.Should().Contain("check-changes");

		// Regenerate step depends on all read steps
		var regenerateStep = orchestration.Steps.First(s => s.Name == "regenerate-digest")
			.Should().BeOfType<PromptOrchestrationStep>().Subject;
		regenerateStep.DependsOn.Should().Contain("read-schema-doc");
		regenerateStep.DependsOn.Should().Contain("read-models");
		regenerateStep.DependsOn.Should().Contain("read-examples");
		regenerateStep.DependsOn.Should().Contain("read-current-digest");
		regenerateStep.ReasoningLevel.Should().Be(ReasoningLevel.High);

		// Write step depends on regenerate
		var writeStep = orchestration.Steps.First(s => s.Name == "write-digest")
			.Should().BeOfType<CommandOrchestrationStep>().Subject;
		writeStep.DependsOn.Should().Contain("regenerate-digest");
		writeStep.Stdin.Should().NotBeNullOrWhiteSpace();

		// Read steps should run in parallel (all depend only on gate)
		var readSteps = orchestration.Steps
			.Where(s => s.Name.StartsWith("read-"))
			.ToList();
		readSteps.Should().HaveCount(4);
		readSteps.Should().AllSatisfy(s => s.DependsOn.Should().Contain("gate"));
	}

	#endregion

	#region YAML Parsing

	[Fact]
	public void ConvertYamlToJson_ValidYaml_ReturnsValidJson()
	{
		var yaml = """
			name: test-orchestration
			description: Test description
			steps:
			  - name: step1
			    type: prompt
			    dependsOn: []
			    systemPrompt: You are a test assistant.
			    userPrompt: Test prompt
			    model: claude-opus-4.6
			""";

		var json = OrchestrationParser.ConvertYamlToJson(yaml);
		var orchestration = OrchestrationParser.ParseOrchestration(json, []);

		orchestration.Name.Should().Be("test-orchestration");
		orchestration.Description.Should().Be("Test description");
		orchestration.Steps.Should().HaveCount(1);
		orchestration.Steps[0].Name.Should().Be("step1");
	}

	[Fact]
	public void ConvertYamlToJson_MultilinePrompts_PreservesContent()
	{
		var yaml = """
			name: multiline-test
			description: Test multiline prompts
			steps:
			  - name: step1
			    type: prompt
			    dependsOn: []
			    systemPrompt: |
			      You are a helpful assistant.
			      You should be thorough and precise.
			      Always provide examples.
			    userPrompt: |
			      Analyze the following:
			      {{param.input}}
			    model: claude-opus-4.6
			""";

		var json = OrchestrationParser.ConvertYamlToJson(yaml);
		var orchestration = OrchestrationParser.ParseOrchestration(json, []);

		var step = orchestration.Steps[0].Should().BeOfType<PromptOrchestrationStep>().Subject;
		step.SystemPrompt.Should().Contain("You are a helpful assistant.");
		step.SystemPrompt.Should().Contain("Always provide examples.");
		step.UserPrompt.Should().Contain("{{param.input}}");
	}

	[Fact]
	public void ConvertYamlToJson_WithVariables_ExtractsVariables()
	{
		var yaml = """
			name: vars-test
			description: Test variables
			variables:
			  greeting: hello
			  target: world
			steps:
			  - name: step1
			    type: prompt
			    dependsOn: []
			    systemPrompt: "{{vars.greeting}} {{vars.target}}"
			    userPrompt: test
			    model: claude-opus-4.6
			""";

		var json = OrchestrationParser.ConvertYamlToJson(yaml);
		var orchestration = OrchestrationParser.ParseOrchestration(json, []);

		orchestration.Variables.Should().ContainKey("greeting");
		orchestration.Variables["greeting"].Should().Be("hello");
		orchestration.Variables.Should().ContainKey("target");
		orchestration.Variables["target"].Should().Be("world");
	}

	[Fact]
	public void ConvertYamlToJson_WithSchedulerTrigger_ParsesTriggerConfig()
	{
		var yaml = """
			name: trigger-test
			description: Test trigger
			trigger:
			  type: scheduler
			  cron: "0 */5 * * *"
			  enabled: true
			steps:
			  - name: step1
			    type: prompt
			    dependsOn: []
			    systemPrompt: test
			    userPrompt: test
			    model: claude-opus-4.6
			""";

		var json = OrchestrationParser.ConvertYamlToJson(yaml);
		var orchestration = OrchestrationParser.ParseOrchestration(json, []);

		orchestration.Trigger.Should().BeOfType<SchedulerTriggerConfig>();
		var trigger = (SchedulerTriggerConfig)orchestration.Trigger;
		trigger.Cron.Should().Be("0 */5 * * *");
	}

	[Fact]
	public void ConvertYamlToJson_AllStepTypes_ParsesCorrectly()
	{
		var yaml = """
			name: complex-test
			description: Test all step types
			steps:
			  - name: prompt-step
			    type: Prompt
			    dependsOn: []
			    systemPrompt: test system
			    userPrompt: test user
			    model: claude-opus-4.6
			  - name: http-step
			    type: Http
			    dependsOn:
			      - prompt-step
			    method: POST
			    url: https://api.example.com/data
			    headers:
			      Authorization: Bearer token
			    body: "{{prompt-step.output}}"
			  - name: transform-step
			    type: Transform
			    dependsOn:
			      - http-step
			    template: "Result: {{http-step.output}}"
			  - name: command-step
			    type: Command
			    dependsOn: []
			    command: echo
			    arguments:
			      - hello
			      - world
			""";

		var json = OrchestrationParser.ConvertYamlToJson(yaml);
		var orchestration = OrchestrationParser.ParseOrchestration(json, []);

		orchestration.Steps.Should().HaveCount(4);
		orchestration.Steps[0].Should().BeOfType<PromptOrchestrationStep>();
		orchestration.Steps[1].Should().BeOfType<HttpOrchestrationStep>();
		orchestration.Steps[2].Should().BeOfType<TransformOrchestrationStep>();
		orchestration.Steps[3].Should().BeOfType<CommandOrchestrationStep>();

		var httpStep = (HttpOrchestrationStep)orchestration.Steps[1];
		httpStep.Method.Should().Be("POST");
		httpStep.Url.Should().Be("https://api.example.com/data");

		var cmdStep = (CommandOrchestrationStep)orchestration.Steps[3];
		cmdStep.Command.Should().Be("echo");
		cmdStep.Arguments.Should().BeEquivalentTo(["hello", "world"]);
	}

	[Fact]
	public void ConvertYamlToJson_EmptyContent_ThrowsInvalidOperationException()
	{
		var act = () => OrchestrationParser.ConvertYamlToJson("");

		act.Should().Throw<InvalidOperationException>()
			.WithMessage("*empty*");
	}

	[Fact]
	public void IsYamlFile_DetectsYamlExtensions()
	{
		OrchestrationParser.IsYamlFile("test.yaml").Should().BeTrue();
		OrchestrationParser.IsYamlFile("test.yml").Should().BeTrue();
		OrchestrationParser.IsYamlFile("test.YAML").Should().BeTrue();
		OrchestrationParser.IsYamlFile("test.YML").Should().BeTrue();
		OrchestrationParser.IsYamlFile("test.json").Should().BeFalse();
		OrchestrationParser.IsYamlFile("test.txt").Should().BeFalse();
		OrchestrationParser.IsYamlFile("yaml.json").Should().BeFalse();
	}

	[Fact]
	public void ConvertYamlToJson_WithInputs_ParsesTypedInputs()
	{
		var yaml = """
			name: inputs-test
			description: Test inputs
			inputs:
			  ticker:
			    type: string
			    description: Stock ticker symbol
			    required: true
			  includeHistory:
			    type: boolean
			    default: "true"
			steps:
			  - name: step1
			    type: prompt
			    dependsOn: []
			    parameters:
			      - ticker
			    systemPrompt: test
			    userPrompt: "Analyze {{param.ticker}}"
			    model: claude-opus-4.6
			""";

		var json = OrchestrationParser.ConvertYamlToJson(yaml);
		var orchestration = OrchestrationParser.ParseOrchestration(json, []);

		orchestration.Inputs.Should().NotBeNull();
		orchestration.Inputs.Should().ContainKey("ticker");
		orchestration.Inputs!["ticker"].Type.Should().Be(InputType.String);
		orchestration.Inputs["ticker"].Required.Should().BeTrue();
	}

	[Fact]
	public void ParseOrchestrationFile_YamlFile_ParsesSuccessfully()
	{
		var yaml = """
			name: file-test
			description: Test YAML file parsing
			steps:
			  - name: step1
			    type: prompt
			    dependsOn: []
			    systemPrompt: |
			      You are helpful.
			    userPrompt: Hello
			    model: claude-opus-4.6
			""";

		var tempDir = Path.Combine(Path.GetTempPath(), $"orchestra-yaml-test-{Guid.NewGuid():N}");
		Directory.CreateDirectory(tempDir);
		var filePath = Path.Combine(tempDir, "test.yaml");

		try
		{
			File.WriteAllText(filePath, yaml);
			var orchestration = OrchestrationParser.ParseOrchestrationFile(filePath, []);

			orchestration.Name.Should().Be("file-test");
			orchestration.Steps.Should().HaveCount(1);

			var step = orchestration.Steps[0].Should().BeOfType<PromptOrchestrationStep>().Subject;
			step.SystemPrompt.Should().Contain("You are helpful.");
		}
		finally
		{
			Directory.Delete(tempDir, recursive: true);
		}
	}

	[Fact]
	public void ParseOrchestrationFileMetadataOnly_YamlFile_ParsesSuccessfully()
	{
		var yaml = """
			name: metadata-test
			description: Test YAML metadata-only parsing
			version: "2.0.0"
			steps:
			  - name: step1
			    type: prompt
			    dependsOn: []
			    systemPrompt: test
			    userPrompt: test
			    model: claude-opus-4.6
			""";

		var tempDir = Path.Combine(Path.GetTempPath(), $"orchestra-yaml-test-{Guid.NewGuid():N}");
		Directory.CreateDirectory(tempDir);
		var filePath = Path.Combine(tempDir, "test.yml");

		try
		{
			File.WriteAllText(filePath, yaml);
			var orchestration = OrchestrationParser.ParseOrchestrationFileMetadataOnly(filePath);

			orchestration.Name.Should().Be("metadata-test");
			orchestration.Version.Should().Be("2.0.0");
			orchestration.Steps.Should().HaveCount(1);
		}
		finally
		{
			Directory.Delete(tempDir, recursive: true);
		}
	}

	[Fact]
	public void GetOrchestrationFiles_FindsJsonAndYamlFiles()
	{
		var tempDir = Path.Combine(Path.GetTempPath(), $"orchestra-scan-test-{Guid.NewGuid():N}");
		Directory.CreateDirectory(tempDir);

		try
		{
			File.WriteAllText(Path.Combine(tempDir, "test1.json"), "{}");
			File.WriteAllText(Path.Combine(tempDir, "test2.yaml"), "name: test");
			File.WriteAllText(Path.Combine(tempDir, "test3.yml"), "name: test");
			File.WriteAllText(Path.Combine(tempDir, "readme.txt"), "ignore");
			File.WriteAllText(Path.Combine(tempDir, "data.xml"), "ignore");

			var files = OrchestrationParser.GetOrchestrationFiles(tempDir);

			files.Should().HaveCount(3);
			files.Should().Contain(f => f.EndsWith(".json"));
			files.Should().Contain(f => f.EndsWith(".yaml"));
			files.Should().Contain(f => f.EndsWith(".yml"));
		}
		finally
		{
			Directory.Delete(tempDir, recursive: true);
		}
	}

	[Fact]
	public void ConvertYamlToJson_WithSubagents_ParsesCorrectly()
	{
		var yaml = """
			name: subagent-test
			description: Test subagents in YAML
			steps:
			  - name: main-step
			    type: Prompt
			    dependsOn: []
			    systemPrompt: You are an orchestrator.
			    userPrompt: Coordinate the work.
			    model: claude-opus-4.6
			    subagents:
			      - name: researcher
			        description: Research agent
			        prompt: |
			          You are a research specialist.
			          Find relevant information on the topic.
			        infer: true
			""";

		var json = OrchestrationParser.ConvertYamlToJson(yaml);
		var orchestration = OrchestrationParser.ParseOrchestration(json, []);

		var step = orchestration.Steps[0].Should().BeOfType<PromptOrchestrationStep>().Subject;
		step.Subagents.Should().HaveCount(1);
		step.Subagents[0].Name.Should().Be("researcher");
		step.Subagents[0].Prompt.Should().Contain("You are a research specialist.");
	}

	[Fact]
	public void ConvertYamlToJson_WithLoop_ParsesCorrectly()
	{
		var yaml = """
			name: loop-test
			description: Test loop config in YAML
			steps:
			  - name: iterative-step
			    type: Prompt
			    dependsOn: []
			    systemPrompt: Generate code.
			    userPrompt: Write a function.
			    model: claude-opus-4.6
			    loop:
			      target: iterative-step
			      maxIterations: 3
			      exitPattern: APPROVED
			""";

		var json = OrchestrationParser.ConvertYamlToJson(yaml);
		var orchestration = OrchestrationParser.ParseOrchestration(json, []);

		var step = orchestration.Steps[0].Should().BeOfType<PromptOrchestrationStep>().Subject;
		step.Loop.Should().NotBeNull();
		step.Loop!.Target.Should().Be("iterative-step");
		step.Loop.MaxIterations.Should().Be(3);
		step.Loop.ExitPattern.Should().Be("APPROVED");
	}

	#endregion

	#region Advanced Copilot SDK Features Example

	[Fact]
	public void ParseOrchestration_AdvancedCopilotSdkFeaturesJson_ParsesAllNewFeatures()
	{
		// Arrange
		var examplesDir = Path.GetFullPath(
			Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "examples"));
		var filePath = Path.Combine(examplesDir, "copilot-sdk-advanced-features.json");

		// Act
		var orchestration = OrchestrationParser.ParseOrchestrationFile(filePath, []);

		// Assert
		orchestration.Name.Should().Be("advanced-copilot-sdk-features");
		orchestration.Tags.Should().Contain("copilot-sdk");
		orchestration.Steps.Should().HaveCount(5);

		// Step: analyze-ui-mockup — customize mode + sections + attachments + infinite sessions
		var analyzeStep = orchestration.Steps.OfType<PromptOrchestrationStep>()
			.Single(s => s.Name == "analyze-ui-mockup");
		analyzeStep.SystemPromptMode.Should().Be(SystemPromptMode.Customize);
		analyzeStep.SystemPromptSections.Should().NotBeNull();
		analyzeStep.SystemPromptSections.Should().ContainKey("tone");
		analyzeStep.SystemPromptSections!["tone"].Action.Should().Be(SystemPromptSectionAction.Replace);
		analyzeStep.SystemPromptSections["tone"].Content.Should().Contain("direct and actionable");
		analyzeStep.SystemPromptSections.Should().ContainKey("code_change_rules");
		analyzeStep.SystemPromptSections["code_change_rules"].Action.Should().Be(SystemPromptSectionAction.Remove);
		analyzeStep.SystemPromptSections.Should().ContainKey("guidelines");
		analyzeStep.SystemPromptSections["guidelines"].Action.Should().Be(SystemPromptSectionAction.Append);

		analyzeStep.Attachments.Should().HaveCount(1);
		var fileAttachment = analyzeStep.Attachments[0].Should().BeOfType<FileImageAttachment>().Subject;
		fileAttachment.Path.Should().Be("{{param.mockupPath}}");
		fileAttachment.DisplayName.Should().Be("UI Mockup");

		analyzeStep.InfiniteSessions.Should().NotBeNull();
		analyzeStep.InfiniteSessions!.Enabled.Should().BeTrue();
		analyzeStep.InfiniteSessions.BackgroundCompactionThreshold.Should().Be(0.80);
		analyzeStep.InfiniteSessions.BufferExhaustionThreshold.Should().Be(0.95);

		// Step: code-review-readonly — customize mode enforcing read-only
		var reviewStep = orchestration.Steps.OfType<PromptOrchestrationStep>()
			.Single(s => s.Name == "code-review-readonly");
		reviewStep.SystemPromptMode.Should().Be(SystemPromptMode.Customize);
		reviewStep.SystemPromptSections.Should().ContainKey("code_change_rules");
		reviewStep.SystemPromptSections!["code_change_rules"].Action.Should().Be(SystemPromptSectionAction.Replace);
		reviewStep.SystemPromptSections["code_change_rules"].Content.Should().Contain("read-only review mode");
	}

	[Fact]
	public void ParseOrchestration_AdvancedCopilotSdkFeaturesYaml_ParsesAllNewFeatures()
	{
		// Arrange
		var examplesDir = Path.GetFullPath(
			Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "examples"));
		var filePath = Path.Combine(examplesDir, "copilot-sdk-advanced-features.yaml");

		// Act
		var orchestration = OrchestrationParser.ParseOrchestrationFile(filePath, []);

		// Assert
		orchestration.Name.Should().Be("copilot-sdk-advanced-features");
		orchestration.Tags.Should().Contain("infinite-sessions");
		orchestration.Steps.Should().HaveCount(5);

		// Step: analyze-ui — full feature showcase
		var analyzeStep = orchestration.Steps.OfType<PromptOrchestrationStep>()
			.Single(s => s.Name == "analyze-ui");
		analyzeStep.SystemPromptMode.Should().Be(SystemPromptMode.Customize);
		analyzeStep.SystemPromptSections.Should().HaveCount(3);
		analyzeStep.SystemPromptSections!["tone"].Action.Should().Be(SystemPromptSectionAction.Replace);
		analyzeStep.SystemPromptSections["code_change_rules"].Action.Should().Be(SystemPromptSectionAction.Remove);
		analyzeStep.SystemPromptSections["guidelines"].Action.Should().Be(SystemPromptSectionAction.Append);

		analyzeStep.Attachments.Should().HaveCount(1);
		analyzeStep.Attachments[0].Should().BeOfType<FileImageAttachment>();

		analyzeStep.InfiniteSessions.Should().NotBeNull();
		analyzeStep.InfiniteSessions!.Enabled.Should().BeTrue();

		// Step: code-review — infinite sessions disabled
		var reviewStep = orchestration.Steps.OfType<PromptOrchestrationStep>()
			.Single(s => s.Name == "code-review");
		reviewStep.InfiniteSessions.Should().NotBeNull();
		reviewStep.InfiniteSessions!.Enabled.Should().BeFalse();
	}

	#endregion
}
