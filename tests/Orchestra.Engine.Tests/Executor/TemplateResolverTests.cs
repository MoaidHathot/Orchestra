using FluentAssertions;

namespace Orchestra.Engine.Tests.Executor;

public class TemplateResolverTests
{
	private static readonly OrchestrationInfo s_defaultInfo = new("test-orchestration", "1.0.0", "run123", DateTimeOffset.UtcNow);

	private static readonly TransformOrchestrationStep s_defaultStep = new()
	{
		Name = "current-step",
		Type = OrchestrationStepType.Transform,
		DependsOn = [],
		Template = ""
	};

	[Fact]
	public void Resolve_ParameterExpression_ReplacesWithValue()
	{
		// Arrange
		var context = new OrchestrationExecutionContext
		{
			OrchestrationInfo = s_defaultInfo,
			Parameters = new Dictionary<string, string> { ["topic"] = "AI" }
		};
		var parameters = new Dictionary<string, string> { ["topic"] = "AI" };
		var template = "Write about {{param.topic}}";

		// Act
		var result = TemplateResolver.Resolve(template, parameters, context, [], s_defaultStep);

		// Assert
		result.Should().Be("Write about AI");
	}

	[Fact]
	public void Resolve_MultipleParameters_ReplacesAll()
	{
		// Arrange
		var context = new OrchestrationExecutionContext
		{
			OrchestrationInfo = s_defaultInfo,
			Parameters = new Dictionary<string, string>
			{
				["topic"] = "AI",
				["tone"] = "formal",
				["length"] = "500 words"
			}
		};
		var parameters = new Dictionary<string, string>
		{
			["topic"] = "AI",
			["tone"] = "formal",
			["length"] = "500 words"
		};
		var template = "Write about {{param.topic}} in a {{param.tone}} tone, approximately {{param.length}}";

		// Act
		var result = TemplateResolver.Resolve(template, parameters, context, [], s_defaultStep);

		// Assert
		result.Should().Be("Write about AI in a formal tone, approximately 500 words");
	}

	[Fact]
	public void Resolve_StepOutputExpression_ReplacesWithContent()
	{
		// Arrange
		var context = new OrchestrationExecutionContext
		{
			OrchestrationInfo = s_defaultInfo,
			Parameters = new Dictionary<string, string>()
		};
		context.AddResult("step1", ExecutionResult.Succeeded("processed content", rawContent: "raw content"));
		var template = "Use the output: {{step1.output}}";

		// Act
		var result = TemplateResolver.Resolve(template, new Dictionary<string, string>(), context, ["step1"], s_defaultStep);

		// Assert
		result.Should().Be("Use the output: processed content");
	}

	[Fact]
	public void Resolve_StepRawOutputExpression_ReplacesWithRawContent()
	{
		// Arrange
		var context = new OrchestrationExecutionContext
		{
			OrchestrationInfo = s_defaultInfo,
			Parameters = new Dictionary<string, string>()
		};
		context.AddResult("step1", ExecutionResult.Succeeded("processed content", rawContent: "raw content"));
		var template = "Use the raw output: {{step1.rawOutput}}";

		// Act
		var result = TemplateResolver.Resolve(template, new Dictionary<string, string>(), context, ["step1"], s_defaultStep);

		// Assert
		result.Should().Be("Use the raw output: raw content");
	}

	[Fact]
	public void Resolve_StepRawOutput_FallsBackToContent_WhenRawContentNull()
	{
		// Arrange
		var context = new OrchestrationExecutionContext
		{
			OrchestrationInfo = s_defaultInfo,
			Parameters = new Dictionary<string, string>()
		};
		context.AddResult("step1", ExecutionResult.Succeeded("processed content"));
		var template = "Use the raw output: {{step1.rawOutput}}";

		// Act
		var result = TemplateResolver.Resolve(template, new Dictionary<string, string>(), context, ["step1"], s_defaultStep);

		// Assert
		result.Should().Be("Use the raw output: processed content");
	}

	[Fact]
	public void Resolve_UnknownParameter_LeavesAsIs()
	{
		// Arrange
		var context = new OrchestrationExecutionContext
		{
			OrchestrationInfo = s_defaultInfo,
			Parameters = new Dictionary<string, string>()
		};
		var parameters = new Dictionary<string, string>();
		var template = "Value is {{param.unknown}}";

		// Act
		var result = TemplateResolver.Resolve(template, parameters, context, [], s_defaultStep);

		// Assert
		result.Should().Be("Value is {{param.unknown}}");
	}

	[Fact]
	public void Resolve_UnknownStep_LeavesAsIs()
	{
		// Arrange
		var context = new OrchestrationExecutionContext
		{
			OrchestrationInfo = s_defaultInfo,
			Parameters = new Dictionary<string, string>()
		};
		var template = "Value is {{unknown.output}}";

		// Act
		var result = TemplateResolver.Resolve(template, new Dictionary<string, string>(), context, [], s_defaultStep);

		// Assert
		result.Should().Be("Value is {{unknown.output}}");
	}

	[Fact]
	public void Resolve_MixedExpressions_ResolvesAll()
	{
		// Arrange
		var context = new OrchestrationExecutionContext
		{
			OrchestrationInfo = s_defaultInfo,
			Parameters = new Dictionary<string, string> { ["topic"] = "AI" }
		};
		var parameters = new Dictionary<string, string> { ["topic"] = "AI" };
		context.AddResult("research", ExecutionResult.Succeeded("research findings"));
		context.AddResult("outline", ExecutionResult.Succeeded("document outline", rawContent: "raw outline"));
		var template = "Write about {{param.topic}} using {{research.output}} and follow {{outline.rawOutput}}";

		// Act
		var result = TemplateResolver.Resolve(template, parameters, context, ["research", "outline"], s_defaultStep);

		// Assert
		result.Should().Be("Write about AI using research findings and follow raw outline");
	}

	[Fact]
	public void Resolve_NoExpressions_ReturnsUnchanged()
	{
		// Arrange
		var context = new OrchestrationExecutionContext
		{
			OrchestrationInfo = s_defaultInfo,
			Parameters = new Dictionary<string, string>()
		};
		var template = "This is a plain text template with no expressions.";

		// Act
		var result = TemplateResolver.Resolve(template, new Dictionary<string, string>(), context, [], s_defaultStep);

		// Assert
		result.Should().Be("This is a plain text template with no expressions.");
	}

	[Fact]
	public void Resolve_NonDependencyStep_FallsBackToTryGetResult()
	{
		// Arrange
		var context = new OrchestrationExecutionContext
		{
			OrchestrationInfo = s_defaultInfo,
			Parameters = new Dictionary<string, string>()
		};
		context.AddResult("otherStep", ExecutionResult.Succeeded("fallback content", rawContent: "fallback raw"));
		var template = "Use {{otherStep.output}} and {{otherStep.rawOutput}}";

		// Act — otherStep is NOT in dependsOn, but exists in context
		var result = TemplateResolver.Resolve(template, new Dictionary<string, string>(), context, ["unrelatedStep"], s_defaultStep);

		// Assert — falls back to TryGetResult path
		result.Should().Be("Use fallback content and fallback raw");
	}

	#region Step Output JSON-Path

	/// <summary>
	/// <c>{{stepName.output.field}}</c> extracts a scalar field from a step's JSON output.
	/// String fields are returned without surrounding quotes; numeric/boolean fields are
	/// returned in their canonical JSON form. This lets a Script step emit a single JSON
	/// object that downstream consumers can pluck individual fields from without needing
	/// separate output channels (used by <c>run-self-healing.yaml</c>'s
	/// <c>controllerMcpTimeoutSeconds</c> extraction).
	/// </summary>
	[Fact]
	public void Resolve_StepOutputJsonPath_NumericField_ResolvesToCanonicalForm()
	{
		// Arrange
		var context = new OrchestrationExecutionContext
		{
			OrchestrationInfo = s_defaultInfo,
			Parameters = new Dictionary<string, string>(),
		};
		context.AddResult("validate-inputs", ExecutionResult.Succeeded(
			"{\"childWaitTimeoutSeconds\":21660,\"controllerMcpTimeoutSeconds\":21960}"));
		var template = "Budget: {{validate-inputs.output.controllerMcpTimeoutSeconds}}s";

		// Act
		var result = TemplateResolver.Resolve(template, context.Parameters, context, ["validate-inputs"], s_defaultStep);

		// Assert
		result.Should().Be("Budget: 21960s");
	}

	[Fact]
	public void Resolve_StepOutputJsonPath_StringField_ReturnedWithoutQuotes()
	{
		// Arrange
		var context = new OrchestrationExecutionContext
		{
			OrchestrationInfo = s_defaultInfo,
			Parameters = new Dictionary<string, string>(),
		};
		context.AddResult("setup", ExecutionResult.Succeeded(
			"{\"endpoint\":\"http://example.com\",\"mode\":\"sync\"}"));
		var template = "Endpoint: {{setup.output.endpoint}}";

		// Act
		var result = TemplateResolver.Resolve(template, context.Parameters, context, ["setup"], s_defaultStep);

		// Assert
		result.Should().Be("Endpoint: http://example.com");
	}

	[Fact]
	public void Resolve_StepOutputJsonPath_BooleanField_ResolvesToCanonicalForm()
	{
		var context = new OrchestrationExecutionContext
		{
			OrchestrationInfo = s_defaultInfo,
			Parameters = new Dictionary<string, string>(),
		};
		context.AddResult("config", ExecutionResult.Succeeded("{\"enabled\":true,\"verbose\":false}"));
		var template = "{{config.output.enabled}}, {{config.output.verbose}}";

		var result = TemplateResolver.Resolve(template, context.Parameters, context, ["config"], s_defaultStep);

		result.Should().Be("true, false");
	}

	[Fact]
	public void Resolve_StepOutputJsonPath_NestedField_WalksDottedPath()
	{
		var context = new OrchestrationExecutionContext
		{
			OrchestrationInfo = s_defaultInfo,
			Parameters = new Dictionary<string, string>(),
		};
		context.AddResult("data", ExecutionResult.Succeeded(
			"{\"runtime\":{\"limits\":{\"timeoutSeconds\":21960}}}"));
		var template = "{{data.output.runtime.limits.timeoutSeconds}}";

		var result = TemplateResolver.Resolve(template, context.Parameters, context, ["data"], s_defaultStep);

		result.Should().Be("21960");
	}

	[Fact]
	public void Resolve_StepOutputJsonPath_MissingField_LeavesTemplateAsLiteral()
	{
		var context = new OrchestrationExecutionContext
		{
			OrchestrationInfo = s_defaultInfo,
			Parameters = new Dictionary<string, string>(),
		};
		context.AddResult("data", ExecutionResult.Succeeded("{\"a\":1,\"b\":2}"));
		var template = "X={{data.output.missing}}";

		var result = TemplateResolver.Resolve(template, context.Parameters, context, ["data"], s_defaultStep);

		result.Should().Be("X={{data.output.missing}}");
	}

	[Fact]
	public void Resolve_StepOutputJsonPath_NonJsonOutput_LeavesTemplateAsLiteral()
	{
		var context = new OrchestrationExecutionContext
		{
			OrchestrationInfo = s_defaultInfo,
			Parameters = new Dictionary<string, string>(),
		};
		context.AddResult("data", ExecutionResult.Succeeded("not json at all"));
		var template = "X={{data.output.field}}";

		var result = TemplateResolver.Resolve(template, context.Parameters, context, ["data"], s_defaultStep);

		result.Should().Be("X={{data.output.field}}");
	}

