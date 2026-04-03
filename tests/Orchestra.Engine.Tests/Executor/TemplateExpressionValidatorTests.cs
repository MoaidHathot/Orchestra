using FluentAssertions;

namespace Orchestra.Engine.Tests.Executor;

public class TemplateExpressionValidatorTests
{
	#region Helper Methods

	private static Orchestration CreateOrchestration(
		OrchestrationStep[]? steps = null,
		Dictionary<string, string>? variables = null,
		Mcp[]? mcps = null)
	{
		return new Orchestration
		{
			Name = "test-orchestration",
			Description = "Test orchestration",
			Steps = steps ?? [CreateTransformStep("step1", "Hello")],
			Variables = variables ?? [],
			Mcps = mcps ?? [],
		};
	}

	private static TransformOrchestrationStep CreateTransformStep(
		string name,
		string template,
		string[]? dependsOn = null,
		string[]? parameters = null)
	{
		return new TransformOrchestrationStep
		{
			Name = name,
			Type = OrchestrationStepType.Transform,
			DependsOn = dependsOn ?? [],
			Parameters = parameters ?? [],
			Template = template,
		};
	}

	private static PromptOrchestrationStep CreatePromptStep(
		string name,
		string userPrompt,
		string[]? dependsOn = null,
		string[]? parameters = null,
		Mcp[]? mcps = null,
		Subagent[]? subagents = null,
		string[]? skillDirectories = null)
	{
		return new PromptOrchestrationStep
		{
			Name = name,
			Type = OrchestrationStepType.Prompt,
			DependsOn = dependsOn ?? [],
			Parameters = parameters ?? [],
			SystemPrompt = "You are a helpful assistant.",
			UserPrompt = userPrompt,
			Model = "claude-opus-4.5",
			Mcps = mcps ?? [],
			Subagents = subagents ?? [],
			SkillDirectories = skillDirectories ?? [],
		};
	}

	private static CommandOrchestrationStep CreateCommandStep(
		string name,
		string command,
		string[]? arguments = null,
		string? workingDirectory = null,
		string? stdin = null,
		Dictionary<string, string>? environment = null,
		string[]? dependsOn = null,
		string[]? parameters = null)
	{
		return new CommandOrchestrationStep
		{
			Name = name,
			Type = OrchestrationStepType.Command,
			DependsOn = dependsOn ?? [],
			Parameters = parameters ?? [],
			Command = command,
			Arguments = arguments ?? [],
			WorkingDirectory = workingDirectory,
			Stdin = stdin,
			Environment = environment ?? [],
		};
	}

	private static HttpOrchestrationStep CreateHttpStep(
		string name,
		string url,
		string? body = null,
		Dictionary<string, string>? headers = null,
		string[]? dependsOn = null,
		string[]? parameters = null)
	{
		return new HttpOrchestrationStep
		{
			Name = name,
			Type = OrchestrationStepType.Http,
			DependsOn = dependsOn ?? [],
			Parameters = parameters ?? [],
			Url = url,
			Body = body,
			Headers = headers ?? [],
		};
	}

	#endregion

	#region ValidateOrchestration — Valid Orchestrations

	[Fact]
	public void ValidateOrchestration_ValidOrchestration_ReturnsNoErrors()
	{
		var orchestration = CreateOrchestration(
			steps:
			[
				CreateTransformStep("step1", "Hello {{param.topic}}", parameters: ["topic"]),
				CreateTransformStep("step2", "{{step1.output}} extended", dependsOn: ["step1"]),
			],
			variables: new() { ["greeting"] = "Hello {{param.topic}}" });

		var result = TemplateExpressionValidator.ValidateOrchestration(orchestration);

		result.IsValid.Should().BeTrue();
		result.Errors.Should().BeEmpty();
	}

	[Fact]
	public void ValidateOrchestration_EmptyOrchestration_ReturnsNoErrors()
	{
		var orchestration = CreateOrchestration(steps: [CreateTransformStep("step1", "plain text")]);

		var result = TemplateExpressionValidator.ValidateOrchestration(orchestration);

		result.IsValid.Should().BeTrue();
	}

