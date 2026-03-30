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
	public void Resolve_OrchestrationAllProperties_ResolvesAll()
	{
		// Arrange
		var startedAt = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
		var info = new OrchestrationInfo("full-test", "5.0.0", "run-full", startedAt);
		var context = new OrchestrationExecutionContext
		{
			OrchestrationInfo = info,
			Parameters = new Dictionary<string, string>()
		};
		var template = "{{orchestration.name}} v{{orchestration.version}} [{{orchestration.runId}}] at {{orchestration.startedAt}}";

		// Act
		var result = TemplateResolver.Resolve(template, [], context, [], s_defaultStep);

		// Assert
		result.Should().Be($"full-test v5.0.0 [run-full] at {startedAt:o}");
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
	public void Resolve_VarsContainingStepOutput_ResolvesRecursively()
	{
		// Arrange
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

		// Assert
		result.Should().Be("Result: deep insights");
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
}
