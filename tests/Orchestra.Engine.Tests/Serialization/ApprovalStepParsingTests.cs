using FluentAssertions;
using System.Text.Json;

namespace Orchestra.Engine.Tests.Serialization;

/// <summary>
/// Tests for parsing the Approval step type from JSON / YAML orchestration definitions.
/// </summary>
public class ApprovalStepParsingTests
{
	[Fact]
	public void Parses_MinimalApprovalStep()
	{
		var json = """
			{
				"name": "test",
				"description": "test",
				"steps": [
					{
						"name": "review",
						"type": "Approval",
						"prompt": "Approve?"
					}
				]
			}
			""";

		var orchestration = OrchestrationParser.ParseOrchestration(json, []);

		orchestration.Steps.Should().ContainSingle();
		var step = orchestration.Steps[0].Should().BeOfType<ApprovalOrchestrationStep>().Subject;
		step.Name.Should().Be("review");
		step.Type.Should().Be(OrchestrationStepType.Approval);
		step.Prompt.Should().Be("Approve?");
		step.Choices.Should().BeEmpty();
		step.OnTimeout.Should().Be(ApprovalTimeoutBehavior.Fail);
	}

	[Fact]
	public void Parses_ApprovalStep_WithChoices()
	{
		var json = """
			{
				"name": "t",
				"description": "t",
				"steps": [
					{
						"name": "review",
						"type": "Approval",
						"prompt": "Approve deploy?",
						"choices": ["approve", "reject"]
					}
				]
			}
			""";

		var orchestration = OrchestrationParser.ParseOrchestration(json, []);

		var step = (ApprovalOrchestrationStep)orchestration.Steps[0];
		step.Choices.Should().Equal("approve", "reject");
	}

	[Fact]
	public void Parses_OnTimeoutFail()
	{
		var json = """
			{
				"name": "t", "description": "t",
				"steps": [{
					"name": "review",
					"type": "Approval",
					"prompt": "?",
					"timeoutSeconds": 60,
					"onTimeout": "fail"
				}]
			}
			""";

		var orchestration = OrchestrationParser.ParseOrchestration(json, []);

		var step = (ApprovalOrchestrationStep)orchestration.Steps[0];
		step.OnTimeout.Should().Be(ApprovalTimeoutBehavior.Fail);
		step.TimeoutSeconds.Should().Be(60);
	}

	[Fact]
	public void Parses_OnTimeoutDefaultResponse()
	{
		var json = """
			{
				"name": "t", "description": "t",
				"steps": [{
					"name": "review",
					"type": "Approval",
					"prompt": "?",
					"onTimeout": "defaultResponse",
					"defaultResponse": "reject"
				}]
			}
			""";

		var orchestration = OrchestrationParser.ParseOrchestration(json, []);

		var step = (ApprovalOrchestrationStep)orchestration.Steps[0];
		step.OnTimeout.Should().Be(ApprovalTimeoutBehavior.DefaultResponse);
		step.DefaultResponse.Should().Be("reject");
	}

	[Fact]
	public void Parses_OnTimeoutCancel()
	{
		var json = """
			{
				"name": "t", "description": "t",
				"steps": [{
					"name": "review",
					"type": "Approval",
					"prompt": "?",
					"onTimeout": "cancel"
				}]
			}
			""";

		var orchestration = OrchestrationParser.ParseOrchestration(json, []);

		var step = (ApprovalOrchestrationStep)orchestration.Steps[0];
		step.OnTimeout.Should().Be(ApprovalTimeoutBehavior.Cancel);
	}

	[Fact]
	public void Throws_WhenDefaultResponseMissingForDefaultResponseTimeout()
	{
		var json = """
			{
				"name": "t", "description": "t",
				"steps": [{
					"name": "review",
					"type": "Approval",
					"prompt": "?",
					"onTimeout": "defaultResponse"
				}]
			}
			""";

		var act = () => OrchestrationParser.ParseOrchestration(json, []);

		act.Should().Throw<JsonException>().WithMessage("*defaultResponse*");
	}

	[Fact]
	public void Throws_WhenPromptMissing()
	{
		var json = """
			{
				"name": "t", "description": "t",
				"steps": [{
					"name": "review",
					"type": "Approval"
				}]
			}
			""";

		var act = () => OrchestrationParser.ParseOrchestration(json, []);

		act.Should().Throw<JsonException>().WithMessage("*prompt*");
	}

	[Fact]
	public void Throws_OnUnknownTimeoutBehavior()
	{
		var json = """
			{
				"name": "t", "description": "t",
				"steps": [{
					"name": "review",
					"type": "Approval",
					"prompt": "?",
					"onTimeout": "explode"
				}]
			}
			""";

		var act = () => OrchestrationParser.ParseOrchestration(json, []);

		act.Should().Throw<JsonException>().WithMessage("*unknown*onTimeout*");
	}

	[Fact]
	public void Parses_ApprovalStep_WithDependsOn()
	{
		var json = """
			{
				"name": "t", "description": "t",
				"steps": [
					{ "name": "build", "type": "Command", "command": "echo", "arguments": ["ok"] },
					{
						"name": "review",
						"type": "Approval",
						"prompt": "Approve build?",
						"dependsOn": ["build"]
					}
				]
			}
			""";

		var orchestration = OrchestrationParser.ParseOrchestration(json, []);

		var step = (ApprovalOrchestrationStep)orchestration.Steps[1];
		step.DependsOn.Should().Equal("build");
	}

	[Fact]
	public void Parses_PromptStep_WithEnableTools()
	{
		var json = """
			{
				"name": "t", "description": "t",
				"steps": [{
					"name": "writer",
					"type": "Prompt",
					"systemPrompt": "s",
					"userPrompt": "u",
					"model": "claude-opus-4.6",
					"enableTools": ["request_user_input"]
				}]
			}
			""";

		var orchestration = OrchestrationParser.ParseOrchestration(json, []);

		var step = (PromptOrchestrationStep)orchestration.Steps[0];
		step.EnableTools.Should().NotBeNull();
		step.EnableTools.Should().Contain("request_user_input");
	}

	[Fact]
	public void Parses_DefaultEnableToolsAtOrchestrationLevel()
	{
		var json = """
			{
				"name": "t",
				"description": "t",
				"defaultEnableTools": ["request_user_input"],
				"steps": [{
					"name": "writer",
					"type": "Prompt",
					"systemPrompt": "s",
					"userPrompt": "u",
					"model": "claude-opus-4.6"
				}]
			}
			""";

		var orchestration = OrchestrationParser.ParseOrchestration(json, []);

		orchestration.DefaultEnableTools.Should().Contain("request_user_input");
	}

	[Fact]
	public void Parses_PauseTimeoutDuringWait_DefaultsToTrue()
	{
		var json = """
			{
				"name": "t",
				"description": "t",
				"steps": [{
					"name": "s",
					"type": "Approval",
					"prompt": "?"
				}]
			}
			""";

		var orchestration = OrchestrationParser.ParseOrchestration(json, []);

		orchestration.PauseTimeoutDuringWait.Should().BeTrue();
	}

	[Fact]
	public void Parses_PauseTimeoutDuringWait_FalseOverride()
	{
		var json = """
			{
				"name": "t",
				"description": "t",
				"pauseTimeoutDuringWait": false,
				"steps": [{
					"name": "s",
					"type": "Approval",
					"prompt": "?"
				}]
			}
			""";

		var orchestration = OrchestrationParser.ParseOrchestration(json, []);

		orchestration.PauseTimeoutDuringWait.Should().BeFalse();
	}
}
