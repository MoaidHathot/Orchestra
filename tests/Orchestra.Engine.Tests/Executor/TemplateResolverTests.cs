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
}