	[Fact]
	public void ValidateOrchestration_ValidOrchestrationProperties_ReturnsNoErrors()
	{
		var orchestration = CreateOrchestration(
			steps:
			[
				CreateTransformStep("step1",
					"Name: {{orchestration.name}}, Version: {{orchestration.version}}, " +
					"RunId: {{orchestration.runId}}, Started: {{orchestration.startedAt}}, " +
					"Temp: {{orchestration.tempDir}}"),
			]);

		var result = TemplateExpressionValidator.ValidateOrchestration(orchestration);

		result.IsValid.Should().BeTrue();
	}

	[Fact]
	public void ValidateOrchestration_ValidStepProperties_ReturnsNoErrors()
	{
		var orchestration = CreateOrchestration(
			steps:
			[
				CreateTransformStep("step1", "Step: {{step.name}}, Type: {{step.type}}"),
			]);

		var result = TemplateExpressionValidator.ValidateOrchestration(orchestration);

		result.IsValid.Should().BeTrue();
	}

	[Fact]
	public void ValidateOrchestration_ValidVarsReference_ReturnsNoErrors()
	{
		var orchestration = CreateOrchestration(
			steps: [CreateTransformStep("step1", "{{vars.greeting}}")],
			variables: new() { ["greeting"] = "Hello" });

		var result = TemplateExpressionValidator.ValidateOrchestration(orchestration);

		result.IsValid.Should().BeTrue();
	}

	[Fact]
	public void ValidateOrchestration_ValidEnvReference_ReturnsNoErrors()
	{
		// env expressions are validated at runtime, not parse time
		var orchestration = CreateOrchestration(
			steps: [CreateTransformStep("step1", "Token: {{env.MY_TOKEN}}")]);

		var result = TemplateExpressionValidator.ValidateOrchestration(orchestration);

		result.IsValid.Should().BeTrue();
	}

	[Fact]
	public void ValidateOrchestration_ValidStepOutputWithDependency_ReturnsNoErrors()
	{
		var orchestration = CreateOrchestration(
			steps:
			[
				CreateTransformStep("step1", "First"),
				CreateTransformStep("step2", "{{step1.output}}", dependsOn: ["step1"]),
			]);

		var result = TemplateExpressionValidator.ValidateOrchestration(orchestration);

		result.IsValid.Should().BeTrue();
	}

	[Fact]
	public void ValidateOrchestration_ValidStepRawOutputWithDependency_ReturnsNoErrors()
	{
		var orchestration = CreateOrchestration(
			steps:
			[
				CreateTransformStep("step1", "First"),
				CreateTransformStep("step2", "{{step1.rawOutput}}", dependsOn: ["step1"]),
			]);

		var result = TemplateExpressionValidator.ValidateOrchestration(orchestration);

		result.IsValid.Should().BeTrue();
	}

	[Fact]
	public void ValidateOrchestration_ValidStepFilesWithDependency_ReturnsNoErrors()
	{
		var orchestration = CreateOrchestration(
			steps:
			[
				CreateTransformStep("step1", "First"),
				CreateTransformStep("step2", "{{step1.files}}", dependsOn: ["step1"]),
			]);

		var result = TemplateExpressionValidator.ValidateOrchestration(orchestration);

		result.IsValid.Should().BeTrue();
	}

	[Fact]
	public void ValidateOrchestration_ValidStepFilesIndexWithDependency_ReturnsNoErrors()
	{
		var orchestration = CreateOrchestration(
			steps:
			[
				CreateTransformStep("step1", "First"),
				CreateTransformStep("step2", "{{step1.files[0]}}", dependsOn: ["step1"]),
			]);

		var result = TemplateExpressionValidator.ValidateOrchestration(orchestration);

		result.IsValid.Should().BeTrue();
	}

	[Fact]
	public void ValidateOrchestration_TransitiveDependency_ReturnsNoErrors()
	{
		var orchestration = CreateOrchestration(
			steps:
			[
				CreateTransformStep("step1", "First"),
				CreateTransformStep("step2", "{{step1.output}}", dependsOn: ["step1"]),
				CreateTransformStep("step3", "{{step1.output}}", dependsOn: ["step2"]),
			]);

		var result = TemplateExpressionValidator.ValidateOrchestration(orchestration);

		result.IsValid.Should().BeTrue();
	}