	[Fact]
	public void Resolve_StepOutputJsonPath_FieldIsObject_LeavesTemplateAsLiteral()
	{
		// A complex object is not a usable substitution leaf — substituting raw JSON into
		// a template position is rarely what authors mean, so we treat it as unresolved.
		var context = new OrchestrationExecutionContext
		{
			OrchestrationInfo = s_defaultInfo,
			Parameters = new Dictionary<string, string>(),
		};
		context.AddResult("data", ExecutionResult.Succeeded("{\"nested\":{\"a\":1}}"));
		var template = "X={{data.output.nested}}";

		var result = TemplateResolver.Resolve(template, context.Parameters, context, ["data"], s_defaultStep);

		result.Should().Be("X={{data.output.nested}}");
	}

	[Fact]
	public void Resolve_StepOutputJsonPath_CaseInsensitiveFieldNames()
	{
		var context = new OrchestrationExecutionContext
		{
			OrchestrationInfo = s_defaultInfo,
			Parameters = new Dictionary<string, string>(),
		};
		context.AddResult("data", ExecutionResult.Succeeded("{\"controllerMcpTimeoutSeconds\":21960}"));
		var template = "{{data.output.CONTROLLERMCPTIMEOUTSECONDS}}";

		var result = TemplateResolver.Resolve(template, context.Parameters, context, ["data"], s_defaultStep);

		result.Should().Be("21960");
	}

	#endregion

	#region Orchestration Namespace

	[Fact]
	public void Resolve_OrchestrationName_ReturnsName()
	{
		// Arrange
		var info = new OrchestrationInfo("my-pipeline", "2.0.0", "run-abc", DateTimeOffset.UtcNow);
		var context = new OrchestrationExecutionContext
		{
			OrchestrationInfo = info,
			Parameters = new Dictionary<string, string>()
		};
		var template = "Running {{orchestration.name}}";

		// Act
		var result = TemplateResolver.Resolve(template, [], context, [], s_defaultStep);

		// Assert
		result.Should().Be("Running my-pipeline");
	}

	[Fact]
	public void Resolve_OrchestrationVersion_ReturnsVersion()
	{
		// Arrange
		var info = new OrchestrationInfo("pipeline", "3.1.0", "run-1", DateTimeOffset.UtcNow);
		var context = new OrchestrationExecutionContext
		{
			OrchestrationInfo = info,
			Parameters = new Dictionary<string, string>()
		};
		var template = "Version: {{orchestration.version}}";

		// Act
		var result = TemplateResolver.Resolve(template, [], context, [], s_defaultStep);

		// Assert
		result.Should().Be("Version: 3.1.0");
	}

	[Fact]
	public void Resolve_OrchestrationRunId_ReturnsRunId()
	{
		// Arrange
		var info = new OrchestrationInfo("pipeline", "1.0.0", "run-xyz-789", DateTimeOffset.UtcNow);
		var context = new OrchestrationExecutionContext
		{
			OrchestrationInfo = info,
			Parameters = new Dictionary<string, string>()
		};
		var template = "Run: {{orchestration.runId}}";

		// Act
		var result = TemplateResolver.Resolve(template, [], context, [], s_defaultStep);

		// Assert
		result.Should().Be("Run: run-xyz-789");
	}

	[Fact]
	public void Resolve_OrchestrationStartedAt_ReturnsIso8601()
	{
		// Arrange
		var startedAt = new DateTimeOffset(2025, 6, 15, 10, 30, 0, TimeSpan.Zero);
		var info = new OrchestrationInfo("pipeline", "1.0.0", "run-1", startedAt);
		var context = new OrchestrationExecutionContext
		{
			OrchestrationInfo = info,
			Parameters = new Dictionary<string, string>()
		};
		var template = "Started: {{orchestration.startedAt}}";

		// Act
		var result = TemplateResolver.Resolve(template, [], context, [], s_defaultStep);

		// Assert
		result.Should().Be($"Started: {startedAt:o}");
	}

	[Fact]
	public void Resolve_OrchestrationTempDir_ReturnsTempDirectory()
	{
		// Arrange
		var tempRoot = Path.Combine(Path.GetTempPath(), $"orchestra-test-{Guid.NewGuid():N}");
		try
		{
			var store = new OrchestrationTempFileStore(tempRoot, "my-pipeline", "run-abc");
			var info = new OrchestrationInfo("my-pipeline", "2.0.0", "run-abc", DateTimeOffset.UtcNow);
			var context = new OrchestrationExecutionContext
			{
				OrchestrationInfo = info,
				Parameters = new Dictionary<string, string>(),
				TempFileStore = store
			};
			var template = "TempDir: {{orchestration.tempDir}}";

			// Act
			var result = TemplateResolver.Resolve(template, [], context, [], s_defaultStep);

			// Assert
			result.Should().Be($"TempDir: {store.TempDirectory}");
		}
		finally
		{
			if (Directory.Exists(tempRoot))
				Directory.Delete(tempRoot, recursive: true);
		}
	}

	[Fact]
	public void Resolve_OrchestrationTempDir_NoStore_ReturnsEmptyString()
	{
		// Arrange
		var info = new OrchestrationInfo("my-pipeline", "2.0.0", "run-abc", DateTimeOffset.UtcNow);
		var context = new OrchestrationExecutionContext
		{
			OrchestrationInfo = info,
			Parameters = new Dictionary<string, string>(),
			TempFileStore = null
		};
		var template = "TempDir: {{orchestration.tempDir}}";

		// Act
		var result = TemplateResolver.Resolve(template, [], context, [], s_defaultStep);

		// Assert
		result.Should().Be("TempDir: ");
	}

	[Fact]
	public void Resolve_OrchestrationSourcePathAndDirectory_ReturnsSourceMetadata()
	{
		// Arrange
		var sourcePath = Path.GetFullPath(Path.Combine("workspace", "orchestrations", "System", "run-ephermal.yaml"));
		var sourceDirectory = Path.GetDirectoryName(sourcePath)!;
		var info = new OrchestrationInfo("my-pipeline", "2.0.0", "run-abc", DateTimeOffset.UtcNow, sourcePath, sourceDirectory);
		var context = new OrchestrationExecutionContext
		{
			OrchestrationInfo = info,
			Parameters = new Dictionary<string, string>()
		};
		var template = "Source: {{orchestration.sourcePath}} Dir: {{orchestration.sourceDirectory}}";

		// Act
		var result = TemplateResolver.Resolve(template, [], context, [], s_defaultStep);

		// Assert
		result.Should().Be($"Source: {sourcePath} Dir: {sourceDirectory}");
	}

	[Fact]
	public void Resolve_OrchestrationSourcePathAndDirectory_NoSource_ReturnsEmptyStrings()
	{
		// Arrange
		var context = new OrchestrationExecutionContext
		{
			OrchestrationInfo = s_defaultInfo,
			Parameters = new Dictionary<string, string>()
		};
		var template = "[{{orchestration.sourcePath}}][{{orchestration.sourceDirectory}}]";

		// Act
		var result = TemplateResolver.Resolve(template, [], context, [], s_defaultStep);

		// Assert
		result.Should().Be("[][]");
	}

	[Fact]
	public void Resolve_OrchestrationAllProperties_ResolvesAll()
	{
		// Arrange
		var startedAt = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
		var sourcePath = Path.GetFullPath(Path.Combine("workspace", "full-test.yaml"));
		var sourceDirectory = Path.GetDirectoryName(sourcePath)!;
		var info = new OrchestrationInfo("full-test", "5.0.0", "run-full", startedAt, sourcePath, sourceDirectory);
		var context = new OrchestrationExecutionContext
		{
			OrchestrationInfo = info,
			Parameters = new Dictionary<string, string>()
		};
		var template = "{{orchestration.name}} v{{orchestration.version}} [{{orchestration.runId}}] at {{orchestration.startedAt}} from {{orchestration.sourceDirectory}}";

		// Act
		var result = TemplateResolver.Resolve(template, [], context, [], s_defaultStep);

		// Assert
		result.Should().Be($"full-test v5.0.0 [run-full] at {startedAt:o} from {sourceDirectory}");
	}

	[Fact]
	public void Resolve_OrchestrationUnknownProperty_Throws()
	{
		// Arrange
		var context = new OrchestrationExecutionContext
		{
			OrchestrationInfo = s_defaultInfo,
			Parameters = new Dictionary<string, string>()
		};
		var template = "{{orchestration.invalid}}";

		// Act
		var act = () => TemplateResolver.Resolve(template, [], context, [], s_defaultStep);

		// Assert
		act.Should().Throw<InvalidOperationException>()
			.WithMessage("*Unknown orchestration property*invalid*");
	}

	#endregion

	#region Step Namespace

	[Fact]
	public void Resolve_StepName_ReturnsCurrentStepName()
	{
		// Arrange
		var step = new TransformOrchestrationStep
		{
			Name = "data-transform",
			Type = OrchestrationStepType.Transform,
			DependsOn = [],
			Template = ""
		};
		var context = new OrchestrationExecutionContext
		{
			OrchestrationInfo = s_defaultInfo,
			Parameters = new Dictionary<string, string>()
		};
		var template = "Executing step: {{step.name}}";

		// Act
		var result = TemplateResolver.Resolve(template, [], context, [], step);

		// Assert
		result.Should().Be("Executing step: data-transform");
	}

	[Fact]
	public void Resolve_StepType_ReturnsCurrentStepType()
	{
		// Arrange
		var step = new CommandOrchestrationStep
		{
			Name = "build",
			Type = OrchestrationStepType.Command,
			DependsOn = [],
			Command = "dotnet"
		};
		var context = new OrchestrationExecutionContext
		{
			OrchestrationInfo = s_defaultInfo,
			Parameters = new Dictionary<string, string>()
		};
		var template = "Step type: {{step.type}}";

		// Act
		var result = TemplateResolver.Resolve(template, [], context, [], step);

		// Assert
		result.Should().Be("Step type: Command");
	}

	[Fact]
	public void Resolve_StepUnknownProperty_Throws()
	{
		// Arrange
		var context = new OrchestrationExecutionContext
		{
			OrchestrationInfo = s_defaultInfo,
			Parameters = new Dictionary<string, string>()
		};
		var template = "{{step.invalid}}";

		// Act
		var act = () => TemplateResolver.Resolve(template, [], context, [], s_defaultStep);

		// Assert
		act.Should().Throw<InvalidOperationException>()
			.WithMessage("*Unknown step property*invalid*");
	}

	#endregion

	#region Vars Namespace

	[Fact]
	public void Resolve_VarsSimple_ReturnsVariableValue()
	{
		// Arrange
		var context = new OrchestrationExecutionContext
		{
			OrchestrationInfo = s_defaultInfo,
			Parameters = new Dictionary<string, string>(),
			Variables = new Dictionary<string, string> { ["outputDir"] = "/reports" }
		};
		var template = "Save to {{vars.outputDir}}";

		// Act
		var result = TemplateResolver.Resolve(template, [], context, [], s_defaultStep);

		// Assert
		result.Should().Be("Save to /reports");
	}

	[Fact]
	public void Resolve_VarsWithParamExpansion_ResolvesRecursively()
	{
		// Arrange
		var parameters = new Dictionary<string, string> { ["project"] = "myapp" };
		var context = new OrchestrationExecutionContext
		{
			OrchestrationInfo = s_defaultInfo,
			Parameters = parameters,
			Variables = new Dictionary<string, string>
			{
				["outputDir"] = "/reports/{{param.project}}"
			}
		};
		var template = "Save to {{vars.outputDir}}";

		// Act
		var result = TemplateResolver.Resolve(template, parameters, context, [], s_defaultStep);

		// Assert
		result.Should().Be("Save to /reports/myapp");
	}

