using System.Text.Json;
using FluentAssertions;
using Orchestra.Engine.Serialization;

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
	public void ParseOrchestration_WithSessionTuningFields_ParsesThem()
	{
		// Arrange
		var json = """
			{
				"name": "tuned",
				"description": "Test",
				"steps": [
					{
						"name": "tuned-step",
						"type": "Prompt",
						"systemPrompt": "S",
						"userPrompt": "U",
						"model": "claude-opus-4.6",
						"reasoningSummary": "concise",
						"contextTier": "longContext",
						"workingDirectory": "C:/work/dir",
						"githubToken": "{{env.GITHUB_TOKEN}}",
						"humanInput": true
					}
				]
			}
			""";

		// Act
		var orchestration = OrchestrationParser.ParseOrchestration(json, []);

		// Assert
		var step = orchestration.Steps[0] as PromptOrchestrationStep;
		step.Should().NotBeNull();
		step!.ReasoningSummary.Should().Be(ReasoningSummaryLevel.Concise);
		step.ContextTier.Should().Be(ContextTier.LongContext);
		step.WorkingDirectory.Should().Be("C:/work/dir");
		step.GitHubToken.Should().Be("{{env.GITHUB_TOKEN}}");
		step.HumanInput.Should().BeTrue();
	}
	[Fact]
	public void ParseOrchestration_WithoutSessionTuningFields_LeavesThemNull()
	{
		// Arrange
		var json = """
			{
				"name": "untuned",
				"description": "Test",
				"steps": [
					{
						"name": "untuned-step",
						"type": "Prompt",
						"systemPrompt": "S",
						"userPrompt": "U",
						"model": "claude-opus-4.6"
					}
				]
			}
			""";

		// Act
		var orchestration = OrchestrationParser.ParseOrchestration(json, []);

		// Assert
		var step = orchestration.Steps[0] as PromptOrchestrationStep;
		step.Should().NotBeNull();
		step!.ReasoningSummary.Should().BeNull();
		step.ContextTier.Should().BeNull();
		step.WorkingDirectory.Should().BeNull();
		step.GitHubToken.Should().BeNull();
		step.HumanInput.Should().BeNull();
	}

	[Fact]
	public void ParseOrchestration_WithPermissionPolicy_ParsesModeAndDeny()
	{
		// Arrange
		var json = """
			{
				"name": "gated",
				"description": "Test",
				"steps": [
					{
						"name": "gated-step",
						"type": "Prompt",
						"systemPrompt": "S",
						"userPrompt": "U",
						"model": "claude-opus-4.6",
						"permissionPolicy": {
							"mode": "denyList",
							"deny": ["shell", "url", "*.env"]
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
		step!.PermissionPolicy.Should().NotBeNull();
		step.PermissionPolicy!.Mode.Should().Be(PermissionMode.DenyList);
		step.PermissionPolicy.Deny.Should().BeEquivalentTo("shell", "url", "*.env");
	}

	[Fact]
	public void ParseOrchestration_WithSandbox_ParsesFilesystemAndNetwork()
	{
		// Arrange
		var json = """
			{
				"name": "boxed",
				"description": "Test",
				"steps": [
					{
						"name": "boxed-step",
						"type": "Prompt",
						"systemPrompt": "S",
						"userPrompt": "U",
						"model": "claude-opus-4.6",
						"sandbox": {
							"enabled": true,
							"filesystem": {
								"readonly": ["/src"],
								"readwrite": ["/tmp/work"],
								"denied": ["/etc/secrets"]
							},
							"network": {
								"allowedHosts": ["api.github.com"],
								"blockedHosts": ["evil.example"],
								"allowOutbound": false,
								"allowLocalNetwork": false
							}
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
		step!.Sandbox.Should().NotBeNull();
		step.Sandbox!.Enabled.Should().BeTrue();
		step.Sandbox.Filesystem!.ReadonlyPaths.Should().BeEquivalentTo("/src");
		step.Sandbox.Filesystem.ReadwritePaths.Should().BeEquivalentTo("/tmp/work");
		step.Sandbox.Filesystem.DeniedPaths.Should().BeEquivalentTo("/etc/secrets");
		step.Sandbox.Network!.AllowedHosts.Should().BeEquivalentTo("api.github.com");
		step.Sandbox.Network.BlockedHosts.Should().BeEquivalentTo("evil.example");
		step.Sandbox.Network.AllowOutbound.Should().BeFalse();
		step.Sandbox.Network.AllowLocalNetwork.Should().BeFalse();
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

	[Fact]
	public void ParseOrchestration_WithAgentPool_ParsesPoolConfig()
	{
		var json = """
			{
				"name": "pooled",
				"description": "Test",
				"agentPool": {
					"minInstances": 2,
					"maxInstances": 6,
					"maxSessionsPerInstance": 1,
					"idleTimeoutSeconds": 30
				},
				"steps": []
			}
			""";

		var orchestration = OrchestrationParser.ParseOrchestration(json, []);

		orchestration.AgentPool.Should().NotBeNull();
		orchestration.AgentPool!.MinInstances.Should().Be(2);
		orchestration.AgentPool.MaxInstances.Should().Be(6);
		orchestration.AgentPool.MaxSessionsPerInstance.Should().Be(1);
		orchestration.AgentPool.IdleTimeoutSeconds.Should().Be(30);
	}

	[Fact]
	public void ParseOrchestration_AgentPoolMaxInstancesZero_ThrowsJsonException()
	{
		var json = """
			{
				"name": "bad-pool",
				"description": "Test",
				"agentPool": {
					"maxInstances": 0
				},
				"steps": []
			}
			""";

		var act = () => OrchestrationParser.ParseOrchestration(json, []);

		act.Should().Throw<System.Text.Json.JsonException>()
			.WithMessage("*agentPool.maxInstances*");
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
		// SDK 1.0.0 added LocalMcp.Environment; the parser should default to null when
		// no 'environment' object is present so unchanged orchestrations behave exactly
		// as they did before (process-inherited environment).
		local.Environment.Should().BeNull();
	}

	[Fact]
	public void ParseMcps_LocalMcp_WithEnvironment_ParsesIntoDictionary()
	{
		// Arrange — the canonical env-injection pattern: API key + per-server NODE_ENV.
		// Values may carry template expressions ({{env.X}}) that resolve at step time.
		var json = """
			{
				"mcps": [
					{
						"name": "openai-tool",
						"type": "local",
						"command": "npx",
						"arguments": ["openai-mcp-server"],
						"environment": {
							"OPENAI_API_KEY": "{{env.OPENAI_API_KEY}}",
							"NODE_ENV": "production"
						}
					}
				]
			}
			""";

		// Act
		var mcps = OrchestrationParser.ParseMcps(json);

		// Assert
		mcps.Should().HaveCount(1);
		var local = mcps[0].Should().BeOfType<LocalMcp>().Subject;
		local.Environment.Should().NotBeNull();
		local.Environment!.Should().HaveCount(2);
		local.Environment!["OPENAI_API_KEY"].Should().Be("{{env.OPENAI_API_KEY}}");
		local.Environment!["NODE_ENV"].Should().Be("production");
	}

	[Fact]
	public void ParseMcps_LocalMcp_EmptyEnvironmentObject_ParsesToNull()
	{
		// Arrange — empty {} should behave the same as omitting the field entirely; the
		// runtime should fall back to the inherited host environment without surfacing
		// an empty dictionary that downstream code might treat differently.
		var json = """
			{
				"mcps": [
					{
						"name": "tool",
						"type": "local",
						"command": "cmd",
						"arguments": [],
						"environment": {}
					}
				]
			}
			""";

		// Act
		var mcps = OrchestrationParser.ParseMcps(json);

		// Assert
		var local = mcps[0].Should().BeOfType<LocalMcp>().Subject;
		local.Environment.Should().BeNull();
	}

	[Fact]
	public void ParseMcps_LocalMcp_EnvironmentWithNonStringValue_Throws()
	{
		// Arrange — only strings are valid (template expressions are also strings); a
		// numeric value is a YAML/JSON authoring mistake we want surfaced immediately.
		var json = """
			{
				"mcps": [
					{
						"name": "tool",
						"type": "local",
						"command": "cmd",
						"arguments": [],
						"environment": {
							"LEVEL": 5
						}
					}
				]
			}
			""";

		// Act
		var act = () => OrchestrationParser.ParseMcps(json);

		// Assert
		act.Should().Throw<JsonException>()
			.WithMessage("*non-string value*'LEVEL'*");
	}

	[Fact]
	public void ParseMcps_LocalMcp_EnvironmentAsArray_Throws()
	{
		// Arrange — a JSON array is the most common shape mistake (authors thinking of
		// it as a list of strings rather than a key/value map). Surface a clear error.
		var json = """
			{
				"mcps": [
					{
						"name": "tool",
						"type": "local",
						"command": "cmd",
						"arguments": [],
						"environment": ["KEY=value"]
					}
				]
			}
			""";

		// Act
		var act = () => OrchestrationParser.ParseMcps(json);

		// Assert
		act.Should().Throw<JsonException>()
			.WithMessage("*invalid 'environment' value*JSON object*");
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
		remote.Timeout.Should().BeNull("timeout is optional");
	}

	[Fact]
	public void ParseMcps_RemoteMcp_TimeoutSeconds_ParsesIntoTimeSpan()
	{
		var json = """
			{
				"mcps": [
					{
						"name": "long-running",
						"type": "remote",
						"endpoint": "https://api.example.com/mcp",
						"timeoutSeconds": 14400
					}
				]
			}
			""";

		var mcps = OrchestrationParser.ParseMcps(json);

		var remote = mcps.OfType<RemoteMcp>().Single();
		remote.Timeout.Should().NotBeNull();
		remote.Timeout!.Value.Should().Be(TimeSpan.FromSeconds(14400));
	}

	[Fact]
	public void ParseMcps_LocalMcp_TimeoutSeconds_ParsesIntoTimeSpan()
	{
		var json = """
			{
				"mcps": [
					{
						"name": "local-tool",
						"type": "local",
						"command": "node",
						"arguments": ["server.js"],
						"timeoutSeconds": 600
					}
				]
			}
			""";

		var mcps = OrchestrationParser.ParseMcps(json);

		var local = mcps.OfType<LocalMcp>().Single();
		local.Timeout.Should().NotBeNull();
		local.Timeout!.Value.Should().Be(TimeSpan.FromMinutes(10));
	}

	/// <summary>
	/// `timeoutSeconds` may also be supplied as a template-string expression that resolves
	/// to an integer count of seconds at step-execution time. In that case the parser
	/// captures the raw template in <see cref="Mcp.TimeoutTemplate"/> and leaves
	/// <see cref="Mcp.Timeout"/> null until the template is resolved by
	/// <see cref="TemplateResolver.ResolveStaticMcp"/>.
	/// </summary>
	[Fact]
	public void ParseMcps_RemoteMcp_TimeoutSecondsAsTemplate_CapturedAsTemplate()
	{
		var json = """
			{
				"mcps": [
					{
						"name": "orchestra",
						"type": "remote",
						"endpoint": "http://localhost:5001/mcp/data",
						"timeoutSeconds": "{{validate-inputs.output.controllerMcpTimeoutSeconds}}"
					}
				]
			}
			""";

		var mcps = OrchestrationParser.ParseMcps(json);

		var remote = mcps.OfType<RemoteMcp>().Single();
		remote.Timeout.Should().BeNull("the template form leaves Timeout unresolved at parse time");
		remote.TimeoutTemplate.Should().Be("{{validate-inputs.output.controllerMcpTimeoutSeconds}}");
	}

	/// <summary>
	/// A param/vars-only template is also valid; it does not require a step context to resolve.
	/// </summary>
	[Fact]
	public void ParseMcps_LocalMcp_TimeoutSecondsAsParamTemplate_CapturedAsTemplate()
	{
		var json = """
			{
				"mcps": [
					{
						"name": "local-tool",
						"type": "local",
						"command": "node",
						"arguments": ["server.js"],
						"timeoutSeconds": "{{param.childTimeoutSeconds}}"
					}
				]
			}
			""";

		var mcps = OrchestrationParser.ParseMcps(json);

		var local = mcps.OfType<LocalMcp>().Single();
		local.Timeout.Should().BeNull();
		local.TimeoutTemplate.Should().Be("{{param.childTimeoutSeconds}}");
	}

	/// <summary>
	/// A string that happens to be a plain integer literal (e.g. <c>"3600"</c>) is treated
	/// as the numeric form for ergonomic parity. The string-form is reserved for actual
	/// template expressions (containing <c>{{</c>).
	/// </summary>
	[Fact]
	public void ParseMcps_RemoteMcp_TimeoutSecondsAsNumericString_ParsedAsNumber()
	{
		var json = """
			{
				"mcps": [
					{
						"name": "orchestra",
						"type": "remote",
						"endpoint": "http://localhost:5001/mcp/data",
						"timeoutSeconds": "3600"
					}
				]
			}
			""";

		var mcps = OrchestrationParser.ParseMcps(json);

		var remote = mcps.OfType<RemoteMcp>().Single();
		remote.Timeout.Should().Be(TimeSpan.FromSeconds(3600));
		remote.TimeoutTemplate.Should().BeNull();
	}

	/// <summary>
	/// A non-positive integer (zero or negative) is treated as "absent" — both legs of the
	/// timeout fields stay null — matching the historical numeric behavior.
	/// </summary>
	[Fact]
	public void ParseMcps_RemoteMcp_TimeoutSecondsZero_IsTreatedAsAbsent()
	{
		var json = """
			{
				"mcps": [
					{
						"name": "orchestra",
						"type": "remote",
						"endpoint": "http://localhost:5001/mcp/data",
						"timeoutSeconds": 0
					}
				]
			}
			""";

		var mcps = OrchestrationParser.ParseMcps(json);

		var remote = mcps.OfType<RemoteMcp>().Single();
		remote.Timeout.Should().BeNull();
		remote.TimeoutTemplate.Should().BeNull();
	}

	/// <summary>
	/// Empty/whitespace string is treated as absent, not as an invalid template — keeps
	/// the field optional under JSON shape variations.
	/// </summary>
	[Fact]
	public void ParseMcps_RemoteMcp_TimeoutSecondsEmptyString_IsTreatedAsAbsent()
	{
		var json = """
			{
				"mcps": [
					{
						"name": "orchestra",
						"type": "remote",
						"endpoint": "http://localhost:5001/mcp/data",
						"timeoutSeconds": "   "
					}
				]
			}
			""";

		var mcps = OrchestrationParser.ParseMcps(json);

		var remote = mcps.OfType<RemoteMcp>().Single();
		remote.Timeout.Should().BeNull();
		remote.TimeoutTemplate.Should().BeNull();
	}

	/// <summary>
	/// A non-string, non-number value for <c>timeoutSeconds</c> is a structural error
	/// that the parser surfaces with a clear diagnostic.
	/// </summary>
	[Fact]
	public void ParseMcps_RemoteMcp_TimeoutSecondsInvalidShape_ThrowsJsonException()
	{
		var json = """
			{
				"mcps": [
					{
						"name": "orchestra",
						"type": "remote",
						"endpoint": "http://localhost:5001/mcp/data",
						"timeoutSeconds": true
					}
				]
			}
			""";

		var act = () => OrchestrationParser.ParseMcps(json);

		act.Should().Throw<System.Text.Json.JsonException>()
			.WithMessage("*orchestra*timeoutSeconds*");
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
	public void ParseOrchestrationFile_WithRelativePaths_ResolvesPromptAssetsAndLocalMcpFromFileDirectory()
	{
		// Arrange
		var tempDir = Path.Combine(Path.GetTempPath(), $"orchestra-relative-paths-{Guid.NewGuid():N}");
		Directory.CreateDirectory(tempDir);
		try
		{
			var promptsDir = Path.Combine(tempDir, "prompts");
			Directory.CreateDirectory(promptsDir);
			File.WriteAllText(Path.Combine(promptsDir, "system.md"), "System from relative prompt");

			var path = Path.Combine(tempDir, "orchestration.json");
			File.WriteAllText(path, """
				{
					"name": "relative-paths",
					"description": "Relative path parsing test",
					"variables": {
						"skillsDir": "./skills",
						"assetsDir": "./assets",
						"mcpDir": "./mcp"
					},
					"mcps": [
						{
							"name": "local-tools",
							"type": "local",
							"command": "node",
							"arguments": ["server.js"],
							"workingDirectory": "{{vars.mcpDir}}"
						}
					],
					"steps": [
						{
							"name": "step1",
							"type": "Prompt",
							"systemPromptFile": "./prompts/system.md",
							"userPrompt": "Test",
							"model": "claude-opus-4.5",
							"skillDirectories": ["{{vars.skillsDir}}"],
							"attachments": [
								{ "type": "file", "path": "{{vars.assetsDir}}/image.png" }
							]
						}
					]
				}
				""");

			// Act
			var orchestration = OrchestrationParser.ParseOrchestrationFile(path, []);

			// Assert
			var step = orchestration.Steps[0].Should().BeOfType<PromptOrchestrationStep>().Subject;
			step.SystemPrompt.Should().Be("System from relative prompt");
			step.SkillDirectories.Should().Contain(Path.GetFullPath(Path.Combine(tempDir, "skills")));

			var attachment = step.Attachments.Single().Should().BeOfType<FileImageAttachment>().Subject;
			attachment.Path.Should().Be(Path.GetFullPath(Path.Combine(tempDir, "assets", "image.png")));

			var mcp = orchestration.Mcps.Single().Should().BeOfType<LocalMcp>().Subject;
			mcp.WorkingDirectory.Should().Be(Path.GetFullPath(Path.Combine(tempDir, "mcp")));
		}
		finally
		{
			if (Directory.Exists(tempDir))
				Directory.Delete(tempDir, recursive: true);
		}
	}

	[Fact]
	public void ParseOrchestrationFile_SetsSourcePathAndDirectory()
	{
		var tempDir = Path.Combine(Path.GetTempPath(), $"orchestra-source-path-{Guid.NewGuid():N}");
		Directory.CreateDirectory(tempDir);
		try
		{
			var path = Path.Combine(tempDir, "orchestration.yaml");
			File.WriteAllText(path, """
				name: source-path-test
				description: Source path test
				steps:
				  - name: report
				    type: Transform
				    template: ok
				""");

			var orchestration = OrchestrationParser.ParseOrchestrationFile(path, []);

			orchestration.SourcePath.Should().Be(Path.GetFullPath(path));
			orchestration.SourceDirectory.Should().Be(Path.GetFullPath(tempDir));
		}
		finally
		{
			if (Directory.Exists(tempDir))
				Directory.Delete(tempDir, recursive: true);
		}
	}

	[Fact]
	public void ParseOrchestrationFile_WithSourcePath_UsesOriginalSourceForSourceMetadata()
	{
		var tempDir = Path.Combine(Path.GetTempPath(), $"orchestra-source-path-{Guid.NewGuid():N}");
		Directory.CreateDirectory(tempDir);
		try
		{
			var managedDir = Path.Combine(tempDir, "managed");
			var sourceDir = Path.Combine(tempDir, "source");
			Directory.CreateDirectory(managedDir);
			Directory.CreateDirectory(sourceDir);
			var managedPath = Path.Combine(managedDir, "managed.yaml");
			var sourcePath = Path.Combine(sourceDir, "original.yaml");
			File.WriteAllText(managedPath, """
				name: source-path-test
				description: Source path test
				steps:
				  - name: report
				    type: Transform
				    template: ok
				""");

			var orchestration = OrchestrationParser.ParseOrchestrationFile(managedPath, sourcePath, []);

			orchestration.SourcePath.Should().Be(Path.GetFullPath(sourcePath));
			orchestration.SourceDirectory.Should().Be(Path.GetFullPath(sourceDir));
		}
		finally
		{
			if (Directory.Exists(tempDir))
				Directory.Delete(tempDir, recursive: true);
		}
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

	#region Metadata Parsing

	[Fact]
	public void ParseOrchestration_WithMetadata_ParsesAllValueTypes()
	{
		// Arrange
		var json = """
			{
				"name": "metadata-test",
				"description": "Test with metadata",
				"metadata": {
					"datetime": "2026-04-30T12:00:00Z",
					"author": "alice",
					"priority": 3,
					"production": true,
					"owners": ["alice", "bob"],
					"links": {
						"ticket": "JIRA-123",
						"runbook": "https://example.com/runbook"
					}
				},
				"steps": []
			}
			""";

		// Act
		var orchestration = OrchestrationParser.ParseOrchestration(json, []);

		// Assert
		orchestration.Metadata.Should().HaveCount(6);
		orchestration.Metadata["datetime"]!.GetValue<string>().Should().Be("2026-04-30T12:00:00Z");
		orchestration.Metadata["author"]!.GetValue<string>().Should().Be("alice");
		orchestration.Metadata["priority"]!.GetValue<int>().Should().Be(3);
		orchestration.Metadata["production"]!.GetValue<bool>().Should().BeTrue();
		orchestration.Metadata["owners"]!.AsArray().Should().HaveCount(2);
		orchestration.Metadata["owners"]![0]!.GetValue<string>().Should().Be("alice");
		orchestration.Metadata["links"]!["ticket"]!.GetValue<string>().Should().Be("JIRA-123");
	}

	[Fact]
	public void ParseOrchestration_WithoutMetadata_DefaultsToEmptyDictionary()
	{
		// Arrange
		var json = """
			{
				"name": "no-metadata",
				"description": "Test without metadata",
				"steps": []
			}
			""";

		// Act
		var orchestration = OrchestrationParser.ParseOrchestration(json, []);

		// Assert
		orchestration.Metadata.Should().NotBeNull();
		orchestration.Metadata.Should().BeEmpty();
	}

	[Fact]
	public void ParseOrchestration_WithMetadata_DoesNotAffectExecution()
	{
		// Arrange - metadata should be ignored entirely by the runtime;
		// the rest of the orchestration must parse normally even when metadata is present.
		var json = """
			{
				"name": "metadata-and-steps",
				"description": "Metadata coexists with normal fields",
				"metadata": {
					"createdAt": "2026-04-30T08:00:00Z",
					"team": "platform"
				},
				"steps": [
					{
						"name": "step1",
						"type": "prompt",
						"dependsOn": [],
						"systemPrompt": "Test",
						"userPrompt": "Test",
						"model": "claude-opus-4.6"
					}
				]
			}
			""";

		// Act
		var orchestration = OrchestrationParser.ParseOrchestration(json, []);

		// Assert
		orchestration.Name.Should().Be("metadata-and-steps");
		orchestration.Steps.Should().HaveCount(1);
		orchestration.Steps[0].Name.Should().Be("step1");
		orchestration.Metadata["team"]!.GetValue<string>().Should().Be("platform");
	}

	[Fact]
	public void ParseOrchestration_WithMetadata_RoundTripsThroughSerialization()
	{
		// Arrange
		var json = """
			{
				"name": "roundtrip",
				"description": "Round-trip metadata",
				"metadata": {
					"datetime": "2026-04-30T12:00:00Z",
					"nested": { "key": "value", "count": 7 }
				},
				"steps": []
			}
			""";

		// Act - parse, serialize the metadata dictionary as JSON, then reconstruct
		// an orchestration JSON and re-parse to confirm structure survives round-trip.
		var first = OrchestrationParser.ParseOrchestration(json, []);
		var metadataJson = System.Text.Json.JsonSerializer.Serialize(first.Metadata);
		var rebuilt = $$"""
			{
				"name": "roundtrip",
				"description": "Round-trip metadata",
				"metadata": {{metadataJson}},
				"steps": []
			}
			""";
		var second = OrchestrationParser.ParseOrchestration(rebuilt, []);

		// Assert - metadata survives the round-trip with structure intact
		second.Metadata["datetime"]!.GetValue<string>().Should().Be("2026-04-30T12:00:00Z");
		second.Metadata["nested"]!["key"]!.GetValue<string>().Should().Be("value");
		second.Metadata["nested"]!["count"]!.GetValue<int>().Should().Be(7);
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

	#region FailOnToolError Parsing

	[Fact]
	public void ParseOrchestration_StepWithFailOnToolErrorTrue_ParsesAsTrue()
	{
		// Arrange
		var json = """
			{
				"name": "fail-on-tool-error-test",
				"description": "Test",
				"steps": [
					{
						"name": "step1",
						"type": "prompt",
						"systemPrompt": "s",
						"userPrompt": "u",
						"model": "claude-opus-4.6",
						"failOnToolError": true
					}
				]
			}
			""";

		// Act
		var orchestration = OrchestrationParser.ParseOrchestration(json, []);

		// Assert
		orchestration.Steps[0].FailOnToolError.Should().BeTrue();
	}

	[Fact]
	public void ParseOrchestration_StepWithFailOnToolErrorFalse_ParsesAsFalse()
	{
		// Arrange — explicit false is distinct from "unset" and must override an
		// orchestration-level DefaultFailOnToolError=true when wired through later.
		var json = """
			{
				"name": "fail-on-tool-error-test",
				"description": "Test",
				"steps": [
					{
						"name": "step1",
						"type": "prompt",
						"systemPrompt": "s",
						"userPrompt": "u",
						"model": "claude-opus-4.6",
						"failOnToolError": false
					}
				]
			}
			""";

		// Act
		var orchestration = OrchestrationParser.ParseOrchestration(json, []);

		// Assert
		orchestration.Steps[0].FailOnToolError.Should().BeFalse();
	}

	[Fact]
	public void ParseOrchestration_StepWithoutFailOnToolError_DefaultsToNull()
	{
		// Arrange — null preserves the "inherit from orchestration default" semantics
		// (see OrchestrationStep.FailOnToolError docstring).
		var json = """
			{
				"name": "no-fail-on-tool-error-test",
				"description": "Test",
				"steps": [
					{
						"name": "step1",
						"type": "prompt",
						"systemPrompt": "s",
						"userPrompt": "u",
						"model": "claude-opus-4.6"
					}
				]
			}
			""";

		// Act
		var orchestration = OrchestrationParser.ParseOrchestration(json, []);

		// Assert
		orchestration.Steps[0].FailOnToolError.Should().BeNull();
	}

	[Fact]
	public void ParseOrchestration_DefaultFailOnToolErrorTrue_ParsesAtOrchestrationLevel()
	{
		// Arrange — orchestration-level default that individual steps inherit unless
		// they specify their own value.
		var json = """
			{
				"name": "default-fail-on-tool-error-test",
				"description": "Test",
				"defaultFailOnToolError": true,
				"steps": [
					{
						"name": "step1",
						"type": "prompt",
						"systemPrompt": "s",
						"userPrompt": "u",
						"model": "claude-opus-4.6"
					}
				]
			}
			""";

		// Act
		var orchestration = OrchestrationParser.ParseOrchestration(json, []);

		// Assert
		orchestration.DefaultFailOnToolError.Should().BeTrue();
		// Step-level remains null (inherit). The executor resolves the effective
		// value via `step.FailOnToolError ?? context.DefaultFailOnToolError`.
		orchestration.Steps[0].FailOnToolError.Should().BeNull();
	}

	[Fact]
	public void ParseOrchestration_WithoutDefaultFailOnToolError_DefaultsToFalse()
	{
		// Arrange — backward compatibility: existing orchestrations that don't set
		// DefaultFailOnToolError get the historical behavior (tool failures non-fatal).
		var json = """
			{
				"name": "no-default-test",
				"description": "Test",
				"steps": [
					{
						"name": "step1",
						"type": "prompt",
						"systemPrompt": "s",
						"userPrompt": "u",
						"model": "claude-opus-4.6"
					}
				]
			}
			""";

		// Act
		var orchestration = OrchestrationParser.ParseOrchestration(json, []);

		// Assert
		orchestration.DefaultFailOnToolError.Should().BeFalse();
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

	/// <summary>
	/// Locks in the contract demonstrated by the mcp-coordinator-with-explicit-timeout
	/// example: the per-server <c>timeoutSeconds</c> on an Orchestra data-plane MCP
	/// entry parses into <see cref="Mcp.Timeout"/> exactly as authored. This guards
	/// against regressions where the example would silently lose its override and
	/// fall back to the host default.
	/// </summary>
	[Fact]
	public void ParseOrchestrationFile_McpCoordinatorWithExplicitTimeoutExample_PreservesPerServerTimeout()
	{
		var path = Path.GetFullPath(Path.Combine(
			AppContext.BaseDirectory, "..", "..", "..", "..", "..",
			"examples", "mcp-coordinator-with-explicit-timeout.yaml"));

		var orchestration = OrchestrationParser.ParseOrchestrationFile(path, []);

		var orchestraMcp = orchestration.Mcps.OfType<RemoteMcp>()
			.Single(m => string.Equals(m.Name, "orchestra", StringComparison.OrdinalIgnoreCase));
		orchestraMcp.Timeout.Should().Be(TimeSpan.FromSeconds(5400),
			"the example explicitly demonstrates a 90-minute per-server override");

		// The companion filesystem MCP demonstrates the 'no override' case.
		var fsMcp = orchestration.Mcps.OfType<LocalMcp>()
			.Single(m => string.Equals(m.Name, "filesystem", StringComparison.OrdinalIgnoreCase));
		fsMcp.Timeout.Should().BeNull(
			"the example deliberately leaves the local filesystem MCP at the SDK default");
	}

	/// <summary>
	/// Locks in the contract demonstrated by the mcp-per-server-timeouts example:
	/// each MCP can have its own deadline, and entries that omit timeoutSeconds
	/// remain unset at parse time (the host's data-plane default is applied later
	/// by McpManager.Resolve, not at parse time).
	/// </summary>
	[Fact]
	public void ParseOrchestrationFile_McpPerServerTimeoutsExample_EachServerHasDistinctTimeout()
	{
		var path = Path.GetFullPath(Path.Combine(
			AppContext.BaseDirectory, "..", "..", "..", "..", "..",
			"examples", "mcp-per-server-timeouts.yaml"));

		var orchestration = OrchestrationParser.ParseOrchestrationFile(path, []);

		var orchestra = orchestration.Mcps.OfType<RemoteMcp>().Single(m => m.Name == "orchestra");
		var db = orchestration.Mcps.OfType<RemoteMcp>().Single(m => m.Name == "db");
		var fs = orchestration.Mcps.OfType<LocalMcp>().Single(m => m.Name == "filesystem");
		var analyzer = orchestration.Mcps.OfType<LocalMcp>().Single(m => m.Name == "code-analyzer");

		orchestra.Timeout.Should().Be(TimeSpan.FromSeconds(7200), "2-hour deep-tree override");
		db.Timeout.Should().Be(TimeSpan.FromSeconds(30), "30-second fail-fast cap");
		fs.Timeout.Should().BeNull("filesystem entry deliberately uses the SDK default");
		analyzer.Timeout.Should().Be(TimeSpan.FromSeconds(900), "15-minute analyzer override");
	}

	/// <summary>
	/// Locks in the contract demonstrated by mcp-coordinator-with-default-timeout:
	/// the data-plane MCP entry intentionally OMITS timeoutSeconds so that the
	/// host's DefaultOrchestraInvokeTimeoutSeconds applies at runtime. If a future
	/// edit silently adds a per-orchestration override, this test will fail and
	/// alert the author that the example's narrative no longer matches its config.
	/// </summary>
	[Fact]
	public void ParseOrchestrationFile_McpCoordinatorWithDefaultTimeoutExample_OmitsPerServerTimeout()
	{
		var path = Path.GetFullPath(Path.Combine(
			AppContext.BaseDirectory, "..", "..", "..", "..", "..",
			"examples", "mcp-coordinator-with-default-timeout.yaml"));

		var orchestration = OrchestrationParser.ParseOrchestrationFile(path, []);

		var orchestraMcp = orchestration.Mcps.OfType<RemoteMcp>()
			.Single(m => string.Equals(m.Name, "orchestra", StringComparison.OrdinalIgnoreCase));
		orchestraMcp.Timeout.Should().BeNull(
			"this example demonstrates the host-level default; an explicit timeoutSeconds " +
			"would defeat the example's purpose");
		orchestraMcp.Endpoint.Should().Contain("/mcp/data",
			"the example targets Orchestra's data-plane route, which is what triggers the default");
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
					"releaseNotes": {
						"type": "string",
						"description": "Multiline release notes",
						"required": false,
						"multiline": true
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
		orchestration.Inputs.Should().HaveCount(5);

		orchestration.Inputs!["serviceName"].Type.Should().Be(InputType.String);
		orchestration.Inputs["serviceName"].Description.Should().Be("Name of the service to deploy");
		orchestration.Inputs["serviceName"].Required.Should().BeTrue();

		orchestration.Inputs["environment"].Type.Should().Be(InputType.String);
		orchestration.Inputs["environment"].Enum.Should().BeEquivalentTo("staging", "production");

		orchestration.Inputs["dryRun"].Type.Should().Be(InputType.Boolean);
		orchestration.Inputs["dryRun"].Required.Should().BeFalse();
		orchestration.Inputs["dryRun"].Default.Should().Be("false");
		orchestration.Inputs["releaseNotes"].Type.Should().Be(InputType.String);
		orchestration.Inputs["releaseNotes"].Multiline.Should().BeTrue();

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
	public void ConvertYamlToJson_WithMetadata_ParsesAllValueTypes()
	{
		var yaml = """
			name: yaml-metadata-test
			description: Test metadata in YAML
			metadata:
			  datetime: "2026-04-30T12:00:00Z"
			  author: alice
			  priority: 3
			  production: true
			  owners:
			    - alice
			    - bob
			  links:
			    ticket: JIRA-123
			    runbook: https://example.com/runbook
			steps: []
			""";

		var json = OrchestrationParser.ConvertYamlToJson(yaml);
		var orchestration = OrchestrationParser.ParseOrchestration(json, []);

		orchestration.Metadata.Should().HaveCount(6);
		orchestration.Metadata["datetime"]!.GetValue<string>().Should().Be("2026-04-30T12:00:00Z");
		orchestration.Metadata["author"]!.GetValue<string>().Should().Be("alice");
		orchestration.Metadata["priority"]!.GetValue<int>().Should().Be(3);
		orchestration.Metadata["production"]!.GetValue<bool>().Should().BeTrue();
		orchestration.Metadata["owners"]!.AsArray().Should().HaveCount(2);
		orchestration.Metadata["owners"]![0]!.GetValue<string>().Should().Be("alice");
		orchestration.Metadata["links"]!["ticket"]!.GetValue<string>().Should().Be("JIRA-123");
	}

	[Fact]
	public void ConvertYamlToJson_WithoutMetadata_DefaultsToEmptyDictionary()
	{
		var yaml = """
			name: yaml-no-metadata
			description: Test without metadata
			steps: []
			""";

		var json = OrchestrationParser.ConvertYamlToJson(yaml);
		var orchestration = OrchestrationParser.ParseOrchestration(json, []);

		orchestration.Metadata.Should().NotBeNull();
		orchestration.Metadata.Should().BeEmpty();
	}

	[Fact]
	public void ParseOrchestrationFile_VariablesAndMetadataYamlExample_ParsesCorrectly()
	{
		// Arrange - load the bundled YAML example so it stays valid going forward.
		var examplesDir = Path.GetFullPath(
			Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "examples"));
		var filePath = Path.Combine(examplesDir, "variables-and-metadata.yaml");

		// Act
		var orchestration = OrchestrationParser.ParseOrchestrationFile(filePath, []);

		// Assert - core structure
		orchestration.Name.Should().Be("variables-and-metadata");
		orchestration.Steps.Should().HaveCount(3);
		orchestration.Variables.Should().ContainKey("baseUrl");

		// Assert - metadata round-trips with mixed value types intact
		orchestration.Metadata.Should().HaveCount(6);
		orchestration.Metadata["createdAt"]!.GetValue<string>().Should().Be("2026-04-30T12:00:00Z");
		orchestration.Metadata["author"]!.GetValue<string>().Should().Be("platform-team");
		orchestration.Metadata["ticket"]!.GetValue<string>().Should().Be("JIRA-1234");
		orchestration.Metadata["environment"]!.GetValue<string>().Should().Be("staging");
		orchestration.Metadata["owners"]!.AsArray().Should().HaveCount(2);
		orchestration.Metadata["owners"]![0]!.GetValue<string>().Should().Be("alice@example.com");
		orchestration.Metadata["sla"]!["responseTimeMinutes"]!.GetValue<int>().Should().Be(15);
		orchestration.Metadata["sla"]!["businessHoursOnly"]!.GetValue<bool>().Should().BeTrue();
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

	[Fact]
	public void ParseOrchestrationFile_YamlWithEscapedTemplateExpressions_ParsesValidatesAndPreservesBackslash()
	{
		// Arrange — end-to-end coverage of the escape pipeline: YAML literal
		// block scalars preserve backslashes, the JSON converter retains them
		// across the YAML→JSON boundary, the validator skips escaped expressions,
		// and the resolver consumes the backslash at runtime. This exact shape
		// (a Script step whose body contains a documentation reference to its
		// OWN output) is the regression scenario from `pr-auto-reviewer.yaml`.
		var yaml = """
			name: escape-syntax-e2e
			description: Verifies that \{{...}} escapes survive YAML parsing and validation.
			steps:
			  - name: fetch-data
			    type: Script
			    shell: pwsh
			    script: |
			      # The downstream `transform` step consumes \{{fetch-data.output}}
			      # as a JSON document. Keep the contract documented here.
			      Write-Output '{"id":1}'
			  - name: transform
			    type: Prompt
			    dependsOn: [fetch-data]
			    systemPrompt: |
			      Read the JSON in \{{fetch-data.output}} and produce a summary.
			      Use the literal placeholder \{{param.style}} when the user did
			      not supply a style preference.
			    userPrompt: |
			      Real reference (resolves at runtime): {{fetch-data.output}}
			    model: claude-opus-4.6
			""";

		var tempDir = Path.Combine(Path.GetTempPath(), $"orchestra-escape-e2e-{Guid.NewGuid():N}");
		Directory.CreateDirectory(tempDir);
		var filePath = Path.Combine(tempDir, "escape-syntax.yaml");

		try
		{
			File.WriteAllText(filePath, yaml);

			// Act 1 — parse the YAML through the real pipeline.
			var orchestration = OrchestrationParser.ParseOrchestrationFile(filePath, []);

			// Assert 1 — the backslash escape survives YAML→JSON→object deserialization.
			var scriptStep = orchestration.Steps[0].Should().BeOfType<ScriptOrchestrationStep>().Subject;
			scriptStep.Script.Should().Contain(@"\{{fetch-data.output}}",
				because: "the runtime resolver needs to see the backslash to know this is an escape");

			var promptStep = orchestration.Steps[1].Should().BeOfType<PromptOrchestrationStep>().Subject;
			promptStep.SystemPrompt.Should().Contain(@"\{{fetch-data.output}}");
			promptStep.SystemPrompt.Should().Contain(@"\{{param.style}}");
			// The unescaped reference in the userPrompt is NOT prefixed with a backslash —
			// it is a real cross-step reference that the resolver will substitute.
			promptStep.UserPrompt.Should().Contain("{{fetch-data.output}}");
			promptStep.UserPrompt.Should().NotContain(@"\{{fetch-data.output}}");

			// Act 2 — run full validation.
			var validation = TemplateExpressionValidator.ValidateOrchestration(orchestration);

			// Assert 2 — the escaped self-reference in the script body and the
			// escaped placeholders in the system prompt do NOT produce errors.
			// (Without the escape, the script body wouldn't be validated since
			// ScriptOrchestrationStep is not in GetStepFields, but the prompt
			// step's `\{{param.style}}` would otherwise fail "undeclared param"
			// validation, and `\{{fetch-data.output}}` would otherwise pass
			// reachability only because `transform` depends on `fetch-data`.)
			validation.IsValid.Should().BeTrue(validation.FormatErrors());
		}
		finally
		{
			Directory.Delete(tempDir, recursive: true);
		}
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

	#region ParseMcpFile env-var expansion

	[Fact]
	public void ParseMcpFile_LocalMcp_ExpandsDollarBraceInArgumentsAndEnvironment()
	{
		// Arrange — the canonical failure case that motivated this feature:
		// a stdio MCP whose `arguments[]` and `environment{}` contain ${VAR} refs.
		// Without expansion, the literal `${ORCHESTRA_TEST_TENANT}` would leak
		// into the spawned child process command line.
		const string tenantVar = "ORCHESTRA_TEST_TENANT_AAA";
		const string clientVar = "ORCHESTRA_TEST_CLIENT_AAA";
		const string audienceVar = "ORCHESTRA_TEST_AUDIENCE_AAA";
		var saved = SnapshotAndSet(new()
		{
			[tenantVar] = "72f988bf-86f1-41af-91ab-2d7cd011db47",
			[clientVar] = "aebc6443-996d-45c2-90f0-388ff96faa56",
			[audienceVar] = "ea9ffc3e-8a23-4a7d-836d-234d7c7565c1",
		});

		var tempFile = Path.Combine(Path.GetTempPath(), $"orchestra-mcp-test-{Guid.NewGuid():N}.json");
		try
		{
			var json = $$"""
				{
					"mcps": [
						{
							"name": "teams",
							"type": "local",
							"command": "dnx",
							"arguments": [
								"McpProxy.Samples.Teams.Mcp", "--yes", "--",
								"--tenant-id=${{{tenantVar}}}",
								"--public-client-id=${{{clientVar}}}"
							],
							"environment": {
								"AZURE_SCOPES": "${{{audienceVar}}}/.default"
							}
						}
					]
				}
				""";
			File.WriteAllText(tempFile, json);

			// Act
			var mcps = OrchestrationParser.ParseMcpFile(tempFile);

			// Assert — every reference is resolved to the actual GUID before
			// the LocalMcp instance is constructed.
			mcps.Should().HaveCount(1);
			var local = mcps[0].Should().BeOfType<LocalMcp>().Subject;
			local.Arguments.Should().BeEquivalentTo(
				"McpProxy.Samples.Teams.Mcp", "--yes", "--",
				"--tenant-id=72f988bf-86f1-41af-91ab-2d7cd011db47",
				"--public-client-id=aebc6443-996d-45c2-90f0-388ff96faa56");
			local.Environment.Should().NotBeNull();
			local.Environment!["AZURE_SCOPES"].Should().Be("ea9ffc3e-8a23-4a7d-836d-234d7c7565c1/.default");
		}
		finally
		{
			File.Delete(tempFile);
			RestoreEnv(saved);
		}
	}

	[Fact]
	public void ParseMcpFile_RemoteMcp_ExpandsEnvColonValue()
	{
		// Arrange — "env:VAR" whole-string indirection on a remote endpoint.
		const string endpointVar = "ORCHESTRA_TEST_ENDPOINT_AAA";
		var saved = SnapshotAndSet(new()
		{
			[endpointVar] = "https://api.example.com/mcp/data",
		});

		var tempFile = Path.Combine(Path.GetTempPath(), $"orchestra-mcp-test-{Guid.NewGuid():N}.json");
		try
		{
			var json = $$"""
				{
					"mcps": [
						{
							"name": "remote-tool",
							"type": "remote",
							"endpoint": "env:{{endpointVar}}"
						}
					]
				}
				""";
			File.WriteAllText(tempFile, json);

			// Act
			var mcps = OrchestrationParser.ParseMcpFile(tempFile);

			// Assert — the indirection becomes the actual URL.
			mcps.Should().HaveCount(1);
			var remote = mcps[0].Should().BeOfType<RemoteMcp>().Subject;
			remote.Endpoint.Should().Be("https://api.example.com/mcp/data");
		}
		finally
		{
			File.Delete(tempFile);
			RestoreEnv(saved);
		}
	}

	[Fact]
	public void ParseMcpFile_MissingEnvVar_ThrowsWithPathAndVariableName()
	{
		// Arrange — variable intentionally absent. The exception must name
		// both the variable and the source file so operators can fix it.
		const string varName = "ORCHESTRA_TEST_MISSING_AAA";
		var saved = SnapshotAndSet(new() { [varName] = null });

		var tempFile = Path.Combine(Path.GetTempPath(), $"orchestra-mcp-test-{Guid.NewGuid():N}.json");
		try
		{
			var json = $$"""
				{
					"mcps": [
						{
							"name": "broken",
							"type": "local",
							"command": "dnx",
							"arguments": ["--tenant-id=${{{varName}}}"]
						}
					]
				}
				""";
			File.WriteAllText(tempFile, json);

			// Act
			var act = () => OrchestrationParser.ParseMcpFile(tempFile);

			// Assert
			var ex = act.Should().Throw<EnvironmentVariableExpansionException>().Which;
			ex.VariableName.Should().Be(varName);
			ex.SourcePath.Should().Be(tempFile);
			ex.Syntax.Should().Be("${VAR}");
		}
		finally
		{
			File.Delete(tempFile);
			RestoreEnv(saved);
		}
	}

	/// <summary>
	/// Snapshots the current value of each named env var (so we can restore in
	/// the finally block) and applies the requested values. A null value
	/// removes the variable.
	/// </summary>
	private static Dictionary<string, string?> SnapshotAndSet(Dictionary<string, string?> assignments)
	{
		var saved = new Dictionary<string, string?>();
		foreach (var (name, value) in assignments)
		{
			saved[name] = Environment.GetEnvironmentVariable(name);
			Environment.SetEnvironmentVariable(name, value);
		}
		return saved;
	}

	private static void RestoreEnv(Dictionary<string, string?> saved)
	{
		foreach (var (name, value) in saved)
			Environment.SetEnvironmentVariable(name, value);
	}

	#endregion
}