	#endregion

	#region ValidateOrchestration — Missing Parameters

	[Fact]
	public void ValidateOrchestration_UndeclaredParameter_ReturnsError()
	{
		var orchestration = CreateOrchestration(
			steps: [CreateTransformStep("step1", "Hello {{param.topic}}")]);

		var result = TemplateExpressionValidator.ValidateOrchestration(orchestration);

		result.IsValid.Should().BeFalse();
		result.Errors.Should().ContainSingle(e =>
			e.Message.Contains("'topic'") &&
			e.Message.Contains("not declared") &&
			e.StepName == "step1" &&
			e.FieldName == "Template" &&
			e.Expression == "{{param.topic}}");
	}

	[Fact]
	public void ValidateOrchestration_ParameterDeclaredInAnotherStep_ReturnsNoErrors()
	{
		// Parameters are globally pooled across all steps
		var orchestration = CreateOrchestration(
			steps:
			[
				CreateTransformStep("step1", "Hello {{param.topic}}"),
				CreateTransformStep("step2", "plain text", parameters: ["topic"]),
			]);

		var result = TemplateExpressionValidator.ValidateOrchestration(orchestration);

		result.IsValid.Should().BeTrue();
	}

	#endregion

	#region ValidateOrchestration — Undefined Variables

	[Fact]
	public void ValidateOrchestration_UndefinedVariable_ReturnsError()
	{
		var orchestration = CreateOrchestration(
			steps: [CreateTransformStep("step1", "{{vars.missing}}")]);

		var result = TemplateExpressionValidator.ValidateOrchestration(orchestration);

		result.IsValid.Should().BeFalse();
		result.Errors.Should().ContainSingle(e =>
			e.Message.Contains("'missing'") &&
			e.Message.Contains("not defined") &&
			e.Expression == "{{vars.missing}}");
	}

	#endregion

	#region ValidateOrchestration — Circular Variables

	[Fact]
	public void ValidateOrchestration_DirectCircularVariable_ReturnsError()
	{
		var orchestration = CreateOrchestration(
			steps: [CreateTransformStep("step1", "Hello")],
			variables: new() { ["a"] = "{{vars.a}}" });

		var result = TemplateExpressionValidator.ValidateOrchestration(orchestration);

		result.IsValid.Should().BeFalse();
		result.Errors.Should().Contain(e =>
			e.Message.Contains("Circular variable reference"));
	}

	[Fact]
	public void ValidateOrchestration_IndirectCircularVariable_ReturnsError()
	{
		var orchestration = CreateOrchestration(
			steps: [CreateTransformStep("step1", "Hello")],
			variables: new()
			{
				["a"] = "{{vars.b}}",
				["b"] = "{{vars.c}}",
				["c"] = "{{vars.a}}",
			});

		var result = TemplateExpressionValidator.ValidateOrchestration(orchestration);

		result.IsValid.Should().BeFalse();
		result.Errors.Should().Contain(e =>
			e.Message.Contains("Circular variable reference"));
	}

	[Fact]
	public void ValidateOrchestration_NonCircularVariableChain_ReturnsNoErrors()
	{
		var orchestration = CreateOrchestration(
			steps: [CreateTransformStep("step1", "{{vars.a}}")],
			variables: new()
			{
				["a"] = "{{vars.b}}",
				["b"] = "{{vars.c}}",
				["c"] = "final value",
			});

		var result = TemplateExpressionValidator.ValidateOrchestration(orchestration);

		result.IsValid.Should().BeTrue();
	}

	#endregion

	#region ValidateOrchestration — Invalid Orchestration Properties

	[Fact]
	public void ValidateOrchestration_InvalidOrchestrationProperty_ReturnsError()
	{
		var orchestration = CreateOrchestration(
			steps: [CreateTransformStep("step1", "{{orchestration.invalid}}")]);

		var result = TemplateExpressionValidator.ValidateOrchestration(orchestration);

		result.IsValid.Should().BeFalse();
		result.Errors.Should().ContainSingle(e =>
			e.Message.Contains("Unknown orchestration property 'invalid'") &&
			e.Expression == "{{orchestration.invalid}}");
	}

	#endregion

	#region ValidateOrchestration — Invalid Step Properties