	[Fact]
	public void Resolve_VarsChained_ResolvesTransitively()
	{
		// Arrange
		var parameters = new Dictionary<string, string> { ["env"] = "prod" };
		var context = new OrchestrationExecutionContext
		{
			OrchestrationInfo = s_defaultInfo,
			Parameters = parameters,
			Variables = new Dictionary<string, string>
			{
				["baseDir"] = "/data/{{param.env}}",
				["outputDir"] = "{{vars.baseDir}}/reports"
			}
		};
		var template = "Writing to {{vars.outputDir}}";

		// Act
		var result = TemplateResolver.Resolve(template, parameters, context, [], s_defaultStep);

		// Assert
		result.Should().Be("Writing to /data/prod/reports");
	}

	[Fact]
	public void Resolve_VarsCircularReference_LeavesAsIs()
	{
		// Arrange
		var context = new OrchestrationExecutionContext
		{
			OrchestrationInfo = s_defaultInfo,
			Parameters = new Dictionary<string, string>(),
			Variables = new Dictionary<string, string>
			{
				["a"] = "{{vars.b}}",
				["b"] = "{{vars.a}}"
			}
		};
		var template = "Value: {{vars.a}}";

		// Act — should not throw or infinite-loop
		var result = TemplateResolver.Resolve(template, [], context, [], s_defaultStep);

		// Assert — circular reference leaves the inner expression as-is
		result.Should().Be("Value: {{vars.a}}");
	}

	[Fact]
	public void Resolve_VarsSelfReference_LeavesAsIs()
	{
		// Arrange
		var context = new OrchestrationExecutionContext
		{
			OrchestrationInfo = s_defaultInfo,
			Parameters = new Dictionary<string, string>(),
			Variables = new Dictionary<string, string>
			{
				["x"] = "prefix-{{vars.x}}"
			}
		};
		var template = "{{vars.x}}";

		// Act
		var result = TemplateResolver.Resolve(template, [], context, [], s_defaultStep);

		// Assert — self-reference is left as-is
		result.Should().Be("prefix-{{vars.x}}");
	}

	[Fact]
	public void Resolve_VarsUnknown_LeavesAsIs()
	{
		// Arrange
		var context = new OrchestrationExecutionContext
		{
			OrchestrationInfo = s_defaultInfo,
			Parameters = new Dictionary<string, string>(),
			Variables = new Dictionary<string, string>()
		};
		var template = "Value: {{vars.nonexistent}}";

		// Act
		var result = TemplateResolver.Resolve(template, [], context, [], s_defaultStep);

		// Assert
		result.Should().Be("Value: {{vars.nonexistent}}");
	}

	[Fact]
	public void Resolve_VarsWithOrchestrationExpression_ResolvesRecursively()
	{
		// Arrange
		var info = new OrchestrationInfo("my-pipeline", "1.0.0", "run-42", DateTimeOffset.UtcNow);
		var context = new OrchestrationExecutionContext
		{
			OrchestrationInfo = info,
			Parameters = new Dictionary<string, string>(),
			Variables = new Dictionary<string, string>
			{
				["logFile"] = "/logs/{{orchestration.name}}/{{orchestration.runId}}.log"
			}
		};
		var template = "Log: {{vars.logFile}}";

		// Act
		var result = TemplateResolver.Resolve(template, [], context, [], s_defaultStep);

		// Assert
		result.Should().Be("Log: /logs/my-pipeline/run-42.log");
	}

	#endregion

	#region Mixed Namespace Expressions

	[Fact]
	public void Resolve_MixedNamespaces_ResolvesAllNamespaces()
	{
		// Arrange
		var info = new OrchestrationInfo("deploy-pipeline", "2.0.0", "run-999", DateTimeOffset.UtcNow);
		var step = new CommandOrchestrationStep
		{
			Name = "deploy",
			Type = OrchestrationStepType.Command,
			DependsOn = [],
			Command = "deploy.sh"
		};
		var parameters = new Dictionary<string, string> { ["env"] = "staging" };
		var context = new OrchestrationExecutionContext
		{
			OrchestrationInfo = info,
			Parameters = parameters,
			Variables = new Dictionary<string, string>
			{
				["region"] = "us-west-2"
			}
		};
		context.AddResult("build", ExecutionResult.Succeeded("build-ok"));

		var template = "{{orchestration.name}} [{{step.name}}] deploying to {{param.env}}/{{vars.region}} with {{build.output}}";

		// Act
		var result = TemplateResolver.Resolve(template, parameters, context, ["build"], step);

		// Assert
		result.Should().Be("deploy-pipeline [deploy] deploying to staging/us-west-2 with build-ok");
	}

	#endregion

	#region Env Namespace

	[Fact]
	public void Resolve_EnvExistingVariable_ReturnsValue()
	{
		// Arrange
		Environment.SetEnvironmentVariable("ORCHESTRA_TEST_VAR", "test-value-123");
		try
		{
			var context = new OrchestrationExecutionContext
			{
				OrchestrationInfo = s_defaultInfo,
				Parameters = new Dictionary<string, string>()
			};
			var template = "Value: {{env.ORCHESTRA_TEST_VAR}}";

			// Act
			var result = TemplateResolver.Resolve(template, [], context, [], s_defaultStep);

			// Assert
			result.Should().Be("Value: test-value-123");
		}
		finally
		{
			Environment.SetEnvironmentVariable("ORCHESTRA_TEST_VAR", null);
		}
	}

	[Fact]
	public void Resolve_EnvMissingVariable_LeavesAsIs()
	{
		// Arrange
		// Ensure the variable does not exist
		Environment.SetEnvironmentVariable("ORCHESTRA_NONEXISTENT_VAR", null);
		var context = new OrchestrationExecutionContext
		{
			OrchestrationInfo = s_defaultInfo,
			Parameters = new Dictionary<string, string>()
		};
		var template = "Value: {{env.ORCHESTRA_NONEXISTENT_VAR}}";

		// Act
		var result = TemplateResolver.Resolve(template, [], context, [], s_defaultStep);

		// Assert
		result.Should().Be("Value: {{env.ORCHESTRA_NONEXISTENT_VAR}}");
	}

	[Fact]
	public void Resolve_EnvMultipleVariables_ResolvesAll()
	{
		// Arrange
		Environment.SetEnvironmentVariable("ORCHESTRA_TEST_HOST", "db.example.com");
		Environment.SetEnvironmentVariable("ORCHESTRA_TEST_PORT", "5432");
		try
		{
			var context = new OrchestrationExecutionContext
			{
				OrchestrationInfo = s_defaultInfo,
				Parameters = new Dictionary<string, string>()
			};
			var template = "Connection: {{env.ORCHESTRA_TEST_HOST}}:{{env.ORCHESTRA_TEST_PORT}}";

			// Act
			var result = TemplateResolver.Resolve(template, [], context, [], s_defaultStep);

			// Assert
			result.Should().Be("Connection: db.example.com:5432");
		}
		finally
		{
			Environment.SetEnvironmentVariable("ORCHESTRA_TEST_HOST", null);
			Environment.SetEnvironmentVariable("ORCHESTRA_TEST_PORT", null);
		}
	}

	[Fact]
	public void Resolve_EnvEmptyValue_ResolvesToEmptyString()
	{
		// Arrange
		Environment.SetEnvironmentVariable("ORCHESTRA_TEST_EMPTY", "");
		try
		{
			var context = new OrchestrationExecutionContext
			{
				OrchestrationInfo = s_defaultInfo,
				Parameters = new Dictionary<string, string>()
			};
			var template = "Before[{{env.ORCHESTRA_TEST_EMPTY}}]After";

			// Act
			var result = TemplateResolver.Resolve(template, [], context, [], s_defaultStep);

			// Assert
			result.Should().Be("Before[]After");
		}
		finally
		{
			Environment.SetEnvironmentVariable("ORCHESTRA_TEST_EMPTY", null);
		}
	}

	[Fact]
	public void Resolve_EnvInVarsRecursiveExpansion_Resolves()
	{
		// Arrange
		Environment.SetEnvironmentVariable("ORCHESTRA_TEST_DB_HOST", "prod-db.internal");
		try
		{
			var context = new OrchestrationExecutionContext
			{
				OrchestrationInfo = s_defaultInfo,
				Parameters = new Dictionary<string, string>(),
				Variables = new Dictionary<string, string>
				{
					["connectionString"] = "Server={{env.ORCHESTRA_TEST_DB_HOST}};Database=mydb"
				}
			};
			var template = "{{vars.connectionString}}";

			// Act
			var result = TemplateResolver.Resolve(template, [], context, [], s_defaultStep);

			// Assert
			result.Should().Be("Server=prod-db.internal;Database=mydb");
		}
		finally
		{
			Environment.SetEnvironmentVariable("ORCHESTRA_TEST_DB_HOST", null);
		}
	}

	[Fact]
	public void Resolve_EnvMixedWithOtherNamespaces_ResolvesAll()
	{
		// Arrange
		Environment.SetEnvironmentVariable("ORCHESTRA_TEST_API_KEY", "sk-abc123");
		try
		{
			var info = new OrchestrationInfo("api-pipeline", "1.0.0", "run-1", DateTimeOffset.UtcNow);
			var parameters = new Dictionary<string, string> { ["endpoint"] = "/users" };
			var context = new OrchestrationExecutionContext
			{
				OrchestrationInfo = info,
				Parameters = parameters,
				Variables = new Dictionary<string, string>
				{
					["baseUrl"] = "https://api.example.com"
				}
			};
			var template = "{{vars.baseUrl}}{{param.endpoint}} [{{orchestration.name}}] key={{env.ORCHESTRA_TEST_API_KEY}}";

			// Act
			var result = TemplateResolver.Resolve(template, parameters, context, [], s_defaultStep);

			// Assert
			result.Should().Be("https://api.example.com/users [api-pipeline] key=sk-abc123");
		}
		finally
		{
			Environment.SetEnvironmentVariable("ORCHESTRA_TEST_API_KEY", null);
		}
	}

	#endregion

	#region Step Files Namespace (Fix #6)

	[Fact]
	public void Resolve_StepFiles_ReturnsJsonArray()
	{
		// Arrange
		var tempRoot = Path.Combine(Path.GetTempPath(), $"orchestra-test-{Guid.NewGuid():N}");
		try
		{
			var store = new OrchestrationTempFileStore(tempRoot, "orch", "run-1");
			var file1 = store.SaveFile("content1", "research", "txt");
			var file2 = store.SaveFile("content2", "research", "json");

			var context = new OrchestrationExecutionContext
			{
				OrchestrationInfo = s_defaultInfo,
				Parameters = new Dictionary<string, string>(),
				TempFileStore = store
			};
			var template = "Files: {{research.files}}";

			// Act
			var result = TemplateResolver.Resolve(template, [], context, ["research"], s_defaultStep);

			// Assert — Should be a JSON array containing both file paths
			result.Should().StartWith("Files: [");
			// Deserialize to verify both paths are present (JSON escapes backslashes on Windows)
			var jsonPart = result["Files: ".Length..];
			var files = System.Text.Json.JsonSerializer.Deserialize<string[]>(jsonPart);
			files.Should().HaveCount(2);
			files.Should().Contain(file1);
			files.Should().Contain(file2);
		}
		finally
		{
			if (Directory.Exists(tempRoot))
				Directory.Delete(tempRoot, recursive: true);
		}
	}

