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
		string[]? skillDirectories = null,
		string model = "claude-opus-4.5",
		string systemPrompt = "You are a helpful assistant.",
		string? inputHandlerPrompt = null,
		string? outputHandlerPrompt = null)
	{
		return new PromptOrchestrationStep
		{
			Name = name,
			Type = OrchestrationStepType.Prompt,
			DependsOn = dependsOn ?? [],
			Parameters = parameters ?? [],
			SystemPrompt = systemPrompt,
			UserPrompt = userPrompt,
			Model = model,
			InputHandlerPrompt = inputHandlerPrompt,
			OutputHandlerPrompt = outputHandlerPrompt,
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
					"Temp: {{orchestration.tempDir}}, Source: {{orchestration.sourcePath}}, " +
					"SourceDir: {{orchestration.sourceDirectory}}"),
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

	/// <summary>
	/// <see cref="Mcp.TimeoutTemplate"/> on a step-level MCP accepts step-output
	/// references against the parent step's <c>DependsOn</c> set — that is the whole
	/// reason the field exists separately from the static-only string fields.
	/// </summary>
	[Fact]
	public void ValidateOrchestration_StepLevelMcp_TimeoutTemplateReferencingReachableStep_IsValid()
	{
		var mcp = new RemoteMcp
		{
			Name = "orchestra",
			Type = McpType.Remote,
			Endpoint = "http://localhost:5001/mcp/data",
			Headers = [],
			TimeoutTemplate = "{{validate-inputs.output}}",
		};
		var step = CreatePromptStep("controller", "Run.", mcps: [mcp], dependsOn: ["validate-inputs"]);
		var orchestration = CreateOrchestration(
			steps: [CreateTransformStep("validate-inputs", "21660"), step]);

		var result = TemplateExpressionValidator.ValidateOrchestration(orchestration);

		result.IsValid.Should().BeTrue(result.FormatErrors());
	}

	/// <summary>
	/// A step-level MCP's <see cref="Mcp.TimeoutTemplate"/> referencing a step that is
	/// NOT in the owning step's <c>DependsOn</c> (direct or transitive) is rejected with
	/// a clear reachability error — the same contract as other step-aware fields.
	/// </summary>
	[Fact]
	public void ValidateOrchestration_StepLevelMcp_TimeoutTemplateReferencingUnreachableStep_ReturnsError()
	{
		var mcp = new RemoteMcp
		{
			Name = "orchestra",
			Type = McpType.Remote,
			Endpoint = "http://localhost:5001/mcp/data",
			Headers = [],
			TimeoutTemplate = "{{validate-inputs.output}}",
		};
		// Note: no dependsOn on the prompt step.
		var step = CreatePromptStep("controller", "Run.", mcps: [mcp]);
		var orchestration = CreateOrchestration(
			steps: [CreateTransformStep("validate-inputs", "21660"), step]);

		var result = TemplateExpressionValidator.ValidateOrchestration(orchestration);

		result.IsValid.Should().BeFalse();
		result.Errors.Should().Contain(e =>
			e.Message.Contains("not reachable via DependsOn") &&
			e.StepName == "controller" &&
			e.FieldName!.Contains("TimeoutTemplate"));
	}

	/// <summary>
	/// An orchestration-level MCP definition is reused-by-reference whenever a step
	/// names it via <c>mcps: [...]</c>. To avoid double-rejecting a valid step-aware
	/// <c>TimeoutTemplate</c>, the orchestration-level validator pass SKIPS the timeout
	/// template (per-step validation catches reachability errors). A step output
	/// reference on an orchestration-level MCP that is also consumed by a step with the
	/// referenced step in its <c>DependsOn</c> set must therefore pass overall
	/// validation.
	/// </summary>
	[Fact]
	public void ValidateOrchestration_OrchestrationLevelMcp_TimeoutTemplateStepOutput_PassesWhenConsumingStepHasReachability()
	{
		var mcp = new RemoteMcp
		{
			Name = "orchestra",
			Type = McpType.Remote,
			Endpoint = "http://localhost:5001/mcp/data",
			Headers = [],
			TimeoutTemplate = "{{validate-inputs.output}}",
		};
		var controller = CreatePromptStep("controller", "Run.", mcps: [mcp], dependsOn: ["validate-inputs"]);
		var orchestration = CreateOrchestration(
			steps: [CreateTransformStep("validate-inputs", "21660"), controller],
			mcps: [mcp]);

		var result = TemplateExpressionValidator.ValidateOrchestration(orchestration);

		result.IsValid.Should().BeTrue(result.FormatErrors());
	}

	/// <summary>
	/// An orchestration-level MCP definition whose <c>TimeoutTemplate</c> references a
	/// step output, that is also consumed by a step WITHOUT reachability to the
	/// referenced step, still fails the per-step pass — so reachability bugs are caught.
	/// </summary>
	[Fact]
	public void ValidateOrchestration_OrchestrationLevelMcp_TimeoutTemplateUnreachableInConsumer_ReturnsError()
	{
		var mcp = new RemoteMcp
		{
			Name = "orchestra",
			Type = McpType.Remote,
			Endpoint = "http://localhost:5001/mcp/data",
			Headers = [],
			TimeoutTemplate = "{{validate-inputs.output}}",
		};
		// Note: no dependsOn on the prompt step.
		var controller = CreatePromptStep("controller", "Run.", mcps: [mcp]);
		var orchestration = CreateOrchestration(
			steps: [CreateTransformStep("validate-inputs", "21660"), controller],
			mcps: [mcp]);

		var result = TemplateExpressionValidator.ValidateOrchestration(orchestration);

		result.IsValid.Should().BeFalse();
		result.Errors.Should().Contain(e =>
			e.Message.Contains("not reachable via DependsOn") &&
			e.StepName == "controller" &&
			e.FieldName!.Contains("TimeoutTemplate"));
	}

	/// <summary>
	/// <see cref="Mcp.TimeoutTemplate"/> on an orchestration-level MCP can still use
	/// param/vars/env/orchestration references — those resolve without step context.
	/// </summary>
	[Fact]
	public void ValidateOrchestration_OrchestrationLevelMcp_TimeoutTemplateParam_IsValid()
	{
		var mcp = new RemoteMcp
		{
			Name = "orchestra",
			Type = McpType.Remote,
			Endpoint = "http://localhost:5001/mcp/data",
			Headers = [],
			TimeoutTemplate = "{{param.childTimeoutSeconds}}",
		};
		var step = CreatePromptStep("controller", "Run.", parameters: ["childTimeoutSeconds"]);
		var orchestration = CreateOrchestration(steps: [step], mcps: [mcp]);

		var result = TemplateExpressionValidator.ValidateOrchestration(orchestration);

		result.IsValid.Should().BeTrue(result.FormatErrors());
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

	#region ValidateOrchestration -- Prompt Step Fields (Model, SystemPrompt, Handlers)

	[Fact]
	public void ValidateOrchestration_ModelWithValidVarsTemplate_IsValid()
	{
		var orchestration = CreateOrchestration(
			variables: new Dictionary<string, string> { ["defaultModel"] = "claude-opus-4.5" },
			steps:
			[
				CreatePromptStep("step1", "Test prompt", model: "{{vars.defaultModel}}")
			]);

		var result = TemplateExpressionValidator.ValidateOrchestration(orchestration);

		result.IsValid.Should().BeTrue();
	}

	[Fact]
	public void ValidateOrchestration_ModelWithUndefinedVariable_ReturnsError()
	{
		var orchestration = CreateOrchestration(
			steps:
			[
				CreatePromptStep("step1", "Test prompt", model: "{{vars.missingModel}}")
			]);

		var result = TemplateExpressionValidator.ValidateOrchestration(orchestration);

		result.IsValid.Should().BeFalse();
		result.Errors.Should().ContainSingle(e =>
			e.FieldName == "Model" &&
			e.Expression == "{{vars.missingModel}}");
	}

	[Fact]
	public void ValidateOrchestration_SystemPromptWithValidVarsTemplate_IsValid()
	{
		var orchestration = CreateOrchestration(
			variables: new Dictionary<string, string> { ["project"] = "Orchestra" },
			steps:
			[
				CreatePromptStep("step1", "Test prompt",
					systemPrompt: "You review code for {{vars.project}}.")
			]);

		var result = TemplateExpressionValidator.ValidateOrchestration(orchestration);

		result.IsValid.Should().BeTrue();
	}

	[Fact]
	public void ValidateOrchestration_SystemPromptWithStepOutputTemplate_IsValid()
	{
		var orchestration = CreateOrchestration(
			steps:
			[
				CreateTransformStep("context", "some context"),
				CreatePromptStep("step1", "Test prompt",
					dependsOn: ["context"],
					systemPrompt: "You are a reviewer. Context: {{context.output}}")
			]);

		var result = TemplateExpressionValidator.ValidateOrchestration(orchestration);

		result.IsValid.Should().BeTrue();
	}

	[Fact]
	public void ValidateOrchestration_OutputHandlerPromptWithValidTemplate_IsValid()
	{
		var orchestration = CreateOrchestration(
			variables: new Dictionary<string, string> { ["format"] = "JSON" },
			steps:
			[
				CreatePromptStep("step1", "Test prompt",
					outputHandlerPrompt: "Format the output as {{vars.format}}")
			]);

		var result = TemplateExpressionValidator.ValidateOrchestration(orchestration);

		result.IsValid.Should().BeTrue();
	}

	[Fact]
	public void ValidateOrchestration_InputHandlerPromptWithUndefinedVariable_ReturnsError()
	{
		var orchestration = CreateOrchestration(
			steps:
			[
				CreatePromptStep("step1", "Test prompt",
					inputHandlerPrompt: "Transform using {{vars.missingTransform}}")
			]);

		var result = TemplateExpressionValidator.ValidateOrchestration(orchestration);

		result.IsValid.Should().BeFalse();
		result.Errors.Should().ContainSingle(e =>
			e.FieldName == "InputHandlerPrompt" &&
			e.Expression == "{{vars.missingTransform}}");
	}

	#endregion

	#region Orchestration-step accessors (executionId / status / steps.*)

	private static OrchestrationInvocationStep CreateOrchestrationStep(
		string name,
		string orchestrationName = "child-orch",
		string[]? dependsOn = null,
		Dictionary<string, string>? childParameters = null)
	{
		return new OrchestrationInvocationStep
		{
			Name = name,
			Type = OrchestrationStepType.Orchestration,
			DependsOn = dependsOn ?? [],
			Parameters = [],
			OrchestrationName = orchestrationName,
			ChildParameters = childParameters ?? [],
		};
	}

	[Fact]
	public void ValidateOrchestration_ChildAccessorOnNonOrchestrationStep_ReportsError()
	{
		// A Prompt step has no ChildOrchestrationInfo; using {{p1.executionId}} on its
		// content would silently fail at runtime. The validator catches it at parse time
		// so authors discover the mistake before invocation.
		var orchestration = CreateOrchestration(
			steps:
			[
				CreatePromptStep("p1", "do work"),
				CreateTransformStep("consume", "child id was {{p1.executionId}}", dependsOn: ["p1"]),
			]);

		var result = TemplateExpressionValidator.ValidateOrchestration(orchestration);

		result.IsValid.Should().BeFalse();
		result.Errors.Should().ContainSingle(e =>
			e.StepName == "consume" &&
			e.Expression == "{{p1.executionId}}" &&
			e.Message.Contains("Orchestration", StringComparison.OrdinalIgnoreCase));
	}

	[Fact]
	public void ValidateOrchestration_ChildAccessorOnOrchestrationStep_IsValid()
	{
		// Same accessor on a step of type Orchestration — must validate cleanly.
		var orchestration = CreateOrchestration(
			steps:
			[
				CreateOrchestrationStep("inv"),
				CreateTransformStep("after", "child id = {{inv.executionId}}", dependsOn: ["inv"]),
			]);

		var result = TemplateExpressionValidator.ValidateOrchestration(orchestration);

		result.IsValid.Should().BeTrue(result.FormatErrors());
	}

	[Fact]
	public void ValidateOrchestration_ChildStepsUnknownLeaf_ReportsError()
	{
		var orchestration = CreateOrchestration(
			steps:
			[
				CreateOrchestrationStep("inv"),
				CreateTransformStep("after", "{{inv.steps.codegen.bogusLeaf}}", dependsOn: ["inv"]),
			]);

		var result = TemplateExpressionValidator.ValidateOrchestration(orchestration);

		result.IsValid.Should().BeFalse();
		result.Errors.Should().ContainSingle(e =>
			e.StepName == "after" &&
			e.Expression == "{{inv.steps.codegen.bogusLeaf}}" &&
			e.Message.Contains("Unknown child-step accessor", StringComparison.OrdinalIgnoreCase) &&
			e.Message.Contains("output", StringComparison.OrdinalIgnoreCase));
	}

	[Theory]
	[InlineData("output")]
	[InlineData("rawOutput")]
	[InlineData("error")]
	[InlineData("status")]
	[InlineData("files")]
	[InlineData("files[0]")]
	[InlineData("files[42]")]
	public void ValidateOrchestration_ChildStepsValidLeaves_AreAccepted(string leaf)
	{
		var orchestration = CreateOrchestration(
			steps:
			[
				CreateOrchestrationStep("inv"),
				CreateTransformStep("after", $"{{{{inv.steps.codegen.{leaf}}}}}", dependsOn: ["inv"]),
			]);

		var result = TemplateExpressionValidator.ValidateOrchestration(orchestration);

		result.IsValid.Should().BeTrue(result.FormatErrors());
	}

	[Fact]
	public void ValidateOrchestration_ChildAccessorInVarsStaticContext_ReportsStaticOnlyError()
	{
		// vars values are resolved in a static-only context (no step outputs reachable).
		// The orchestration-step accessors must follow the same gate as {{stepName.output}}.
		var orchestration = CreateOrchestration(
			steps:
			[
				CreateOrchestrationStep("inv"),
				CreateTransformStep("after", "ok", dependsOn: ["inv"]),
			],
			variables: new Dictionary<string, string>
			{
				["embedded"] = "exec id = {{inv.executionId}}",
			});

		var result = TemplateExpressionValidator.ValidateOrchestration(orchestration);

		result.IsValid.Should().BeFalse();
		result.Errors.Should().ContainSingle(e =>
			e.FieldName == "Variables[embedded]" &&
			e.Expression == "{{inv.executionId}}" &&
			e.Message.Contains("static-only", StringComparison.OrdinalIgnoreCase));
	}

	[Fact]
	public void ValidateOrchestration_ChildAccessorOnUnreachableStep_ReportsReachabilityError()
	{
		// {{inv.executionId}} is valid syntax on an Orchestration step BUT the target step
		// must be reachable via DependsOn. The reachability rule applies to the new
		// accessors just like it does to {{stepName.output}}.
		var orchestration = CreateOrchestration(
			steps:
			[
				CreateOrchestrationStep("inv"),
				// `after` does NOT depend on `inv`; the accessor is unreachable.
				CreateTransformStep("after", "{{inv.executionId}}"),
			]);

		var result = TemplateExpressionValidator.ValidateOrchestration(orchestration);

		result.IsValid.Should().BeFalse();
		result.Errors.Should().ContainSingle(e =>
			e.StepName == "after" &&
			e.Expression == "{{inv.executionId}}" &&
			e.Message.Contains("not reachable", StringComparison.OrdinalIgnoreCase));
	}

	#endregion

	#region ValidateOrchestration — Escape Syntax

	[Fact]
	public void ValidateOrchestration_EscapedSelfReference_NoError()
	{
		// Arrange — the exact regression scenario. A step's UserPrompt contains
		// a documentation reference to its own output via \{{stepName.output}}.
		// The validator must NOT flag this as unreachable; the runtime resolver
		// will strip the backslash and emit the body verbatim.
		var orchestration = CreateOrchestration(
			steps:
			[
				CreatePromptStep(
					"synthesize",
					userPrompt: @"Save markdown so downstream readers can use \{{synthesize.files[0]}}."),
			]);

		// Act
		var result = TemplateExpressionValidator.ValidateOrchestration(orchestration);

		// Assert
		result.IsValid.Should().BeTrue(result.FormatErrors());
	}

	[Fact]
	public void ValidateOrchestration_EscapedUnreachableStep_NoError()
	{
		// Arrange — a Transform step references another step that is NOT in its
		// DependsOn. Without an escape, this would be a hard validation error
		// (the unreachable-step rule). The escape signals intent and suppresses
		// the check.
		var orchestration = CreateOrchestration(
			steps:
			[
				CreateTransformStep("step1", "Hello"),
				CreateTransformStep("step2", @"\{{step1.output}}"), // escaped, no dependsOn
			]);

		// Act
		var result = TemplateExpressionValidator.ValidateOrchestration(orchestration);

		// Assert
		result.IsValid.Should().BeTrue(result.FormatErrors());
	}

	[Fact]
	public void ValidateOrchestration_EscapedNonExistentStep_NoError()
	{
		// Arrange — even references to steps that DO NOT EXIST in the
		// orchestration are tolerated when escaped. The author has explicitly
		// asked for the literal form.
		var orchestration = CreateOrchestration(
			steps:
			[
				CreateTransformStep("step1", @"document the placeholder \{{ghost.output}}"),
			]);

		// Act
		var result = TemplateExpressionValidator.ValidateOrchestration(orchestration);

		// Assert
		result.IsValid.Should().BeTrue(result.FormatErrors());
	}

	[Fact]
	public void ValidateOrchestration_EscapedUnknownNamespace_NoError()
	{
		// Arrange — without an escape, {{foo.bar}} is "Unknown expression namespace 'foo'".
		// With an escape, it is a literal and must be accepted as-is.
		var orchestration = CreateOrchestration(
			steps:
			[
				CreateTransformStep("step1", @"see \{{foo.bar}} in the documentation"),
			]);

		// Act
		var result = TemplateExpressionValidator.ValidateOrchestration(orchestration);

		// Assert
		result.IsValid.Should().BeTrue(result.FormatErrors());
	}

	[Fact]
	public void ValidateOrchestration_EscapedExpression_DoesNotCountAsCircularVarReference()
	{
		// Arrange — a variable value contains \{{vars.self}} which LOOKS like a
		// circular self-reference. Because the escape makes this a literal,
		// circular-reference detection must skip it.
		var orchestration = CreateOrchestration(
			variables: new Dictionary<string, string>
			{
				["self"] = @"This is the literal placeholder \{{vars.self}}",
			});

		// Act
		var result = TemplateExpressionValidator.ValidateOrchestration(orchestration);

		// Assert
		result.IsValid.Should().BeTrue(result.FormatErrors());
	}

	[Fact]
	public void ValidateOrchestration_MixedEscapedAndRealReferences_OnlyRealOnesValidated()
	{
		// Arrange — `step2` legitimately references `step1.output` (with a
		// proper dependsOn) AND has an escaped `\{{ghost.output}}` for
		// documentation. Only the real reference matters; the escape is ignored.
		var orchestration = CreateOrchestration(
			steps:
			[
				CreateTransformStep("step1", "Hello"),
				CreateTransformStep(
					"step2",
					@"Real: {{step1.output}}, Doc: \{{ghost.output}}",
					dependsOn: ["step1"]),
			]);

		// Act
		var result = TemplateExpressionValidator.ValidateOrchestration(orchestration);

		// Assert
		result.IsValid.Should().BeTrue(result.FormatErrors());
	}

	[Fact]
	public void ValidateRuntime_EscapedEnvVar_DoesNotRequireEnvVarToBeSet()
	{
		// Arrange — without an escape, ValidateRuntime would reject the
		// orchestration if ORCHESTRA_NEVER_SET is not in the environment. With
		// an escape, the env var is never read at runtime, so it doesn't have
		// to exist.
		var envName = "ORCHESTRA_ESCAPE_RUNTIME_TEST_" + Guid.NewGuid().ToString("N")[..8];
		Environment.SetEnvironmentVariable(envName, null); // ensure unset
		var orchestration = CreateOrchestration(
			steps: [CreateTransformStep("step1", @"see \{{env." + envName + "}} in the docs")]);

		// Act
		var result = TemplateExpressionValidator.ValidateRuntime(orchestration, parameters: null);

		// Assert
		result.IsValid.Should().BeTrue(result.FormatErrors());
	}

	[Fact]
	public void ValidateRuntime_UnescapedMissingEnvVar_StillReturnsError()
	{
		// Arrange — pins the back-compat behavior: only escaped env references
		// are skipped. Unescaped ones still go through the must-be-set check.
		var envName = "ORCHESTRA_RUNTIME_MUST_BE_MISSING_" + Guid.NewGuid().ToString("N")[..8];
		Environment.SetEnvironmentVariable(envName, null);
		var orchestration = CreateOrchestration(
			steps: [CreateTransformStep("step1", "{{env." + envName + "}}")]);

		// Act
		var result = TemplateExpressionValidator.ValidateRuntime(orchestration, parameters: null);

		// Assert
		result.IsValid.Should().BeFalse();
		result.Errors.Should().Contain(e =>
			e.Message.Contains(envName) && e.Message.Contains("not set"));
	}

	#endregion
}