	[Fact]
	public void ValidateOrchestration_InvalidStepProperty_ReturnsError()
	{
		var orchestration = CreateOrchestration(
			steps: [CreateTransformStep("step1", "{{step.invalid}}")]);

		var result = TemplateExpressionValidator.ValidateOrchestration(orchestration);

		result.IsValid.Should().BeFalse();
		result.Errors.Should().ContainSingle(e =>
			e.Message.Contains("Unknown step property 'invalid'") &&
			e.Expression == "{{step.invalid}}");
	}

	#endregion

	#region ValidateOrchestration — Step Output in Static-Only Context

	[Fact]
	public void ValidateOrchestration_StepOutputInVariableValue_ReturnsError()
	{
		var orchestration = CreateOrchestration(
			steps:
			[
				CreateTransformStep("step1", "Hello"),
				CreateTransformStep("step2", "{{vars.captured}}", dependsOn: ["step1"]),
			],
			variables: new() { ["captured"] = "{{step1.output}}" });

		var result = TemplateExpressionValidator.ValidateOrchestration(orchestration);

		result.IsValid.Should().BeFalse();
		result.Errors.Should().Contain(e =>
			e.Message.Contains("static-only") &&
			e.FieldName!.Contains("Variables[captured]"));
	}

	[Fact]
	public void ValidateOrchestration_StepMetadataInVariableValue_ReturnsError()
	{
		var orchestration = CreateOrchestration(
			steps: [CreateTransformStep("step1", "{{vars.meta}}")],
			variables: new() { ["meta"] = "{{step.name}}" });

		var result = TemplateExpressionValidator.ValidateOrchestration(orchestration);

		result.IsValid.Should().BeFalse();
		result.Errors.Should().Contain(e =>
			e.Message.Contains("static-only") &&
			e.FieldName!.Contains("Variables[meta]"));
	}

	[Fact]
	public void ValidateOrchestration_StepOutputInOrchestrationMcp_ReturnsError()
	{
		var mcp = new LocalMcp
		{
			Name = "test-mcp",
			Type = McpType.Local,
			Command = "{{step1.output}}",
			Arguments = [],
		};
		var orchestration = CreateOrchestration(
			steps: [CreateTransformStep("step1", "Hello")],
			mcps: [mcp]);

		var result = TemplateExpressionValidator.ValidateOrchestration(orchestration);

		result.IsValid.Should().BeFalse();
		result.Errors.Should().Contain(e =>
			e.Message.Contains("static-only") &&
			e.FieldName!.Contains("Mcps[0]"));
	}

	[Fact]
	public void ValidateOrchestration_StepOutputInStepMcp_ReturnsError()
	{
		var mcp = new RemoteMcp
		{
			Name = "test-mcp",
			Type = McpType.Remote,
			Endpoint = "{{step1.output}}",
			Headers = [],
		};
		var step = CreatePromptStep("step2", "Hello", mcps: [mcp], dependsOn: ["step1"]);
		var orchestration = CreateOrchestration(
			steps: [CreateTransformStep("step1", "First"), step]);

		var result = TemplateExpressionValidator.ValidateOrchestration(orchestration);

		result.IsValid.Should().BeFalse();
		result.Errors.Should().Contain(e =>
			e.Message.Contains("static-only") &&
			e.StepName == "step2");
	}

	[Fact]
	public void ValidateOrchestration_StepOutputInSubagentMcp_ReturnsError()
	{
		var mcp = new LocalMcp
		{
			Name = "sub-mcp",
			Type = McpType.Local,
			Command = "{{step1.output}}",
			Arguments = [],
		};
		var subagent = new Subagent
		{
			Name = "sub",
			Prompt = "Help",
			Mcps = [mcp],
		};
		var step = CreatePromptStep("step2", "Hello", subagents: [subagent], dependsOn: ["step1"]);
		var orchestration = CreateOrchestration(
			steps: [CreateTransformStep("step1", "First"), step]);

		var result = TemplateExpressionValidator.ValidateOrchestration(orchestration);

		result.IsValid.Should().BeFalse();
		result.Errors.Should().Contain(e =>
			e.Message.Contains("static-only") &&
			e.FieldName!.Contains("Subagents[0].Mcps[0]"));
	}