	[Fact]
	public void Resolve_StepFilesIndex_ReturnsSpecificFile()
	{
		// Arrange
		var tempRoot = Path.Combine(Path.GetTempPath(), $"orchestra-test-{Guid.NewGuid():N}");
		try
		{
			var store = new OrchestrationTempFileStore(tempRoot, "orch", "run-1");
			var file1 = store.SaveFile("content1", "research", "txt");
			var file2 = store.SaveFile("content2", "research", "json");

			var context = new OrchestrationExecutionContext
			{
				OrchestrationInfo = s_defaultInfo,
				Parameters = new Dictionary<string, string>(),
				TempFileStore = store
			};
			var template = "First: {{research.files[0]}}";

			// Act
			var result = TemplateResolver.Resolve(template, [], context, ["research"], s_defaultStep);

			// Assert — Should contain one of the file paths (order depends on ConcurrentBag)
			result.Should().StartWith("First: ");
			var resolvedPath = result["First: ".Length..];
			resolvedPath.Should().StartWith(store.TempDirectory);
			// The path should be one of the two saved files
			new[] { file1, file2 }.Should().Contain(resolvedPath);
		}
		finally
		{
			if (Directory.Exists(tempRoot))
				Directory.Delete(tempRoot, recursive: true);
		}
	}

	[Fact]
	public void Resolve_StepFilesIndexOutOfRange_ReturnsEmpty()
	{
		// Arrange
		var tempRoot = Path.Combine(Path.GetTempPath(), $"orchestra-test-{Guid.NewGuid():N}");
		try
		{
			var store = new OrchestrationTempFileStore(tempRoot, "orch", "run-1");
			store.SaveFile("content", "research", "txt");

			var context = new OrchestrationExecutionContext
			{
				OrchestrationInfo = s_defaultInfo,
				Parameters = new Dictionary<string, string>(),
				TempFileStore = store
			};
			var template = "File: {{research.files[99]}}";

			// Act
			var result = TemplateResolver.Resolve(template, [], context, ["research"], s_defaultStep);

			// Assert — Out of range index returns empty string
			result.Should().Be("File: ");
		}
		finally
		{
			if (Directory.Exists(tempRoot))
				Directory.Delete(tempRoot, recursive: true);
		}
	}

	[Fact]
	public void Resolve_StepFilesNoFiles_ReturnsEmptyJsonArray()
	{
		// Arrange
		var tempRoot = Path.Combine(Path.GetTempPath(), $"orchestra-test-{Guid.NewGuid():N}");
		try
		{
			var store = new OrchestrationTempFileStore(tempRoot, "orch", "run-1");
			// No files saved for 'research'

			var context = new OrchestrationExecutionContext
			{
				OrchestrationInfo = s_defaultInfo,
				Parameters = new Dictionary<string, string>(),
				TempFileStore = store
			};
			var template = "Files: {{research.files}}";

			// Act
			var result = TemplateResolver.Resolve(template, [], context, ["research"], s_defaultStep);

			// Assert — Empty array
			result.Should().Be("Files: []");
		}
		finally
		{
			if (Directory.Exists(tempRoot))
				Directory.Delete(tempRoot, recursive: true);
		}
	}

	[Fact]
	public void Resolve_StepFilesNoStore_ReturnsEmptyJsonArray()
	{
		// Arrange — No TempFileStore configured
		var context = new OrchestrationExecutionContext
		{
			OrchestrationInfo = s_defaultInfo,
			Parameters = new Dictionary<string, string>(),
			TempFileStore = null
		};
		var template = "Files: {{research.files}}";

		// Act
		var result = TemplateResolver.Resolve(template, [], context, ["research"], s_defaultStep);

		// Assert — Returns empty array when no store
		result.Should().Be("Files: []");
	}

	[Theory]
	[InlineData("{{research.FILES}}", true)]
	[InlineData("{{research.Files}}", true)]
	[InlineData("{{research.files[0]}}", true)]
	[InlineData("{{research.FILES[0]}}", true)]
	public void Resolve_StepFilesCaseInsensitive_Resolves(string template, bool shouldResolve)
	{
		// Arrange
		var tempRoot = Path.Combine(Path.GetTempPath(), $"orchestra-test-{Guid.NewGuid():N}");
		try
		{
			var store = new OrchestrationTempFileStore(tempRoot, "orch", "run-1");
			store.SaveFile("content", "research", "txt");

			var context = new OrchestrationExecutionContext
			{
				OrchestrationInfo = s_defaultInfo,
				Parameters = new Dictionary<string, string>(),
				TempFileStore = store
			};

			// Act
			var result = TemplateResolver.Resolve(template, [], context, ["research"], s_defaultStep);

			// Assert
			if (shouldResolve)
			{
				result.Should().NotContain("{{");
			}
		}
		finally
		{
			if (Directory.Exists(tempRoot))
				Directory.Delete(tempRoot, recursive: true);
		}
	}

	#endregion

	#region Unresolved Template Tracking (Fix #4b)

	[Fact]
	public void Resolve_UnresolvedStepOutput_TracksExpression()
	{
		// Arrange
		var context = new OrchestrationExecutionContext
		{
			OrchestrationInfo = s_defaultInfo,
			Parameters = new Dictionary<string, string>()
		};
		// 'missing' step has no result in context
		var template = "Value is {{missing.output}}";

		// Act
		var result = TemplateResolver.Resolve(template, new Dictionary<string, string>(), context, [], s_defaultStep);

		// Assert — Expression should be left as-is
		result.Should().Be("Value is {{missing.output}}");
		// And it should be tracked as unresolved
		context.ResolutionTracker.UnresolvedExpressions.Should().HaveCount(1);
		context.ResolutionTracker.UnresolvedExpressions.First().Expression.Should().Be("{{missing.output}}");
		context.ResolutionTracker.UnresolvedExpressions.First().StepName.Should().Be("current-step");
	}

	[Fact]
	public void Resolve_UnresolvedStepRawOutput_TracksExpression()
	{
		// Arrange
		var context = new OrchestrationExecutionContext
		{
			OrchestrationInfo = s_defaultInfo,
			Parameters = new Dictionary<string, string>()
		};
		var template = "Value is {{missing.rawOutput}}";

		// Act
		var result = TemplateResolver.Resolve(template, new Dictionary<string, string>(), context, [], s_defaultStep);

		// Assert
		result.Should().Be("Value is {{missing.rawOutput}}");
		context.ResolutionTracker.UnresolvedExpressions.Should().HaveCount(1);
		context.ResolutionTracker.UnresolvedExpressions.First().Expression.Should().Be("{{missing.rawOutput}}");
	}

	[Fact]
	public void Resolve_MultipleUnresolvedExpressions_TracksAll()
	{
		// Arrange
		var context = new OrchestrationExecutionContext
		{
			OrchestrationInfo = s_defaultInfo,
			Parameters = new Dictionary<string, string>()
		};
		var template = "A: {{step1.output}} B: {{step2.output}}";

		// Act
		var result = TemplateResolver.Resolve(template, new Dictionary<string, string>(), context, [], s_defaultStep);

		// Assert
		result.Should().Be("A: {{step1.output}} B: {{step2.output}}");
		context.ResolutionTracker.UnresolvedExpressions.Should().HaveCount(2);
	}

	[Fact]
	public void Resolve_ResolvedStepOutput_DoesNotTrackAsUnresolved()
	{
		// Arrange
		var context = new OrchestrationExecutionContext
		{
			OrchestrationInfo = s_defaultInfo,
			Parameters = new Dictionary<string, string>()
		};
		context.AddResult("step1", ExecutionResult.Succeeded("resolved content"));
		var template = "Value is {{step1.output}}";

		// Act
		var result = TemplateResolver.Resolve(template, new Dictionary<string, string>(), context, ["step1"], s_defaultStep);

		// Assert
		result.Should().Be("Value is resolved content");
		context.ResolutionTracker.UnresolvedExpressions.Should().BeEmpty();
	}

	#endregion

	#region Edge Cases

	[Theory]
	[InlineData("{{orchestration.NAME}}", "my-pipeline")]
	[InlineData("{{orchestration.Version}}", "2.0.0")]
	[InlineData("{{orchestration.RUNID}}", "run-abc")]
	[InlineData("{{ORCHESTRATION.name}}", "my-pipeline")]
	[InlineData("{{step.NAME}}", "current-step")]
	[InlineData("{{STEP.type}}", "Transform")]
	[InlineData("{{VARS.region}}", "us-east-1")]
	[InlineData("{{PARAM.env}}", "prod")]
	[InlineData("{{ENV.ORCHESTRA_TEST_CASE}}", "case-test-value")]
	public void Resolve_CaseInsensitiveNamespaceAndProperty_ResolvesCorrectly(string template, string expected)
	{
		// Arrange
		Environment.SetEnvironmentVariable("ORCHESTRA_TEST_CASE", "case-test-value");
		try
		{
			var info = new OrchestrationInfo("my-pipeline", "2.0.0", "run-abc", DateTimeOffset.UtcNow);
			var parameters = new Dictionary<string, string> { ["env"] = "prod" };
			var context = new OrchestrationExecutionContext
			{
				OrchestrationInfo = info,
				Parameters = parameters,
				Variables = new Dictionary<string, string> { ["region"] = "us-east-1" }
			};

			// Act
			var result = TemplateResolver.Resolve(template, parameters, context, [], s_defaultStep);

			// Assert
			result.Should().Be(expected);
		}
		finally
		{
			Environment.SetEnvironmentVariable("ORCHESTRA_TEST_CASE", null);
		}
	}

	[Theory]
	[InlineData("{{ orchestration.name }}", "my-pipeline")]
	[InlineData("{{  param.env  }}", "prod")]
	[InlineData("{{ step.name }}", "current-step")]
	[InlineData("{{ vars.region }}", "us-east-1")]
	[InlineData("{{   orchestration.version   }}", "2.0.0")]
	[InlineData("{{ env.ORCHESTRA_TEST_WS }}", "ws-test-value")]
	public void Resolve_WhitespaceInExpression_ResolvesCorrectly(string template, string expected)
	{
		// Arrange
		Environment.SetEnvironmentVariable("ORCHESTRA_TEST_WS", "ws-test-value");
		try
		{
			var info = new OrchestrationInfo("my-pipeline", "2.0.0", "run-abc", DateTimeOffset.UtcNow);
			var parameters = new Dictionary<string, string> { ["env"] = "prod" };
			var context = new OrchestrationExecutionContext
			{
				OrchestrationInfo = info,
				Parameters = parameters,
				Variables = new Dictionary<string, string> { ["region"] = "us-east-1" }
			};

			// Act
			var result = TemplateResolver.Resolve(template, parameters, context, [], s_defaultStep);

			// Assert
			result.Should().Be(expected);
		}
		finally
		{
			Environment.SetEnvironmentVariable("ORCHESTRA_TEST_WS", null);
		}
	}

	[Fact]
	public void Resolve_VarsContainingStepOutput_LeavesStepOutputUnresolved()
	{
		// Arrange — variable values use static-only resolution, so step output
		// references inside a variable are left as-is (not resolved).
		var context = new OrchestrationExecutionContext
		{
			OrchestrationInfo = s_defaultInfo,
			Parameters = new Dictionary<string, string>(),
			Variables = new Dictionary<string, string>
			{
				["summary"] = "Result: {{analysis.output}}"
			}
		};
		context.AddResult("analysis", ExecutionResult.Succeeded("deep insights"));
		var template = "{{vars.summary}}";

		// Act
		var result = TemplateResolver.Resolve(template, [], context, ["analysis"], s_defaultStep);

		// Assert — step output expression is left as a literal because
		// variable resolution uses static-only expansion
		result.Should().Be("Result: {{analysis.output}}");
	}

