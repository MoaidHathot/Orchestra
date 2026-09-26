using FluentAssertions;

namespace Orchestra.Engine.Tests.Serialization;

/// <summary>
/// <c>when</c> is a base step field parsed by <see cref="StepBaseFields"/>; every step type must
/// round-trip it, in JSON and in YAML, so a condition can never be silently dropped by one
/// parser that forgot the field.
/// </summary>
public class WhenFieldParsingTests
{
	public static TheoryData<string, string> StepJsonByType() => new()
	{
		{ "Prompt", """{ "name": "s", "type": "Prompt", "systemPrompt": "a", "userPrompt": "b", "model": "claude-opus-4.8", "when": "{{param.go}} == 'yes'" }""" },
		{ "Command", """{ "name": "s", "type": "Command", "command": "echo", "when": "{{param.go}} == 'yes'" }""" },
		{ "Script", """{ "name": "s", "type": "Script", "shell": "pwsh", "script": "1", "when": "{{param.go}} == 'yes'" }""" },
		{ "Http", """{ "name": "s", "type": "Http", "url": "https://x", "when": "{{param.go}} == 'yes'" }""" },
		{ "Transform", """{ "name": "s", "type": "Transform", "template": "t", "when": "{{param.go}} == 'yes'" }""" },
		{ "Approval", """{ "name": "s", "type": "Approval", "prompt": "ok?", "when": "{{param.go}} == 'yes'" }""" },
		{ "Orchestration", """{ "name": "s", "type": "Orchestration", "orchestration": "child", "when": "{{param.go}} == 'yes'" }""" },
	};

	[Theory]
	[MemberData(nameof(StepJsonByType))]
	public void EveryStepType_ParsesWhen(string type, string stepJson)
	{
		var json = $$"""
		{
			"name": "when-parse",
			"description": "d",
			"inputs": { "go": { "type": "string", "required": false, "default": "yes" } },
			"steps": [ {{stepJson}} ]
		}
		""";

		var orchestration = OrchestrationParser.ParseOrchestration(json, []);

		orchestration.Steps.Should().ContainSingle();
		orchestration.Steps[0].Type.ToString().Should().Be(type);
		orchestration.Steps[0].When.Should().Be("{{param.go}} == 'yes'");
	}

	[Fact]
	public void When_IsNullWhenAbsent_AndTrimmed_AndNullWhenBlank()
	{
		var orchestration = OrchestrationParser.ParseOrchestration("""
		{
			"name": "when-shapes",
			"description": "d",
			"steps": [
				{ "name": "absent", "type": "Transform", "template": "t" },
				{ "name": "padded", "type": "Transform", "template": "t", "when": "   true   " },
				{ "name": "blank", "type": "Transform", "template": "t", "when": "   " }
			]
		}
		""", []);

		orchestration.Steps[0].When.Should().BeNull();
		orchestration.Steps[1].When.Should().Be("true");
		orchestration.Steps[2].When.Should().BeNull("a blank condition means no condition");
	}

	[Fact]
	public void When_ParsesFromYaml_WhenQuoted()
	{
		// In YAML a condition starting with `{{` must be quoted or it is read as a flow mapping;
		// this is the shape the docs and the authoring skill show.
		var tmp = Path.Combine(Path.GetTempPath(), $"orchestra-when-{Guid.NewGuid():N}.yaml");
		File.WriteAllText(tmp, """
			name: when-yaml
			description: d
			steps:
			  - name: decide
			    type: Transform
			    template: prod
			  - name: deploy
			    type: Transform
			    dependsOn: [decide]
			    when: "{{decide.output}} == 'prod' && !failure()"
			    template: deploying
			""");
		try
		{
			var orchestration = OrchestrationParser.ParseOrchestrationFile(tmp, []);
			orchestration.Steps[1].When.Should().Be("{{decide.output}} == 'prod' && !failure()");
			TemplateExpressionValidator.ValidateOrchestration(orchestration).IsValid.Should().BeTrue();
		}
		finally
		{
			File.Delete(tmp);
		}
	}
}