	#endregion

	#region ValidateOrchestration — Unreachable Step References

	[Fact]
	public void ValidateOrchestration_StepOutputWithoutDependency_ReturnsError()
	{
		var orchestration = CreateOrchestration(
			steps:
			[
				CreateTransformStep("step1", "Hello"),
				CreateTransformStep("step2", "{{step1.output}}"), // no dependsOn!
			]);

		var result = TemplateExpressionValidator.ValidateOrchestration(orchestration);

		result.IsValid.Should().BeFalse();
		result.Errors.Should().ContainSingle(e =>
			e.Message.Contains("not reachable via DependsOn") &&
			e.StepName == "step2" &&
			e.Expression == "{{step1.output}}");
	}

	[Fact]
	public void ValidateOrchestration_StepOutputToNonExistentStep_ReturnsError()
	{
		var orchestration = CreateOrchestration(
			steps:
			[
				CreateTransformStep("step1", "{{ghost.output}}", dependsOn: ["ghost"]),
			]);

		var result = TemplateExpressionValidator.ValidateOrchestration(orchestration);

		result.IsValid.Should().BeFalse();
		result.Errors.Should().Contain(e =>
			e.Message.Contains("does not exist") &&
			e.Expression == "{{ghost.output}}");
	}

	#endregion

	#region ValidateOrchestration — Unknown Expressions

	[Fact]
	public void ValidateOrchestration_UnknownNamespace_ReturnsError()
	{
		var orchestration = CreateOrchestration(
			steps: [CreateTransformStep("step1", "{{foo.bar}}")]);

		var result = TemplateExpressionValidator.ValidateOrchestration(orchestration);

		result.IsValid.Should().BeFalse();
		result.Errors.Should().ContainSingle(e =>
			e.Message.Contains("Unknown expression namespace 'foo'") &&
			e.Expression == "{{foo.bar}}");
	}

	[Fact]
	public void ValidateOrchestration_NoDotExpression_ReturnsError()
	{
		var orchestration = CreateOrchestration(
			steps: [CreateTransformStep("step1", "{{invalid}}")]);

		var result = TemplateExpressionValidator.ValidateOrchestration(orchestration);

		result.IsValid.Should().BeFalse();
		result.Errors.Should().ContainSingle(e =>
			e.Message.Contains("Invalid expression format") &&
			e.Expression == "{{invalid}}");
	}

	[Fact]
	public void ValidateOrchestration_InvalidStepOutputProperty_ReturnsError()
	{
		var orchestration = CreateOrchestration(
			steps:
			[
				CreateTransformStep("step1", "Hello"),
				CreateTransformStep("step2", "{{step1.typo}}", dependsOn: ["step1"]),
			]);

		var result = TemplateExpressionValidator.ValidateOrchestration(orchestration);

		result.IsValid.Should().BeFalse();
		result.Errors.Should().ContainSingle(e =>
			e.Message.Contains("Unknown output property 'typo'") &&
			e.Expression == "{{step1.typo}}");
	}

	#endregion

	#region ValidateOrchestration — All Step Types

	[Fact]
	public void ValidateOrchestration_CommandStep_ValidatesAllFields()
	{
		var orchestration = CreateOrchestration(
			steps:
			[
				CreateCommandStep("cmd1", "{{param.missing_cmd}}",
					arguments: ["{{param.missing_arg}}"],
					workingDirectory: "{{param.missing_dir}}",
					stdin: "{{param.missing_stdin}}",
					environment: new() { ["KEY"] = "{{param.missing_env}}" }),
			]);

		var result = TemplateExpressionValidator.ValidateOrchestration(orchestration);

		result.IsValid.Should().BeFalse();
		result.Errors.Should().HaveCount(5);
		result.Errors.Should().OnlyContain(e => e.StepName == "cmd1");
	}

	[Fact]
	public void ValidateOrchestration_HttpStep_ValidatesAllFields()
	{
		var orchestration = CreateOrchestration(
			steps:
			[
				CreateHttpStep("http1", "{{param.missing_url}}",
					body: "{{param.missing_body}}",
					headers: new() { ["Auth"] = "{{param.missing_header}}" }),
			]);

		var result = TemplateExpressionValidator.ValidateOrchestration(orchestration);

		result.IsValid.Should().BeFalse();
		result.Errors.Should().HaveCount(3);
		result.Errors.Should().OnlyContain(e => e.StepName == "http1");
	}