	[Fact]
	public void Resolve_VarsThreeLevelCircularChain_LeavesAsIs()
	{
		// Arrange — A → B → C → A
		var context = new OrchestrationExecutionContext
		{
			OrchestrationInfo = s_defaultInfo,
			Parameters = new Dictionary<string, string>(),
			Variables = new Dictionary<string, string>
			{
				["a"] = "{{vars.b}}",
				["b"] = "{{vars.c}}",
				["c"] = "{{vars.a}}"
			}
		};
		var template = "Value: {{vars.a}}";

		// Act — should not throw or infinite-loop
		var result = TemplateResolver.Resolve(template, [], context, [], s_defaultStep);

		// Assert — the innermost circular reference is left as-is
		result.Should().Be("Value: {{vars.a}}");
	}

	[Fact]
	public void Resolve_VarsEmptyValue_ResolvesToEmptyString()
	{
		// Arrange
		var context = new OrchestrationExecutionContext
		{
			OrchestrationInfo = s_defaultInfo,
			Parameters = new Dictionary<string, string>(),
			Variables = new Dictionary<string, string>
			{
				["empty"] = ""
			}
		};
		var template = "Before[{{vars.empty}}]After";

		// Act
		var result = TemplateResolver.Resolve(template, [], context, [], s_defaultStep);

		// Assert
		result.Should().Be("Before[]After");
	}

	#endregion

	#region ResolveStatic Tests

	[Fact]
	public void ResolveStatic_ParameterExpression_Resolves()
	{
		// Arrange
		var context = new OrchestrationExecutionContext
		{
			OrchestrationInfo = s_defaultInfo,
			Parameters = new Dictionary<string, string> { ["project"] = "Orchestra" }
		};
		var template = "Project: {{param.project}}";

		// Act
		var result = TemplateResolver.ResolveStatic(template, context.Parameters, context);

		// Assert
		result.Should().Be("Project: Orchestra");
	}

	[Fact]
	public void ResolveStatic_EnvironmentVariable_Resolves()
	{
		// Arrange
		Environment.SetEnvironmentVariable("ORCHESTRA_TEST_STATIC", "resolved-env-value");
		try
		{
			var context = new OrchestrationExecutionContext
			{
				OrchestrationInfo = s_defaultInfo,
				Parameters = new Dictionary<string, string>()
			};
			var template = "Env: {{env.ORCHESTRA_TEST_STATIC}}";

			// Act
			var result = TemplateResolver.ResolveStatic(template, context.Parameters, context);

			// Assert
			result.Should().Be("Env: resolved-env-value");
		}
		finally
		{
			Environment.SetEnvironmentVariable("ORCHESTRA_TEST_STATIC", null);
		}
	}

	[Fact]
	public void ResolveStatic_OrchestrationMetadata_Resolves()
	{
		// Arrange
		var context = new OrchestrationExecutionContext
		{
			OrchestrationInfo = s_defaultInfo,
			Parameters = new Dictionary<string, string>()
		};
		var template = "Run: {{orchestration.name}} v{{orchestration.version}}";

		// Act
		var result = TemplateResolver.ResolveStatic(template, context.Parameters, context);

		// Assert
		result.Should().Be("Run: test-orchestration v1.0.0");
	}

	[Fact]
	public void ResolveStatic_OrchestrationSourceDirectory_ResolvesInsideVariable()
	{
		// Arrange
		var sourceDirectory = Path.GetFullPath(Path.Combine("workspace", "orchestrations", "System"));
		var info = s_defaultInfo with { SourceDirectory = sourceDirectory };
		var context = new OrchestrationExecutionContext
		{
			OrchestrationInfo = info,
			Parameters = new Dictionary<string, string>(),
			Variables = new Dictionary<string, string>
			{
				["ephermalDir"] = "{{orchestration.sourceDirectory}}/../Ephermal",
				["ephermalFile"] = "{{vars.ephermalDir}}/{{orchestration.runId}}.yaml"
			}
		};
		var template = "{{vars.ephermalFile}}";

		// Act
		var result = TemplateResolver.ResolveStatic(template, context.Parameters, context);

		// Assert
		result.Should().Be($"{sourceDirectory}/../Ephermal/{info.RunId}.yaml");
	}

	[Fact]
	public void ResolveStatic_VarsExpression_Resolves()
	{
		// Arrange
		var context = new OrchestrationExecutionContext
		{
			OrchestrationInfo = s_defaultInfo,
			Parameters = new Dictionary<string, string> { ["region"] = "us-east-1" },
			Variables = new Dictionary<string, string>
			{
				["endpoint"] = "https://{{param.region}}.api.example.com"
			}
		};
		var template = "Connecting to {{vars.endpoint}}";

		// Act
		var result = TemplateResolver.ResolveStatic(template, context.Parameters, context);

		// Assert
		result.Should().Be("Connecting to https://us-east-1.api.example.com");
	}

	[Fact]
	public void ResolveStatic_StepOutput_LeavesAsIs()
	{
		// Arrange
		var context = new OrchestrationExecutionContext
		{
			OrchestrationInfo = s_defaultInfo,
			Parameters = new Dictionary<string, string>()
		};
		context.AddResult("analysis", ExecutionResult.Succeeded("deep insights"));
		var template = "Output: {{analysis.output}}";

		// Act
		var result = TemplateResolver.ResolveStatic(template, context.Parameters, context);

		// Assert — step output reference left as literal
		result.Should().Be("Output: {{analysis.output}}");
	}

	[Fact]
	public void ResolveStatic_StepRawOutput_LeavesAsIs()
	{
		// Arrange
		var context = new OrchestrationExecutionContext
		{
			OrchestrationInfo = s_defaultInfo,
			Parameters = new Dictionary<string, string>()
		};
		context.AddResult("analysis", ExecutionResult.Succeeded("content", "raw content"));
		var template = "Raw: {{analysis.rawOutput}}";

		// Act
		var result = TemplateResolver.ResolveStatic(template, context.Parameters, context);

		// Assert
		result.Should().Be("Raw: {{analysis.rawOutput}}");
	}

	[Fact]
	public void ResolveStatic_StepProperty_LeavesAsIs()
	{
		// Arrange
		var context = new OrchestrationExecutionContext
		{
			OrchestrationInfo = s_defaultInfo,
			Parameters = new Dictionary<string, string>()
		};
		var template = "Step: {{step.name}}";

		// Act
		var result = TemplateResolver.ResolveStatic(template, context.Parameters, context);

		// Assert — step metadata left as literal
		result.Should().Be("Step: {{step.name}}");
	}

	[Fact]
	public void ResolveStatic_MixedExpressions_ResolvesOnlyStatic()
	{
		// Arrange
		var context = new OrchestrationExecutionContext
		{
			OrchestrationInfo = s_defaultInfo,
			Parameters = new Dictionary<string, string> { ["model"] = "claude-opus-4.5" },
			Variables = new Dictionary<string, string>
			{
				["greeting"] = "Hello from {{param.model}}"
			}
		};
		context.AddResult("step1", ExecutionResult.Succeeded("result1"));
		var template = "{{vars.greeting}} | step={{step.name}} | output={{step1.output}}";

		// Act
		var result = TemplateResolver.ResolveStatic(template, context.Parameters, context);

		// Assert — param/vars resolved, step.name and step output left as-is
		result.Should().Be("Hello from claude-opus-4.5 | step={{step.name}} | output={{step1.output}}");
	}

	#endregion

	#region ResolveStaticMcp Tests

	[Fact]
	public void ResolveStaticMcp_LocalMcp_ResolvesCommandAndArguments()
	{
		// Arrange
		var context = new OrchestrationExecutionContext
		{
			OrchestrationInfo = s_defaultInfo,
			Parameters = new Dictionary<string, string>
			{
				["tool_path"] = "/usr/local/bin/mytool",
				["project_dir"] = "/home/user/project"
			}
		};
		var mcp = new LocalMcp
		{
			Name = "my-tool",
			Type = McpType.Local,
			Command = "{{param.tool_path}}",
			Arguments = ["--dir", "{{param.project_dir}}", "--verbose"],
			WorkingDirectory = "{{param.project_dir}}/workspace"
		};

		// Act
		var resolved = TemplateResolver.ResolveStaticMcp(mcp, context.Parameters, context);

		// Assert
		var local = resolved.Should().BeOfType<LocalMcp>().Subject;
		local.Name.Should().Be("my-tool");             // Name preserved
		local.Type.Should().Be(McpType.Local);         // Type preserved
		local.Command.Should().Be("/usr/local/bin/mytool");
		local.Arguments.Should().Equal("--dir", "/home/user/project", "--verbose");
		local.WorkingDirectory.Should().Be("/home/user/project/workspace");
	}

	[Fact]
	public void ResolveStaticMcp_LocalMcp_NullWorkingDirectory_PreservesNull()
	{
		// Arrange
		var context = new OrchestrationExecutionContext
		{
			OrchestrationInfo = s_defaultInfo,
			Parameters = new Dictionary<string, string>()
		};
		var mcp = new LocalMcp
		{
			Name = "simple-tool",
			Type = McpType.Local,
			Command = "echo",
			Arguments = ["hello"],
			WorkingDirectory = null
		};

		// Act
		var resolved = TemplateResolver.ResolveStaticMcp(mcp, context.Parameters, context);

		// Assert
		var local = resolved.Should().BeOfType<LocalMcp>().Subject;
		local.WorkingDirectory.Should().BeNull();
	}

	[Fact]
	public void ResolveStaticMcp_RemoteMcp_ResolvesEndpointAndHeaders()
	{
		// Arrange
		var context = new OrchestrationExecutionContext
		{
			OrchestrationInfo = s_defaultInfo,
			Parameters = new Dictionary<string, string>
			{
				["api_host"] = "api.example.com",
				["api_key"] = "sk-test-12345"
			}
		};
		var mcp = new RemoteMcp
		{
			Name = "remote-api",
			Type = McpType.Remote,
			Endpoint = "https://{{param.api_host}}/v1/mcp",
			Headers = new Dictionary<string, string>
			{
				["Authorization"] = "Bearer {{param.api_key}}",
				["Content-Type"] = "application/json"
			}
		};

		// Act
		var resolved = TemplateResolver.ResolveStaticMcp(mcp, context.Parameters, context);

		// Assert
		var remote = resolved.Should().BeOfType<RemoteMcp>().Subject;
		remote.Name.Should().Be("remote-api");     // Name preserved
		remote.Type.Should().Be(McpType.Remote);   // Type preserved
		remote.Endpoint.Should().Be("https://api.example.com/v1/mcp");
		remote.Headers["Authorization"].Should().Be("Bearer sk-test-12345");
		remote.Headers["Content-Type"].Should().Be("application/json");
	}

	[Fact]
	public void ResolveStaticMcp_UnresolvableExpressions_LeftAsLiterals()
	{
		// Arrange
		var context = new OrchestrationExecutionContext
		{
			OrchestrationInfo = s_defaultInfo,
			Parameters = new Dictionary<string, string>() // No parameters defined
		};
		var mcp = new LocalMcp
		{
			Name = "tool-with-missing-params",
			Type = McpType.Local,
			Command = "{{param.undefined_tool}}",
			Arguments = ["--key", "{{param.undefined_key}}"],
		};

		// Act
		var resolved = TemplateResolver.ResolveStaticMcp(mcp, context.Parameters, context);

		// Assert — unresolvable expressions are left as literals
		var local = resolved.Should().BeOfType<LocalMcp>().Subject;
		local.Command.Should().Be("{{param.undefined_tool}}");
		local.Arguments.Should().Equal("--key", "{{param.undefined_key}}");
	}

