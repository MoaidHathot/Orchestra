using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Orchestra.Engine.Tests.Executor;

public class TransformStepExecutorTests
{
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
		var context = new OrchestrationExecutionContext { Parameters = new Dictionary<string, string>() };

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

		var context = new OrchestrationExecutionContext { Parameters = new Dictionary<string, string>() };
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

		var context = new OrchestrationExecutionContext { Parameters = new Dictionary<string, string>() };

		// Act
		var act = () => executor.ExecuteAsync(wrongStep, context);

		// Assert
		await act.Should().ThrowAsync<InvalidOperationException>()
			.WithMessage("*TransformStepExecutor*PromptOrchestrationStep*TransformOrchestrationStep*");
	}

	#endregion

	#region Cancellation

	[Fact]
	public async Task ExecuteAsync_Cancellation_ThrowsOperationCanceledException()
	{
		// Arrange
		var executor = CreateExecutor();
		var step = CreateTransformStep(template: "some content");
		var context = new OrchestrationExecutionContext { Parameters = new Dictionary<string, string>() };
		using var cts = new CancellationTokenSource();
		cts.Cancel();

		// Act
		var act = () => executor.ExecuteAsync(step, context, cts.Token);

		// Assert
		await act.Should().ThrowAsync<OperationCanceledException>();
	}

	#endregion
}