	[Fact]
	public void ValidateOrchestration_PromptStep_ValidatesUserPromptAndSkillDirectories()
	{
		var step = CreatePromptStep("prompt1", "{{param.missing_prompt}}",
			skillDirectories: ["{{param.missing_dir}}"]);
		var orchestration = CreateOrchestration(steps: [step]);

		var result = TemplateExpressionValidator.ValidateOrchestration(orchestration);

		result.IsValid.Should().BeFalse();
		result.Errors.Should().HaveCount(2);
		result.Errors.Should().OnlyContain(e => e.StepName == "prompt1");
	}

	#endregion

	#region ValidateOrchestration — MCP Fields

	[Fact]
	public void ValidateOrchestration_LocalMcp_ValidatesCommandArgumentsWorkDir()
	{
		var mcp = new LocalMcp
		{
			Name = "test",
			Type = McpType.Local,
			Command = "{{param.cmd}}",
			Arguments = ["{{param.arg}}"],
			WorkingDirectory = "{{param.dir}}",
		};
		var orchestration = CreateOrchestration(
			steps: [CreateTransformStep("step1", "Hello", parameters: ["cmd", "arg", "dir"])],
			mcps: [mcp]);

		var result = TemplateExpressionValidator.ValidateOrchestration(orchestration);

		result.IsValid.Should().BeTrue();
	}

	[Fact]
	public void ValidateOrchestration_RemoteMcp_ValidatesEndpointAndHeaders()
	{
		var mcp = new RemoteMcp
		{
			Name = "test",
			Type = McpType.Remote,
			Endpoint = "{{vars.endpoint}}",
			Headers = new() { ["Authorization"] = "{{vars.token}}" },
		};
		var orchestration = CreateOrchestration(
			steps: [CreateTransformStep("step1", "Hello")],
			mcps: [mcp],
			variables: new() { ["endpoint"] = "https://api.example.com", ["token"] = "Bearer xyz" });

		var result = TemplateExpressionValidator.ValidateOrchestration(orchestration);

		result.IsValid.Should().BeTrue();
	}

	[Fact]
	public void ValidateOrchestration_McpFieldWithUndefinedVar_ReturnsError()
	{
		var mcp = new RemoteMcp
		{
			Name = "test",
			Type = McpType.Remote,
			Endpoint = "{{vars.missing}}",
			Headers = [],
		};
		var orchestration = CreateOrchestration(
			steps: [CreateTransformStep("step1", "Hello")],
			mcps: [mcp]);

		var result = TemplateExpressionValidator.ValidateOrchestration(orchestration);

		result.IsValid.Should().BeFalse();
		result.Errors.Should().ContainSingle(e =>
			e.Message.Contains("'missing'") &&
			e.Message.Contains("not defined") &&
			e.FieldName!.Contains("Mcps[0]"));
	}

	#endregion

	#region ValidateOrchestration — Mixed Errors

	[Fact]
	public void ValidateOrchestration_MultipleErrors_ReportsAll()
	{
		var orchestration = CreateOrchestration(
			steps:
			[
				CreateTransformStep("step1", "{{param.missing}} and {{vars.undefined}}"),
				CreateTransformStep("step2", "{{ghost.output}}", dependsOn: ["step1"]),
			],
			variables: new() { ["a"] = "{{vars.b}}", ["b"] = "{{vars.a}}" });

		var result = TemplateExpressionValidator.ValidateOrchestration(orchestration);

		result.IsValid.Should().BeFalse();
		// Should find: missing param, undefined var, nonexistent step, circular vars
		result.Errors.Count.Should().BeGreaterThanOrEqualTo(4);
	}

	[Fact]
	public void ValidateOrchestration_FormatErrors_ProducesReadableOutput()
	{
		var orchestration = CreateOrchestration(
			steps: [CreateTransformStep("step1", "{{param.x}}")]);

		var result = TemplateExpressionValidator.ValidateOrchestration(orchestration);

		result.IsValid.Should().BeFalse();
		var formatted = result.FormatErrors();
		formatted.Should().Contain("Template expression validation failed");
		formatted.Should().Contain("1 error(s)");
		formatted.Should().Contain("Step 'step1'");
	}

