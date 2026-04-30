using FluentAssertions;
using Xunit;

namespace Orchestra.Engine.Tests.Serialization;

/// <summary>
/// Tests for parsing the new <c>Orchestration</c> step type from JSON.
/// </summary>
public class OrchestrationStepTypeParserTests
{
	private static readonly Mcp[] s_noMcps = [];

	private static Orchestration ParseOrchestration(string stepJson)
	{
		var orchestrationJson = $$"""
		{
			"name": "test",
			"description": "test",
			"version": "1.0.0",
			"steps": [{{stepJson}}]
		}
		""";
		return OrchestrationParser.ParseOrchestration(orchestrationJson, s_noMcps);
	}

	[Fact]
	public void Parses_minimal_orchestration_step()
	{
		var orchestration = ParseOrchestration("""
		{
			"name": "review",
			"type": "Orchestration",
			"orchestration": "pr-code-reviewer"
		}
		""");

		var step = orchestration.Steps.Single().Should().BeOfType<OrchestrationInvocationStep>().Subject;
		step.Name.Should().Be("review");
		step.Type.Should().Be(OrchestrationStepType.Orchestration);
		step.OrchestrationName.Should().Be("pr-code-reviewer");
		step.Mode.Should().Be(OrchestrationInvocationMode.Sync);
		step.ChildParameters.Should().BeEmpty();
		step.InputHandlerPrompt.Should().BeNull();
	}

	[Fact]
	public void Parses_step_with_parameters_and_mode()
	{
		var orchestration = ParseOrchestration("""
		{
			"name": "review",
			"type": "Orchestration",
			"orchestration": "pr-code-reviewer",
			"mode": "async",
			"parameters": {
				"prData": "{{fetch.output}}",
				"reviewer": "claude"
			}
		}
		""");

		var step = (OrchestrationInvocationStep)orchestration.Steps.Single();
		step.Mode.Should().Be(OrchestrationInvocationMode.Async);
		step.ChildParameters.Should().HaveCount(2);
		step.ChildParameters["prData"].Should().Be("{{fetch.output}}");
		step.ChildParameters["reviewer"].Should().Be("claude");
	}

	[Fact]
	public void Parses_input_handler_prompt_and_model()
	{
		var orchestration = ParseOrchestration("""
		{
			"name": "review",
			"type": "Orchestration",
			"orchestration": "pr-code-reviewer",
			"inputHandlerPrompt": "Reshape the parameters",
			"inputHandlerModel": "claude-opus-4.6"
		}
		""");

		var step = (OrchestrationInvocationStep)orchestration.Steps.Single();
		step.InputHandlerPrompt.Should().Be("Reshape the parameters");
		step.InputHandlerModel.Should().Be("claude-opus-4.6");
	}

	[Fact]
	public void Parses_dependsOn_and_timeoutSeconds()
	{
		var orchestration = ParseOrchestration("""
		{
			"name": "review",
			"type": "Orchestration",
			"orchestration": "child",
			"dependsOn": ["fetch", "validate"],
			"timeoutSeconds": 14400
		}
		""");

		var step = (OrchestrationInvocationStep)orchestration.Steps.Single();
		step.DependsOn.Should().ContainInOrder("fetch", "validate");
		step.TimeoutSeconds.Should().Be(14400);
	}

	[Fact]
	public void Throws_when_orchestration_field_missing()
	{
		var act = () => ParseOrchestration("""
		{
			"name": "review",
			"type": "Orchestration"
		}
		""");

		act.Should().Throw<System.Text.Json.JsonException>()
			.WithMessage("*requires*orchestration*");
	}

	[Fact]
	public void Throws_when_orchestration_field_blank()
	{
		var act = () => ParseOrchestration("""
		{
			"name": "review",
			"type": "Orchestration",
			"orchestration": "   "
		}
		""");

		act.Should().Throw<System.Text.Json.JsonException>()
			.WithMessage("*orchestration*");
	}

	[Fact]
	public void Throws_when_mode_invalid()
	{
		var act = () => ParseOrchestration("""
		{
			"name": "review",
			"type": "Orchestration",
			"orchestration": "child",
			"mode": "fire-and-forget"
		}
		""");

		act.Should().Throw<System.Text.Json.JsonException>()
			.WithMessage("*sync*async*");
	}

	[Fact]
	public void Templated_orchestration_name_is_preserved_for_runtime_resolution()
	{
		// The parser should NOT try to resolve the template at parse time; that's done at
		// step-execution time so the orchestration ID can be selected dynamically.
		var orchestration = ParseOrchestration("""
		{
			"name": "review",
			"type": "Orchestration",
			"orchestration": "{{decide-target.output}}"
		}
		""");

		var step = (OrchestrationInvocationStep)orchestration.Steps.Single();
		step.OrchestrationName.Should().Be("{{decide-target.output}}");
	}
}
