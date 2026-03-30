using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Orchestra.Engine.Tests.Executor;

public class TransformStepExecutorTests
{
	private static readonly OrchestrationInfo s_defaultInfo = new("test-orchestration", "1.0.0", "run123", DateTimeOffset.UtcNow);
	private readonly ILogger<TransformStepExecutor> _logger = NullLoggerFactory.Instance.CreateLogger<TransformStepExecutor>();

	private TransformStepExecutor CreateExecutor() => new(_logger);

	private static TransformOrchestrationStep CreateTransformStep(
		string name = "transform-step",
		string template = "Hello, World!",
		string contentType = "text/plain",
		string[]? dependsOn = null,
		string[]? parameters = null) => new()
	{
		Name = name,
		Type = OrchestrationStepType.Transform,
		DependsOn = dependsOn ?? [],
		Parameters = parameters ?? [],
		Template = template,
		ContentType = contentType,
	};

	#region Simple Templates

	[Fact]
	public async Task ExecuteAsync_SimpleTemplate_ReturnsResolvedContent()
	{
		// Arrange
		var executor = CreateExecutor();
		var step = CreateTransformStep(template: "Plain text content");
		var context = new OrchestrationExecutionContext { OrchestrationInfo = s_defaultInfo, Parameters = new Dictionary<string, string>() };

		// Act
		var result = await executor.ExecuteAsync(step, context);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Succeeded);
		result.Content.Should().Be("Plain text content");
	}

	#endregion

	#region Parameter Resolution

	[Fact]
	public async Task ExecuteAsync_ParameterTemplate_ResolvesParameters()
	{
		// Arrange
		var executor = CreateExecutor();
		var step = CreateTransformStep(
			template: "Hello, {{param.name}}! Your ID is {{param.id}}.",
			parameters: ["name", "id"]);

		var context = new OrchestrationExecutionContext
		{
			OrchestrationInfo = s_defaultInfo,
			Parameters = new Dictionary<string, string>
			{
				["name"] = "Alice",
				["id"] = "42"
			}
		};

		// Act
		var result = await executor.ExecuteAsync(step, context);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Succeeded);
		result.Content.Should().Be("Hello, Alice! Your ID is 42.");
	}

	#endregion

	#region Dependency Resolution

	[Fact]
	public async Task ExecuteAsync_DependencyTemplate_ResolvesStepOutput()
	{
		// Arrange
		var executor = CreateExecutor();
		var step = CreateTransformStep(
			template: "Summary: {{step1.output}}",
			dependsOn: ["step1"]);

		var context = new OrchestrationExecutionContext { OrchestrationInfo = s_defaultInfo, Parameters = new Dictionary<string, string>() };
		context.AddResult("step1", ExecutionResult.Succeeded("Generated analysis report"));

		// Act
		var result = await executor.ExecuteAsync(step, context);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Succeeded);
		result.Content.Should().Be("Summary: Generated analysis report");
	}

	#endregion

	#region Mixed Templates

	[Fact]
	public async Task ExecuteAsync_MixedTemplate_ResolvesAllExpressions()
	{
		// Arrange
		var executor = CreateExecutor();
		var step = CreateTransformStep(
			template: "Dear {{param.recipient}},\n\nHere is the report:\n{{report.output}}\n\nRegards,\n{{param.sender}}",
			dependsOn: ["report"],
			parameters: ["recipient", "sender"]);

		var context = new OrchestrationExecutionContext
		{
			OrchestrationInfo = s_defaultInfo,
			Parameters = new Dictionary<string, string>
			{
				["recipient"] = "Bob",
				["sender"] = "Alice"
			}
		};
		context.AddResult("report", ExecutionResult.Succeeded("Q4 revenue increased by 15%."));

		// Act
		var result = await executor.ExecuteAsync(step, context);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Succeeded);
		result.Content.Should().Contain("Dear Bob,");
		result.Content.Should().Contain("Q4 revenue increased by 15%.");
		result.Content.Should().Contain("Alice");
	}

	#endregion

	#region Wrong Step Type

	[Fact]
	public async Task ExecuteAsync_WrongStepType_ThrowsInvalidOperationException()
	{
		// Arrange
		var executor = CreateExecutor();
		var wrongStep = new PromptOrchestrationStep
		{
			Name = "wrong-step",
			Type = OrchestrationStepType.Prompt,
			DependsOn = [],
			SystemPrompt = "system",
			UserPrompt = "user",
			Model = "claude-opus-4.5"
		};

		var context = new OrchestrationExecutionContext { OrchestrationInfo = s_defaultInfo, Parameters = new Dictionary<string, string>() };

		// Act
		var act = () => executor.ExecuteAsync(wrongStep, context);

		// Assert
		await act.Should().ThrowAsync<InvalidOperationException>()
			.WithMessage("*TransformStepExecutor*PromptOrchestrationStep*TransformOrchestrationStep*");
	}

	#endregion

	#region Orchestration Namespace Resolution

	[Fact]
	public async Task ExecuteAsync_OrchestrationTemplate_ResolvesOrchestrationMetadata()
	{
		// Arrange
		var executor = CreateExecutor();
		var startedAt = new DateTimeOffset(2025, 6, 15, 10, 30, 0, TimeSpan.Zero);
		var info = new OrchestrationInfo("my-pipeline", "2.0.0", "run-abc-123", startedAt);

		var step = CreateTransformStep(
			template: "Pipeline: {{orchestration.name}} v{{orchestration.version}}, Run: {{orchestration.runId}}, Started: {{orchestration.startedAt}}");

		var context = new OrchestrationExecutionContext
		{
			OrchestrationInfo = info,
			Parameters = new Dictionary<string, string>()
		};

		// Act
		var result = await executor.ExecuteAsync(step, context);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Succeeded);
		result.Content.Should().Be($"Pipeline: my-pipeline v2.0.0, Run: run-abc-123, Started: {startedAt:o}");
	}

	#endregion

	#region Step Namespace Resolution

	[Fact]
	public async Task ExecuteAsync_StepTemplate_ResolvesStepMetadata()
	{
		// Arrange
		var executor = CreateExecutor();
		var step = CreateTransformStep(
			name: "my-transform-step",
			template: "Step: {{step.name}}, Type: {{step.type}}");

		var context = new OrchestrationExecutionContext
		{
			OrchestrationInfo = s_defaultInfo,
			Parameters = new Dictionary<string, string>()
		};

		// Act
		var result = await executor.ExecuteAsync(step, context);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Succeeded);
		result.Content.Should().Be("Step: my-transform-step, Type: Transform");
	}

	#endregion

	#region Variables Namespace Resolution

	[Fact]
	public async Task ExecuteAsync_VarsTemplate_ResolvesVariables()
	{
		// Arrange
		var executor = CreateExecutor();
		var step = CreateTransformStep(
			template: "Base URL: {{vars.baseUrl}}, Env: {{vars.environment}}");

		var context = new OrchestrationExecutionContext
		{
			OrchestrationInfo = s_defaultInfo,
			Parameters = new Dictionary<string, string>(),
			Variables = new Dictionary<string, string>
			{
				["baseUrl"] = "https://api.example.com",
				["environment"] = "production"
			}
		};

		// Act
		var result = await executor.ExecuteAsync(step, context);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Succeeded);
		result.Content.Should().Be("Base URL: https://api.example.com, Env: production");
	}

	[Fact]
	public async Task ExecuteAsync_VarsWithRecursiveExpansion_ResolvesParamInVariable()
	{
		// Arrange
		var executor = CreateExecutor();
		var step = CreateTransformStep(
			template: "Output: {{vars.outputDir}}",
			parameters: ["project"]);

		var context = new OrchestrationExecutionContext
		{
			OrchestrationInfo = s_defaultInfo,
			Parameters = new Dictionary<string, string>
			{
				["project"] = "myapp"
			},
			Variables = new Dictionary<string, string>
			{
				["outputDir"] = "/reports/{{param.project}}/latest"
			}
		};

		// Act
		var result = await executor.ExecuteAsync(step, context);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Succeeded);
		result.Content.Should().Be("Output: /reports/myapp/latest");
	}

	#endregion

	#region All Namespaces Combined

	[Fact]
	public async Task ExecuteAsync_AllTemplateNamespaces_ResolvesEverything()
	{
		// Arrange
		var executor = CreateExecutor();
		var info = new OrchestrationInfo("full-test", "3.0.0", "run-xyz", DateTimeOffset.UtcNow);

		var step = CreateTransformStep(
			name: "combined-step",
			template: "Orch={{orchestration.name}}, Step={{step.name}}, Var={{vars.region}}, Param={{param.userId}}, Dep={{dep1.output}}",
			dependsOn: ["dep1"],
			parameters: ["userId"]);

		var context = new OrchestrationExecutionContext
		{
			OrchestrationInfo = info,
			Parameters = new Dictionary<string, string>
			{
				["userId"] = "user-42"
			},
			Variables = new Dictionary<string, string>
			{
				["region"] = "us-west-2"
			}
		};
		context.AddResult("dep1", ExecutionResult.Succeeded("dependency-output-data"));

		// Act
		var result = await executor.ExecuteAsync(step, context);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Succeeded);
		result.Content.Should().Be("Orch=full-test, Step=combined-step, Var=us-west-2, Param=user-42, Dep=dependency-output-data");
	}

	#endregion

	#region Env Namespace Resolution

	[Fact]
	public async Task ExecuteAsync_EnvTemplate_ResolvesEnvironmentVariables()
	{
		// Arrange
		Environment.SetEnvironmentVariable("ORCHESTRA_TEST_DB_URL", "postgres://db.example.com:5432/mydb");
		try
		{
			var executor = CreateExecutor();
			var step = CreateTransformStep(
				template: "Database: {{env.ORCHESTRA_TEST_DB_URL}}");

			var context = new OrchestrationExecutionContext
			{
				OrchestrationInfo = s_defaultInfo,
				Parameters = new Dictionary<string, string>()
			};

			// Act
			var result = await executor.ExecuteAsync(step, context);

			// Assert
			result.Status.Should().Be(ExecutionStatus.Succeeded);
			result.Content.Should().Be("Database: postgres://db.example.com:5432/mydb");
		}
		finally
		{
			Environment.SetEnvironmentVariable("ORCHESTRA_TEST_DB_URL", null);
		}
	}

	[Fact]
	public async Task ExecuteAsync_EnvMixedWithAllNamespaces_ResolvesEverything()
	{
		// Arrange
		Environment.SetEnvironmentVariable("ORCHESTRA_TEST_SECRET", "sk-test-key");
		try
		{
			var executor = CreateExecutor();
			var info = new OrchestrationInfo("full-test", "3.0.0", "run-xyz", DateTimeOffset.UtcNow);

			var step = CreateTransformStep(
				name: "combined-step",
				template: "Orch={{orchestration.name}}, Step={{step.name}}, Var={{vars.region}}, Param={{param.userId}}, Dep={{dep1.output}}, Secret={{env.ORCHESTRA_TEST_SECRET}}",
				dependsOn: ["dep1"],
				parameters: ["userId"]);

			var context = new OrchestrationExecutionContext
			{
				OrchestrationInfo = info,
				Parameters = new Dictionary<string, string>
				{
					["userId"] = "user-42"
				},
				Variables = new Dictionary<string, string>
				{
					["region"] = "us-west-2"
				}
			};
			context.AddResult("dep1", ExecutionResult.Succeeded("dependency-output-data"));

			// Act
			var result = await executor.ExecuteAsync(step, context);

			// Assert
			result.Status.Should().Be(ExecutionStatus.Succeeded);
			result.Content.Should().Be("Orch=full-test, Step=combined-step, Var=us-west-2, Param=user-42, Dep=dependency-output-data, Secret=sk-test-key");
		}
		finally
		{
			Environment.SetEnvironmentVariable("ORCHESTRA_TEST_SECRET", null);
		}
	}

	#endregion

	#region Cancellation

	[Fact]
	public async Task ExecuteAsync_Cancellation_ThrowsOperationCanceledException()
	{
		// Arrange
		var executor = CreateExecutor();
		var step = CreateTransformStep(template: "some content");
		var context = new OrchestrationExecutionContext { OrchestrationInfo = s_defaultInfo, Parameters = new Dictionary<string, string>() };
		using var cts = new CancellationTokenSource();
		cts.Cancel();

		// Act
		var act = () => executor.ExecuteAsync(step, context, cts.Token);

		// Assert
		await act.Should().ThrowAsync<OperationCanceledException>();
	}

	#endregion
}