	#endregion

	#region ValidateRuntime — Environment Variables

	[Fact]
	public void ValidateRuntime_MissingEnvVar_ReturnsError()
	{
		var envVarName = $"ORCHESTRA_TEST_MISSING_{Guid.NewGuid():N}";
		var orchestration = CreateOrchestration(
			steps: [CreateTransformStep("step1", $"{{{{env.{envVarName}}}}}")]);

		var result = TemplateExpressionValidator.ValidateRuntime(orchestration, null);

		result.IsValid.Should().BeFalse();
		result.Errors.Should().ContainSingle(e =>
			e.Message.Contains(envVarName) &&
			e.Message.Contains("not set"));
	}

	[Fact]
	public void ValidateRuntime_ExistingEnvVar_ReturnsNoErrors()
	{
		// PATH should always exist on all platforms
		var orchestration = CreateOrchestration(
			steps: [CreateTransformStep("step1", "{{env.PATH}}")]);

		var result = TemplateExpressionValidator.ValidateRuntime(orchestration, null);

		result.IsValid.Should().BeTrue();
	}

	[Fact]
	public void ValidateRuntime_MissingEnvVarInVariable_ReturnsError()
	{
		var envVarName = $"ORCHESTRA_TEST_MISSING_{Guid.NewGuid():N}";
		var orchestration = CreateOrchestration(
			steps: [CreateTransformStep("step1", "{{vars.token}}")],
			variables: new() { ["token"] = $"{{{{env.{envVarName}}}}}" });

		var result = TemplateExpressionValidator.ValidateRuntime(orchestration, null);

		result.IsValid.Should().BeFalse();
		result.Errors.Should().Contain(e =>
			e.Message.Contains(envVarName));
	}

	[Fact]
	public void ValidateRuntime_MissingEnvVarInMcp_ReturnsError()
	{
		var envVarName = $"ORCHESTRA_TEST_MISSING_{Guid.NewGuid():N}";
		var mcp = new RemoteMcp
		{
			Name = "test",
			Type = McpType.Remote,
			Endpoint = $"{{{{env.{envVarName}}}}}",
			Headers = [],
		};
		var orchestration = CreateOrchestration(
			steps: [CreateTransformStep("step1", "Hello")],
			mcps: [mcp]);

		var result = TemplateExpressionValidator.ValidateRuntime(orchestration, null);

		result.IsValid.Should().BeFalse();
		result.Errors.Should().ContainSingle(e =>
			e.Message.Contains(envVarName));
	}

	#endregion

	#region ValidateRuntime — Variable Parameter Resolution

	[Fact]
	public void ValidateRuntime_VariableReferencingMissingParam_ReturnsError()
	{
		var orchestration = CreateOrchestration(
			steps: [CreateTransformStep("step1", "{{vars.greeting}}", parameters: ["name"])],
			variables: new() { ["greeting"] = "Hello {{param.name}}" });

		// Don't provide the "name" parameter
		var result = TemplateExpressionValidator.ValidateRuntime(orchestration, new Dictionary<string, string>());

		result.IsValid.Should().BeFalse();
		result.Errors.Should().ContainSingle(e =>
			e.Message.Contains("'greeting'") &&
			e.Message.Contains("'name'") &&
			e.Message.Contains("not provided"));
	}

	[Fact]
	public void ValidateRuntime_VariableReferencingProvidedParam_ReturnsNoErrors()
	{
		var orchestration = CreateOrchestration(
			steps: [CreateTransformStep("step1", "{{vars.greeting}}", parameters: ["name"])],
			variables: new() { ["greeting"] = "Hello {{param.name}}" });

		var result = TemplateExpressionValidator.ValidateRuntime(orchestration,
			new Dictionary<string, string> { ["name"] = "World" });

		result.IsValid.Should().BeTrue();
	}

	[Fact]
	public void ValidateRuntime_NoEnvOrParamRefs_ReturnsNoErrors()
	{
		var orchestration = CreateOrchestration(
			steps: [CreateTransformStep("step1", "plain text")],
			variables: new() { ["a"] = "static value" });

		var result = TemplateExpressionValidator.ValidateRuntime(orchestration, null);

		result.IsValid.Should().BeTrue();
	}