	[Fact]
	public void ResolveStaticMcp_WithEnvVar_ResolvesEnvironmentVariable()
	{
		// Arrange
		Environment.SetEnvironmentVariable("ORCHESTRA_MCP_TEST_TOKEN", "secret-token-123");
		try
		{
			var context = new OrchestrationExecutionContext
			{
				OrchestrationInfo = s_defaultInfo,
				Parameters = new Dictionary<string, string>()
			};
		var mcp = new RemoteMcp
		{
			Name = "env-api",
			Type = McpType.Remote,
			Endpoint = "https://api.example.com",
				Headers = new Dictionary<string, string>
				{
					["Authorization"] = "Bearer {{env.ORCHESTRA_MCP_TEST_TOKEN}}"
				}
			};

			// Act
			var resolved = TemplateResolver.ResolveStaticMcp(mcp, context.Parameters, context);

			// Assert
			var remote = resolved.Should().BeOfType<RemoteMcp>().Subject;
			remote.Headers["Authorization"].Should().Be("Bearer secret-token-123");
		}
		finally
		{
			Environment.SetEnvironmentVariable("ORCHESTRA_MCP_TEST_TOKEN", null);
		}
	}

	[Fact]
	public void ResolveStaticMcp_WithVarsAndOrchestration_ResolvesNested()
	{
		// Arrange
		var context = new OrchestrationExecutionContext
		{
			OrchestrationInfo = s_defaultInfo,
			Parameters = new Dictionary<string, string> { ["region"] = "us-east-1" },
			Variables = new Dictionary<string, string>
			{
				["base_url"] = "https://{{param.region}}.mcp.example.com"
			}
		};
		var mcp = new RemoteMcp
		{
			Name = "regional-mcp",
			Type = McpType.Remote,
			Endpoint = "{{vars.base_url}}/{{orchestration.name}}",
			Headers = new Dictionary<string, string>
			{
				["X-Run-Id"] = "{{orchestration.runId}}"
			}
		};

		// Act
		var resolved = TemplateResolver.ResolveStaticMcp(mcp, context.Parameters, context);

		// Assert
		var remote = resolved.Should().BeOfType<RemoteMcp>().Subject;
		remote.Endpoint.Should().Be("https://us-east-1.mcp.example.com/test-orchestration");
		remote.Headers["X-Run-Id"].Should().Be("run123");
	}

	[Fact]
	public void ResolveStaticMcp_StepOutputInMcpField_LeftAsLiteral()
	{
		// Arrange — step output expressions in MCP fields should not resolve
		var context = new OrchestrationExecutionContext
		{
			OrchestrationInfo = s_defaultInfo,
			Parameters = new Dictionary<string, string>()
		};
		context.AddResult("setup", ExecutionResult.Succeeded("http://localhost:3000"));
		var mcp = new RemoteMcp
		{
			Name = "dynamic-api",
			Type = McpType.Remote,
			Endpoint = "{{setup.output}}/mcp",
			Headers = new Dictionary<string, string>()
		};

		// Act
		var resolved = TemplateResolver.ResolveStaticMcp(mcp, context.Parameters, context);

		// Assert — step output left as-is since MCP uses static resolution
		var remote = resolved.Should().BeOfType<RemoteMcp>().Subject;
		remote.Endpoint.Should().Be("{{setup.output}}/mcp");
	}

	/// <summary>
	/// <see cref="Mcp.TimeoutTemplate"/> is resolved at step-execution time using the
	/// step-aware overload of <c>ResolveStaticMcp</c>. References to step outputs from
	/// the step's <c>DependsOn</c> set are honored, and the resolved integer count of
	/// seconds materialises in <see cref="Mcp.Timeout"/> on the returned clone.
	/// This is the primary mechanism for letting a preceding Script step emit a derived
	/// MCP transport budget after input validation.
	/// </summary>
	[Fact]
	public void ResolveStaticMcp_TimeoutTemplate_StepOutput_ResolvedToTimeoutWithStepContext()
	{
		// Arrange
		var step = new PromptOrchestrationStep
		{
			Name = "controller",
			Type = OrchestrationStepType.Prompt,
			DependsOn = ["validate-inputs"],
			Model = "test-model",
			SystemPrompt = "",
			UserPrompt = "",
			Mcps = [],
		};
		var context = new OrchestrationExecutionContext
		{
			OrchestrationInfo = s_defaultInfo,
			Parameters = new Dictionary<string, string>(),
		};
		context.AddResult("validate-inputs", ExecutionResult.Succeeded("21660"));

		var mcp = new RemoteMcp
		{
			Name = "orchestra",
			Type = McpType.Remote,
			Endpoint = "http://localhost:5001/mcp/data",
			Headers = new Dictionary<string, string>(),
			TimeoutTemplate = "{{validate-inputs.output}}",
		};

		// Act
		var resolved = TemplateResolver.ResolveStaticMcp(mcp, context.Parameters, context, step.DependsOn, step);

		// Assert
		var remote = resolved.Should().BeOfType<RemoteMcp>().Subject;
		remote.Timeout.Should().Be(TimeSpan.FromSeconds(21660),
			"the template must resolve through the step's DependsOn outputs");
		remote.TimeoutTemplate.Should().BeNull(
			"once resolved the template is consumed so re-running the resolver " +
			"on the result is a no-op");
	}

	/// <summary>
	/// A <see cref="Mcp.TimeoutTemplate"/> using JSON-path access into a step's JSON
	/// output (<c>{{stepName.output.foo}}</c>) extracts the named scalar field. This is
	/// the shape used by <c>run-self-healing.yaml</c>: the <c>validate-inputs</c> Script
	/// emits a single JSON object with all validated runtime values, and the orchestra
	/// MCP entry plucks just <c>controllerMcpTimeoutSeconds</c> from it.
	/// </summary>
	[Fact]
	public void ResolveStaticMcp_TimeoutTemplate_JsonPathOnStepOutput_ResolvedFromScalarField()
	{
		// Arrange
		var step = new PromptOrchestrationStep
		{
			Name = "controller",
			Type = OrchestrationStepType.Prompt,
			DependsOn = ["validate-inputs"],
			Model = "test-model",
			SystemPrompt = "",
			UserPrompt = "",
			Mcps = [],
		};
		var context = new OrchestrationExecutionContext
		{
			OrchestrationInfo = s_defaultInfo,
			Parameters = new Dictionary<string, string>(),
		};
		context.AddResult("validate-inputs", ExecutionResult.Succeeded(
			"{\"childWaitTimeoutSeconds\":21660,\"controllerMcpTimeoutSeconds\":21960}"));

		var mcp = new RemoteMcp
		{
			Name = "orchestra",
			Type = McpType.Remote,
			Endpoint = "http://localhost:5001/mcp/data",
			Headers = new Dictionary<string, string>(),
			TimeoutTemplate = "{{validate-inputs.output.controllerMcpTimeoutSeconds}}",
		};

		// Act
		var resolved = TemplateResolver.ResolveStaticMcp(mcp, context.Parameters, context, step.DependsOn, step);

		// Assert
		resolved.Should().BeOfType<RemoteMcp>().Which.Timeout.Should().Be(TimeSpan.FromSeconds(21960));
	}

	/// <summary>
	/// A <see cref="Mcp.TimeoutTemplate"/> referencing a parameter resolves identically
	/// in both the static-only overload and the step-aware overload — params/vars/env/
	/// orchestration references do not require a step context.
	/// </summary>
	[Fact]
	public void ResolveStaticMcp_TimeoutTemplate_ParamReference_ResolvedInStaticOverload()
	{
		// Arrange
		var context = new OrchestrationExecutionContext
		{
			OrchestrationInfo = s_defaultInfo,
			Parameters = new Dictionary<string, string> { ["childTimeoutSeconds"] = "7200" },
		};
		var mcp = new RemoteMcp
		{
			Name = "orchestra",
			Type = McpType.Remote,
			Endpoint = "http://localhost:5001/mcp/data",
			Headers = new Dictionary<string, string>(),
			TimeoutTemplate = "{{param.childTimeoutSeconds}}",
		};

		// Act — use the static-only overload deliberately
		var resolved = TemplateResolver.ResolveStaticMcp(mcp, context.Parameters, context);

		// Assert
		resolved.Should().BeOfType<RemoteMcp>().Which.Timeout.Should().Be(TimeSpan.FromSeconds(7200));
		resolved.TimeoutTemplate.Should().BeNull();
	}

	/// <summary>
	/// A <see cref="Mcp.TimeoutTemplate"/> that resolves to something non-numeric must
	/// surface a clear, named runtime error rather than silently producing a zero/null
	/// timeout that would then let the SDK's default kick in.
	/// </summary>
	[Fact]
	public void ResolveStaticMcp_TimeoutTemplate_NonNumeric_Throws()
	{
		// Arrange
		var context = new OrchestrationExecutionContext
		{
			OrchestrationInfo = s_defaultInfo,
			Parameters = new Dictionary<string, string> { ["bogus"] = "not-a-number" },
		};
		var mcp = new RemoteMcp
		{
			Name = "orchestra",
			Type = McpType.Remote,
			Endpoint = "http://localhost:5001/mcp/data",
			Headers = new Dictionary<string, string>(),
			TimeoutTemplate = "{{param.bogus}}",
		};

		// Act
		var act = () => TemplateResolver.ResolveStaticMcp(mcp, context.Parameters, context);

		// Assert
		act.Should().Throw<InvalidOperationException>()
			.WithMessage("*orchestra*timeoutSeconds*not a valid integer*");
	}

	/// <summary>
	/// A <see cref="Mcp.TimeoutTemplate"/> that resolves to a non-positive integer must
	/// throw, mirroring the historical numeric-form contract that non-positive values
	/// are treated as configuration errors.
	/// </summary>
	[Fact]
	public void ResolveStaticMcp_TimeoutTemplate_NonPositive_Throws()
	{
		// Arrange
		var context = new OrchestrationExecutionContext
		{
			OrchestrationInfo = s_defaultInfo,
			Parameters = new Dictionary<string, string> { ["zero"] = "0" },
		};
		var mcp = new RemoteMcp
		{
			Name = "orchestra",
			Type = McpType.Remote,
			Endpoint = "http://localhost:5001/mcp/data",
			Headers = new Dictionary<string, string>(),
			TimeoutTemplate = "{{param.zero}}",
		};

		// Act
		var act = () => TemplateResolver.ResolveStaticMcp(mcp, context.Parameters, context);

		// Assert
		act.Should().Throw<InvalidOperationException>()
			.WithMessage("*orchestra*positive integer*");
	}

	#endregion

	#region ResolveVariable Tightening Tests

	[Fact]
	public void Resolve_VarsReferencingOtherVars_StillResolves()
	{
		// Arrange — vars referencing vars should still work
		var context = new OrchestrationExecutionContext
		{
			OrchestrationInfo = s_defaultInfo,
			Parameters = new Dictionary<string, string> { ["base"] = "https://api.example.com" },
			Variables = new Dictionary<string, string>
			{
				["api_url"] = "{{param.base}}/v2",
				["full_endpoint"] = "{{vars.api_url}}/data"
			}
		};
		var template = "Endpoint: {{vars.full_endpoint}}";

		// Act
		var result = TemplateResolver.Resolve(template, context.Parameters, context, [], s_defaultStep);

		// Assert — nested var → param resolution works
		result.Should().Be("Endpoint: https://api.example.com/v2/data");
	}

	[Fact]
	public void Resolve_VarsReferencingEnv_StillResolves()
	{
		// Arrange
		Environment.SetEnvironmentVariable("ORCHESTRA_VAR_TEST_KEY", "env-secret");
		try
		{
			var context = new OrchestrationExecutionContext
			{
				OrchestrationInfo = s_defaultInfo,
				Parameters = new Dictionary<string, string>(),
				Variables = new Dictionary<string, string>
				{
					["auth_header"] = "Bearer {{env.ORCHESTRA_VAR_TEST_KEY}}"
				}
			};
			var template = "Header: {{vars.auth_header}}";

			// Act
			var result = TemplateResolver.Resolve(template, context.Parameters, context, [], s_defaultStep);

			// Assert
			result.Should().Be("Header: Bearer env-secret");
		}
		finally
		{
			Environment.SetEnvironmentVariable("ORCHESTRA_VAR_TEST_KEY", null);
		}
	}

