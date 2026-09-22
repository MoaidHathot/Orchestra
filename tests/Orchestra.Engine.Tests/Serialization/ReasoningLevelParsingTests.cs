using FluentAssertions;

namespace Orchestra.Engine.Tests.Serialization;

/// <summary>
/// Parsing coverage for the reasoning-effort surface: the five <see cref="ReasoningLevel"/>
/// tiers on a Prompt step (all accepted spellings) and the orchestration-level
/// <c>defaultReasoningLevel</c> that mirrors <c>defaultModel</c>.
/// </summary>
public class ReasoningLevelParsingTests
{
	private static string StepJson(string reasoningLevelLiteral) => $$"""
		{
			"name": "reasoning-step",
			"description": "step-level reasoningLevel",
			"steps": [
				{
					"name": "think",
					"type": "prompt",
					"systemPrompt": "You are a test assistant.",
					"userPrompt": "Think hard.",
					"model": "gpt-6-astra",
					"reasoningLevel": {{reasoningLevelLiteral}}
				}
			]
		}
		""";

	[Theory]
	[InlineData("\"low\"", ReasoningLevel.Low)]
	[InlineData("\"medium\"", ReasoningLevel.Medium)]
	[InlineData("\"high\"", ReasoningLevel.High)]
	[InlineData("\"xhigh\"", ReasoningLevel.XHigh)]
	[InlineData("\"max\"", ReasoningLevel.Max)]
	[InlineData("\"Low\"", ReasoningLevel.Low)]
	[InlineData("\"High\"", ReasoningLevel.High)]
	[InlineData("\"XHigh\"", ReasoningLevel.XHigh)]
	[InlineData("\"xHigh\"", ReasoningLevel.XHigh)]
	[InlineData("\"Max\"", ReasoningLevel.Max)]
	[InlineData("\"MAX\"", ReasoningLevel.Max)]
	public void ParseOrchestration_StepReasoningLevel_AcceptsEveryTierCaseInsensitively(string literal, ReasoningLevel expected)
	{
		var orchestration = OrchestrationParser.ParseOrchestration(StepJson(literal), []);

		var step = orchestration.Steps[0].Should().BeOfType<PromptOrchestrationStep>().Subject;
		step.ReasoningLevel.Should().Be(expected);
	}

	[Fact]
	public void ParseOrchestration_StepReasoningLevel_RejectsUnknownTier()
	{
		var act = () => OrchestrationParser.ParseOrchestration(StepJson("\"ultra\""), []);

		act.Should().Throw<Exception>("an unknown effort tier must fail at load time rather than be silently forwarded to the provider");
	}

	[Theory]
	[InlineData("\"low\"", ReasoningLevel.Low)]
	[InlineData("\"medium\"", ReasoningLevel.Medium)]
	[InlineData("\"high\"", ReasoningLevel.High)]
	[InlineData("\"xhigh\"", ReasoningLevel.XHigh)]
	[InlineData("\"XHigh\"", ReasoningLevel.XHigh)]
	[InlineData("\"xHigh\"", ReasoningLevel.XHigh)]
	[InlineData("\"max\"", ReasoningLevel.Max)]
	[InlineData("\"Max\"", ReasoningLevel.Max)]
	public void ParseOrchestration_DefaultReasoningLevel_ParsesEveryTier(string literal, ReasoningLevel expected)
	{
		var json = $$"""
			{
				"name": "default-reasoning",
				"description": "orchestration-level defaultReasoningLevel",
				"defaultModel": "gpt-6-astra",
				"defaultReasoningLevel": {{literal}},
				"steps": [
					{
						"name": "step1",
						"type": "prompt",
						"systemPrompt": "You are a test assistant.",
						"userPrompt": "Test prompt"
					}
				]
			}
			""";

		var orchestration = OrchestrationParser.ParseOrchestration(json, []);

		orchestration.DefaultReasoningLevel.Should().Be(expected);
		// The orchestration default must not be copied onto the step at parse time; the
		// executor resolves it per step so a step-level override keeps winning.
		orchestration.Steps[0].Should().BeOfType<PromptOrchestrationStep>().Subject.ReasoningLevel.Should().BeNull();
	}

	[Fact]
	public void ParseOrchestration_WithoutDefaultReasoningLevel_DefaultsToNull()
	{
		var json = """
			{
				"name": "no-default-reasoning",
				"description": "absent defaultReasoningLevel",
				"defaultModel": "gpt-6-astra",
				"steps": [
					{
						"name": "step1",
						"type": "prompt",
						"systemPrompt": "You are a test assistant.",
						"userPrompt": "Test prompt"
					}
				]
			}
			""";

		var orchestration = OrchestrationParser.ParseOrchestration(json, []);

		orchestration.DefaultReasoningLevel.Should().BeNull();
	}

	[Fact]
	public void ParseOrchestrationFile_Yaml_ParsesDefaultReasoningLevelAndStepOverride()
	{
		var yaml = """
			name: yaml-reasoning
			description: defaultReasoningLevel via YAML
			defaultModel: gpt-6-astra
			defaultReasoningLevel: max
			steps:
			  - name: deep
			    type: prompt
			    systemPrompt: You are helpful.
			    userPrompt: Think.
			  - name: quick
			    type: prompt
			    systemPrompt: You are quick.
			    userPrompt: Answer.
			    reasoningLevel: low
			""";

		var tempDir = Path.Combine(Path.GetTempPath(), $"orchestra-reasoning-test-{Guid.NewGuid():N}");
		Directory.CreateDirectory(tempDir);
		var filePath = Path.Combine(tempDir, "reasoning.yaml");

		try
		{
			File.WriteAllText(filePath, yaml);
			var orchestration = OrchestrationParser.ParseOrchestrationFile(filePath, []);

			orchestration.DefaultReasoningLevel.Should().Be(ReasoningLevel.Max);
			orchestration.Steps[0].Should().BeOfType<PromptOrchestrationStep>().Subject.ReasoningLevel.Should().BeNull();
			orchestration.Steps[1].Should().BeOfType<PromptOrchestrationStep>().Subject.ReasoningLevel.Should().Be(ReasoningLevel.Low);
		}
		finally
		{
			Directory.Delete(tempDir, recursive: true);
		}
	}
}