	#endregion

	#region ValidateOrchestration — Edge Cases

	[Fact]
	public void ValidateOrchestration_VariableReferencingEnv_IsAllowed()
	{
		// env references in variables are fine at parse time (checked at runtime)
		var orchestration = CreateOrchestration(
			steps: [CreateTransformStep("step1", "{{vars.token}}")],
			variables: new() { ["token"] = "Bearer {{env.API_TOKEN}}" });

		var result = TemplateExpressionValidator.ValidateOrchestration(orchestration);

		result.IsValid.Should().BeTrue();
	}

	[Fact]
	public void ValidateOrchestration_VariableReferencingParam_IsAllowed()
	{
		// param references in variables are fine (parameters are globally pooled)
		var orchestration = CreateOrchestration(
			steps: [CreateTransformStep("step1", "{{vars.greeting}}", parameters: ["name"])],
			variables: new() { ["greeting"] = "Hello {{param.name}}" });

		var result = TemplateExpressionValidator.ValidateOrchestration(orchestration);

		result.IsValid.Should().BeTrue();
	}

	[Fact]
	public void ValidateOrchestration_VariableReferencingOrchestration_IsAllowed()
	{
		var orchestration = CreateOrchestration(
			steps: [CreateTransformStep("step1", "{{vars.info}}")],
			variables: new() { ["info"] = "Run {{orchestration.runId}} at {{orchestration.startedAt}}" });

		var result = TemplateExpressionValidator.ValidateOrchestration(orchestration);

		result.IsValid.Should().BeTrue();
	}

	[Fact]
	public void ValidateOrchestration_NoTemplateExpressions_ReturnsNoErrors()
	{
		var orchestration = CreateOrchestration(
			steps:
			[
				CreateCommandStep("cmd1", "echo", arguments: ["hello world"]),
				CreateHttpStep("http1", "https://example.com"),
				CreateTransformStep("t1", "plain text"),
			]);

		var result = TemplateExpressionValidator.ValidateOrchestration(orchestration);

		result.IsValid.Should().BeTrue();
	}

	[Fact]
	public void ValidateOrchestration_ParameterInParamStepDeclaredGlobally_ReturnsNoErrors()
	{
		// param.x used in step1, but "x" declared in step2's Parameters
		var orchestration = CreateOrchestration(
			steps:
			[
				CreateTransformStep("step1", "{{param.x}}"),
				CreateTransformStep("step2", "also uses {{param.x}}", parameters: ["x"]),
			]);

		var result = TemplateExpressionValidator.ValidateOrchestration(orchestration);

		result.IsValid.Should().BeTrue();
	}

	#endregion

	#region FormatErrors

	[Fact]
	public void FormatErrors_WhenValid_ReturnsEmptyString()
	{
		var result = new TemplateValidationResult();

		result.FormatErrors().Should().BeEmpty();
	}

	[Fact]
	public void FormatErrors_WithStepAndField_IncludesBothInOutput()
	{
		var result = new TemplateValidationResult();
		result.Errors.Add(new TemplateValidationError(
			"Some error", StepName: "myStep", FieldName: "Command", Expression: "{{param.x}}"));

		var formatted = result.FormatErrors();

		formatted.Should().Contain("[Step 'myStep', Field 'Command']");
		formatted.Should().Contain("Some error");
		formatted.Should().Contain("Expression: {{param.x}}");
	}

	[Fact]
	public void FormatErrors_WithOnlyField_OmitsStepName()
	{
		var result = new TemplateValidationResult();
		result.Errors.Add(new TemplateValidationError(
			"Some error", FieldName: "Variables[x]"));

		var formatted = result.FormatErrors();

		formatted.Should().Contain("[Field 'Variables[x]']");
	}

	[Fact]
	public void FormatErrors_WithNoContext_ShowsOrchestration()
	{
		var result = new TemplateValidationResult();
		result.Errors.Add(new TemplateValidationError("Some error"));

		var formatted = result.FormatErrors();

		formatted.Should().Contain("[Orchestration]");
	}

	#endregion
}