	[Fact]
	public void Resolve_VarsReferencingOrchestrationMetadata_StillResolves()
	{
		// Arrange
		var context = new OrchestrationExecutionContext
		{
			OrchestrationInfo = s_defaultInfo,
			Parameters = new Dictionary<string, string>(),
			Variables = new Dictionary<string, string>
			{
				["run_label"] = "{{orchestration.name}}-{{orchestration.runId}}"
			}
		};
		var template = "Label: {{vars.run_label}}";

		// Act
		var result = TemplateResolver.Resolve(template, context.Parameters, context, [], s_defaultStep);

		// Assert
		result.Should().Be("Label: test-orchestration-run123");
	}

	[Fact]
	public void Resolve_VarsReferencingStepMetadata_LeavesStepPartUnresolved()
	{
		// Arrange — step.name inside a variable should NOT resolve
		var context = new OrchestrationExecutionContext
		{
			OrchestrationInfo = s_defaultInfo,
			Parameters = new Dictionary<string, string>(),
			Variables = new Dictionary<string, string>
			{
				["context_info"] = "Step={{step.name}}"
			}
		};
		var template = "Info: {{vars.context_info}}";

		// Act
		var result = TemplateResolver.Resolve(template, context.Parameters, context, [], s_defaultStep);

		// Assert — step.name left as-is inside variable resolution
		result.Should().Be("Info: Step={{step.name}}");
	}

	#endregion

	#region OrchestrationStep ChildOrchestrationInfo accessors

	private static ExecutionResult MakeOrchestrationStepResult(
		string executionId = "child-1",
		string orchestrationName = "child-orch",
		ExecutionStatus status = ExecutionStatus.Succeeded,
		string finalContent = "child-final",
		string? errorMessage = null,
		string? completionReason = null,
		CancellationDetails? cancellation = null,
		Dictionary<string, ChildStepInfo>? steps = null)
	{
		var info = new ChildOrchestrationInfo
		{
			ExecutionId = executionId,
			OrchestrationName = orchestrationName,
			OrchestrationId = orchestrationName,
			Status = status,
			ErrorMessage = errorMessage,
			FinalContent = finalContent,
			CompletionReason = completionReason,
			Cancellation = cancellation,
			StepResults = steps ?? new Dictionary<string, ChildStepInfo>(StringComparer.OrdinalIgnoreCase),
			StartedAt = DateTimeOffset.UtcNow,
			CompletedAt = DateTimeOffset.UtcNow,
		};
		return status == ExecutionStatus.Succeeded
			? ExecutionResult.Succeeded(finalContent, childOrchestrationInfo: info)
			: ExecutionResult.Failed(errorMessage ?? "", childOrchestrationInfo: info);
	}

	[Fact]
	public void Resolve_ChildExecutionIdAccessor_ReturnsExecutionId()
	{
		var context = new OrchestrationExecutionContext
		{
			OrchestrationInfo = s_defaultInfo,
			Parameters = new Dictionary<string, string>(),
		};
		context.AddResult("invoke-child", MakeOrchestrationStepResult(executionId: "child-exec-42"));

		var result = TemplateResolver.Resolve(
			"Spawned child {{invoke-child.executionId}} successfully",
			context.Parameters, context, ["invoke-child"], s_defaultStep);

		result.Should().Be("Spawned child child-exec-42 successfully");
	}

	[Fact]
	public void Resolve_ChildStatusAccessor_ReturnsLowercaseStatus()
	{
		var context = new OrchestrationExecutionContext
		{
			OrchestrationInfo = s_defaultInfo,
			Parameters = new Dictionary<string, string>(),
		};
		context.AddResult("invoke-child", MakeOrchestrationStepResult(status: ExecutionStatus.Succeeded));

		var result = TemplateResolver.Resolve(
			"Status: {{invoke-child.status}}",
			context.Parameters, context, ["invoke-child"], s_defaultStep);

		result.Should().Be("Status: succeeded");
	}

	[Fact]
	public void Resolve_ChildStepAccessor_DrillsIntoChildStepOutput_NoTruncation()
	{
		// Self-healing controllers need full untruncated access to child step outputs so
		// they can incorporate previous attempts' content into repair prompts. A 100 KB
		// payload should round-trip verbatim.
		var bigContent = new string('A', 100_000);
		var childSteps = new Dictionary<string, ChildStepInfo>(StringComparer.OrdinalIgnoreCase)
		{
			["big-step"] = new ChildStepInfo { Status = ExecutionStatus.Succeeded, Content = bigContent },
		};
		var context = new OrchestrationExecutionContext
		{
			OrchestrationInfo = s_defaultInfo,
			Parameters = new Dictionary<string, string>(),
		};
		context.AddResult("attempt-1", MakeOrchestrationStepResult(steps: childSteps));

		var result = TemplateResolver.Resolve(
			"{{attempt-1.steps.big-step.output}}",
			context.Parameters, context, ["attempt-1"], s_defaultStep);

		result.Length.Should().Be(100_000, "child step content must not be truncated by the in-process binding path");
		result.Should().Be(bigContent);
	}

	[Fact]
	public void Resolve_ChildStepAccessor_SurfacesErrorOfFailingStep_EvenWhenSiblingSucceeded()
	{
		// Even when the overall child run failed, the parent must be able to drill into
		// EACH child step independently. This is what makes the self-healing pattern work:
		// the controller can see step-by-step what worked and what didn't.
		var childSteps = new Dictionary<string, ChildStepInfo>(StringComparer.OrdinalIgnoreCase)
		{
			["good"] = new ChildStepInfo { Status = ExecutionStatus.Succeeded, Content = "yay" },
			["bad"] = new ChildStepInfo { Status = ExecutionStatus.Failed, ErrorMessage = "compiler error: missing semicolon" },
		};
		var context = new OrchestrationExecutionContext
		{
			OrchestrationInfo = s_defaultInfo,
			Parameters = new Dictionary<string, string>(),
		};
		context.AddResult("attempt-1",
			MakeOrchestrationStepResult(status: ExecutionStatus.Failed, errorMessage: "child overall failed", steps: childSteps));

		var goodOutput = TemplateResolver.Resolve("{{attempt-1.steps.good.output}}",
			context.Parameters, context, ["attempt-1"], s_defaultStep);
		var badError = TemplateResolver.Resolve("{{attempt-1.steps.bad.error}}",
			context.Parameters, context, ["attempt-1"], s_defaultStep);
		var badStatus = TemplateResolver.Resolve("{{attempt-1.steps.bad.status}}",
			context.Parameters, context, ["attempt-1"], s_defaultStep);

		goodOutput.Should().Be("yay");
		badError.Should().Be("compiler error: missing semicolon");
		badStatus.Should().Be("failed");
	}

	[Fact]
	public void Resolve_StepsAccessor_ReturnsValidJsonOfAllChildSteps()
	{
		var childSteps = new Dictionary<string, ChildStepInfo>(StringComparer.OrdinalIgnoreCase)
		{
			["one"] = new ChildStepInfo { Status = ExecutionStatus.Succeeded, Content = "alpha" },
			["two"] = new ChildStepInfo { Status = ExecutionStatus.Failed, ErrorMessage = "kaboom" },
		};
		var context = new OrchestrationExecutionContext
		{
			OrchestrationInfo = s_defaultInfo,
			Parameters = new Dictionary<string, string>(),
		};
		context.AddResult("invoke-child", MakeOrchestrationStepResult(steps: childSteps));

		var json = TemplateResolver.Resolve("{{invoke-child.steps}}",
			context.Parameters, context, ["invoke-child"], s_defaultStep);

		using var doc = System.Text.Json.JsonDocument.Parse(json);
		doc.RootElement.GetProperty("one").GetProperty("status").GetString().Should().Be("succeeded");
		doc.RootElement.GetProperty("one").GetProperty("output").GetString().Should().Be("alpha");
		doc.RootElement.GetProperty("two").GetProperty("error").GetString().Should().Be("kaboom");
	}

	[Fact]
	public void Resolve_ChildResultAccessor_ReturnsFullJsonBlob()
	{
		var childSteps = new Dictionary<string, ChildStepInfo>(StringComparer.OrdinalIgnoreCase)
		{
			["s1"] = new ChildStepInfo { Status = ExecutionStatus.Succeeded, Content = "out1" },
		};
		var context = new OrchestrationExecutionContext
		{
			OrchestrationInfo = s_defaultInfo,
			Parameters = new Dictionary<string, string>(),
		};
		context.AddResult("invoke-child",
			MakeOrchestrationStepResult(executionId: "exec-77", finalContent: "final", steps: childSteps));

		var json = TemplateResolver.Resolve("{{invoke-child.childResult}}",
			context.Parameters, context, ["invoke-child"], s_defaultStep);

		using var doc = System.Text.Json.JsonDocument.Parse(json);
		doc.RootElement.GetProperty("executionId").GetString().Should().Be("exec-77");
		doc.RootElement.GetProperty("status").GetString().Should().Be("succeeded");
		doc.RootElement.GetProperty("finalContent").GetString().Should().Be("final");
		doc.RootElement.GetProperty("stepResults").GetProperty("s1").GetProperty("output").GetString().Should().Be("out1");
	}

	[Fact]
	public void Resolve_ChildExecutionIdAccessor_OnNonOrchestrationStep_LeavesUnresolved()
	{
		// Prompt/Transform/etc. step doesn't have a ChildOrchestrationInfo; the binding
		// must leave the expression literal so the user sees a clear diagnostic via the
		// resolution tracker rather than getting an empty string silently.
		var context = new OrchestrationExecutionContext
		{
			OrchestrationInfo = s_defaultInfo,
			Parameters = new Dictionary<string, string>(),
		};
		context.AddResult("not-orch", ExecutionResult.Succeeded("plain content"));

		var result = TemplateResolver.Resolve("{{not-orch.executionId}}",
			context.Parameters, context, ["not-orch"], s_defaultStep);

		result.Should().Be("{{not-orch.executionId}}");
	}

	[Fact]
	public void Resolve_ChildStepAccessor_UnknownChildStep_LeavesUnresolved()
	{
		var context = new OrchestrationExecutionContext
		{
			OrchestrationInfo = s_defaultInfo,
			Parameters = new Dictionary<string, string>(),
		};
		context.AddResult("invoke-child", MakeOrchestrationStepResult());

		var result = TemplateResolver.Resolve("{{invoke-child.steps.missing.output}}",
			context.Parameters, context, ["invoke-child"], s_defaultStep);

		result.Should().Be("{{invoke-child.steps.missing.output}}");
	}

	[Fact]
	public void Resolve_ChildStepFilesIndex_ReturnsIndexedFilePath()
	{
		var childSteps = new Dictionary<string, ChildStepInfo>(StringComparer.OrdinalIgnoreCase)
		{
			["s"] = new ChildStepInfo
			{
				Status = ExecutionStatus.Succeeded,
				Content = "c",
				SavedFiles = new[] { "/tmp/a.log", "/tmp/b.log" },
			},
		};
		var context = new OrchestrationExecutionContext
		{
			OrchestrationInfo = s_defaultInfo,
			Parameters = new Dictionary<string, string>(),
		};
		context.AddResult("invoke-child", MakeOrchestrationStepResult(steps: childSteps));

		var first = TemplateResolver.Resolve("{{invoke-child.steps.s.files[0]}}",
			context.Parameters, context, ["invoke-child"], s_defaultStep);
		var second = TemplateResolver.Resolve("{{invoke-child.steps.s.files[1]}}",
			context.Parameters, context, ["invoke-child"], s_defaultStep);

		first.Should().Be("/tmp/a.log");
		second.Should().Be("/tmp/b.log");
	}

	#endregion

	#region Escape Syntax

	[Fact]
	public void Resolve_EscapedStepOutput_EmitsLiteralCurliesAndStripsBackslash()
	{
		// Arrange — exactly the bug scenario: a step's own script body contains
		// {{stepName.output}} for documentation. Without the escape this would
		// be tracked as an unresolved expression. With \{{stepName.output}} the
		// engine consumes the backslash and emits the body verbatim.
		var context = new OrchestrationExecutionContext
		{
			OrchestrationInfo = s_defaultInfo,
			Parameters = new Dictionary<string, string>()
		};
		var template = @"downstream consumers use \{{current-step.output}} to read this";

		// Act
		var result = TemplateResolver.Resolve(template, [], context, [], s_defaultStep);

		// Assert
		result.Should().Be("downstream consumers use {{current-step.output}} to read this");
		// And critically: it must NOT be tracked as unresolved, because the user
		// signaled intent with the escape.
		context.ResolutionTracker.UnresolvedExpressions.Should().BeEmpty();
	}

	[Fact]
	public void Resolve_EscapedParameter_DoesNotSubstituteParameter()
	{
		// Arrange — a value happens to look like {{param.topic}} but is meant
		// to be emitted literally (e.g. inside a prompt that documents the
		// parameter contract to an LLM).
		var context = new OrchestrationExecutionContext
		{
			OrchestrationInfo = s_defaultInfo,
			Parameters = new Dictionary<string, string> { ["topic"] = "AI" }
		};
		var template = @"Use the literal placeholder \{{param.topic}} in your output";

		// Act
		var result = TemplateResolver.Resolve(template, context.Parameters, context, [], s_defaultStep);

		// Assert — the literal `{{param.topic}}` appears, NOT the resolved "AI".
		result.Should().Be("Use the literal placeholder {{param.topic}} in your output");
	}

	[Fact]
	public void Resolve_EscapedEnvVar_DoesNotReadEnvironment()
	{
		// Arrange — escaped {{env.*}} must NOT touch the environment at all.
		// We assert no tracking happened by checking AccessedEnvironmentVariables.
		Environment.SetEnvironmentVariable("ORCHESTRA_ESCAPE_TEST", "should-not-appear");
		try
		{
			var context = new OrchestrationExecutionContext
			{
				OrchestrationInfo = s_defaultInfo,
				Parameters = new Dictionary<string, string>()
			};
			var template = @"Reference: \{{env.ORCHESTRA_ESCAPE_TEST}}";

			// Act
			var result = TemplateResolver.Resolve(template, [], context, [], s_defaultStep);

			// Assert
			result.Should().Be("Reference: {{env.ORCHESTRA_ESCAPE_TEST}}");
			context.ResolutionTracker.AccessedEnvironmentVariables.Should().NotContainKey("ORCHESTRA_ESCAPE_TEST");
		}
		finally
		{
			Environment.SetEnvironmentVariable("ORCHESTRA_ESCAPE_TEST", null);
		}
	}

	[Fact]
	public void Resolve_EscapedVarsExpression_DoesNotExpandVariable()
	{
		// Arrange
		var context = new OrchestrationExecutionContext
		{
			OrchestrationInfo = s_defaultInfo,
			Parameters = new Dictionary<string, string>(),
			Variables = new Dictionary<string, string> { ["region"] = "us-east-1" }
		};
		var template = @"The vars.region placeholder is \{{vars.region}}";

		// Act
		var result = TemplateResolver.Resolve(template, [], context, [], s_defaultStep);

		// Assert
		result.Should().Be("The vars.region placeholder is {{vars.region}}");
	}

	[Fact]
	public void Resolve_EscapedOrchestrationProperty_DoesNotResolveMetadata()
	{
		// Arrange
		var info = new OrchestrationInfo("my-pipeline", "1.0.0", "run-1", DateTimeOffset.UtcNow);
		var context = new OrchestrationExecutionContext
		{
			OrchestrationInfo = info,
			Parameters = new Dictionary<string, string>()
		};
		var template = @"Use \{{orchestration.name}} to reference the pipeline name";

		// Act
		var result = TemplateResolver.Resolve(template, [], context, [], s_defaultStep);

		// Assert — the literal stays; the actual name "my-pipeline" does NOT appear.
		result.Should().Be("Use {{orchestration.name}} to reference the pipeline name");
	}

	[Fact]
	public void Resolve_MixedEscapedAndUnescaped_HandlesBothCorrectly()
	{
		// Arrange — escaping is per-expression; unescaped expressions in the
		// same string must still resolve normally.
		var context = new OrchestrationExecutionContext
		{
			OrchestrationInfo = s_defaultInfo,
			Parameters = new Dictionary<string, string> { ["topic"] = "AI" }
		};
		var template = @"Document the contract: \{{param.topic}} is replaced with the actual value (currently: {{param.topic}})";

		// Act
		var result = TemplateResolver.Resolve(template, context.Parameters, context, [], s_defaultStep);

		// Assert
		result.Should().Be("Document the contract: {{param.topic}} is replaced with the actual value (currently: AI)");
	}

	[Fact]
	public void Resolve_MultipleEscapedExpressions_AllPreserved()
	{
		// Arrange
		var context = new OrchestrationExecutionContext
		{
			OrchestrationInfo = s_defaultInfo,
			Parameters = new Dictionary<string, string>()
		};
		var template = @"Pipeline references: \{{step1.output}}, \{{step2.files[0]}}, \{{vars.foo}}.";

		// Act
		var result = TemplateResolver.Resolve(template, [], context, [], s_defaultStep);

		// Assert — every escape works independently, no tracking happens.
		result.Should().Be("Pipeline references: {{step1.output}}, {{step2.files[0]}}, {{vars.foo}}.");
		context.ResolutionTracker.UnresolvedExpressions.Should().BeEmpty();
	}

	[Fact]
	public void Resolve_EscapedExpression_DoesNotTrackAsUnresolved()
	{
		// Arrange — this is the exact regression scenario from the field:
		// `fetch-assigned-prs` step's script contained {{fetch-assigned-prs.output}}
		// in a PowerShell comment, producing a noisy "unresolved template" warning
		// on every run. The escape must suppress that tracking entirely.
		var context = new OrchestrationExecutionContext
		{
			OrchestrationInfo = s_defaultInfo,
			Parameters = new Dictionary<string, string>()
		};
		var selfReferentialStep = new TransformOrchestrationStep
		{
			Name = "fetch-assigned-prs",
			Type = OrchestrationStepType.Transform,
			DependsOn = [],
			Template = ""
		};
		var template = @"# downstream readers consume \{{fetch-assigned-prs.output}} as JSON";

		// Act
		var result = TemplateResolver.Resolve(template, [], context, [], selfReferentialStep);

		// Assert
		result.Should().Be("# downstream readers consume {{fetch-assigned-prs.output}} as JSON");
		context.ResolutionTracker.UnresolvedExpressions.Should().BeEmpty();
	}

	[Fact]
	public void Resolve_UnescapedSelfReference_StillTrackedAsUnresolvedForBackCompat()
	{
		// Arrange — without an explicit escape, the engine continues to track
		// unresolvable expressions exactly as before. This pins the regression
		// behavior we are deliberately preserving (only the opt-in escape skips
		// tracking).
		var context = new OrchestrationExecutionContext
		{
			OrchestrationInfo = s_defaultInfo,
			Parameters = new Dictionary<string, string>()
		};
		var selfReferentialStep = new TransformOrchestrationStep
		{
			Name = "fetch-assigned-prs",
			Type = OrchestrationStepType.Transform,
			DependsOn = [],
			Template = ""
		};
		var template = @"# downstream readers consume {{fetch-assigned-prs.output}} as JSON";

		// Act
		var result = TemplateResolver.Resolve(template, [], context, [], selfReferentialStep);

		// Assert — text unchanged, but the engine WARNS via the tracker.
		result.Should().Be("# downstream readers consume {{fetch-assigned-prs.output}} as JSON");
		context.ResolutionTracker.UnresolvedExpressions.Should().HaveCount(1);
		context.ResolutionTracker.UnresolvedExpressions.First().Expression
			.Should().Be("{{fetch-assigned-prs.output}}");
	}

	[Fact]
	public void ResolveStatic_EscapedExpression_EmitsLiteralCurliesAndStripsBackslash()
	{
		// Arrange — the static resolver (used for vars, MCP fields, etc.) must
		// honor the escape with identical semantics so authors can put literal
		// `{{...}}` text inside variable values or MCP timeout templates.
		var context = new OrchestrationExecutionContext
		{
			OrchestrationInfo = s_defaultInfo,
			Parameters = new Dictionary<string, string> { ["topic"] = "AI" }
		};
		var template = @"Use \{{param.topic}} as the placeholder";

		// Act
		var result = TemplateResolver.ResolveStatic(template, context.Parameters, context);

		// Assert
		result.Should().Be("Use {{param.topic}} as the placeholder");
	}

	[Fact]
	public void Resolve_EscapeInsideVarValue_PreservesLiteralWhenVarIsExpanded()
	{
		// Arrange — a variable whose value contains \{{...}} should resolve to
		// `{{...}}` literal when the variable is expanded. Because Regex.Replace
		// does not re-scan its replacement, the literal text remains in the
		// outer template's output untouched (no double processing).
		var context = new OrchestrationExecutionContext
		{
			OrchestrationInfo = s_defaultInfo,
			Parameters = new Dictionary<string, string>(),
			Variables = new Dictionary<string, string>
			{
				["docNote"] = @"Reference \{{step.output}} in your reply"
			}
		};
		var template = @"{{vars.docNote}}";

		// Act
		var result = TemplateResolver.Resolve(template, [], context, [], s_defaultStep);

		// Assert — the inner variable expansion produces `{{step.output}}`
		// literal, which then sits in the outer template as-is.
		result.Should().Be("Reference {{step.output}} in your reply");
	}

	[Fact]
	public void Resolve_EscapeAtStartOfString_Works()
	{
		// Arrange — an escape at position 0 must still match (no characters
		// precede the backslash, which is a common boundary bug).
		var context = new OrchestrationExecutionContext
		{
			OrchestrationInfo = s_defaultInfo,
			Parameters = new Dictionary<string, string>()
		};
		var template = @"\{{step1.output}} sits at the very start";

		// Act
		var result = TemplateResolver.Resolve(template, [], context, [], s_defaultStep);

		// Assert
		result.Should().Be("{{step1.output}} sits at the very start");
		context.ResolutionTracker.UnresolvedExpressions.Should().BeEmpty();
	}

	[Fact]
	public void Resolve_BackslashFollowedByNonTemplate_IsLeftUntouched()
	{
		// Arrange — the escape only consumes a backslash when it is IMMEDIATELY
		// followed by `{{expr}}`. A standalone backslash (or one in front of
		// non-template text) must be preserved verbatim.
		var context = new OrchestrationExecutionContext
		{
			OrchestrationInfo = s_defaultInfo,
			Parameters = new Dictionary<string, string>()
		};
		var template = @"A literal backslash: \ and a path: C:\Users\test should both survive";

		// Act
		var result = TemplateResolver.Resolve(template, [], context, [], s_defaultStep);

		// Assert — no template pattern was matched at all; the string is unchanged.
		result.Should().Be(@"A literal backslash: \ and a path: C:\Users\test should both survive");
	}

	#endregion
}
