using FluentAssertions;

namespace Orchestra.Engine.Tests.Executor;

public class TemplateResolverTests
{
	[Fact]
	public void Resolve_ParameterExpression_ReplacesWithValue()
	{
		// Arrange
		var context = new OrchestrationExecutionContext
		{
			Parameters = new Dictionary<string, string> { ["topic"] = "AI" }
		};
		var parameters = new Dictionary<string, string> { ["topic"] = "AI" };
		var template = "Write about {{param.topic}}";

		// Act
		var result = TemplateResolver.Resolve(template, parameters, context, []);

		// Assert
		result.Should().Be("Write about AI");
	}

	[Fact]
	public void Resolve_MultipleParameters_ReplacesAll()
	{
		// Arrange
		var context = new OrchestrationExecutionContext
		{
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
		var result = TemplateResolver.Resolve(template, parameters, context, []);

		// Assert
		result.Should().Be("Write about AI in a formal tone, approximately 500 words");
	}

	[Fact]
	public void Resolve_StepOutputExpression_ReplacesWithContent()
	{
		// Arrange
		var context = new OrchestrationExecutionContext
		{
			Parameters = new Dictionary<string, string>()
		};
		context.AddResult("step1", ExecutionResult.Succeeded("processed content", rawContent: "raw content"));
		var template = "Use the output: {{step1.output}}";

		// Act
		var result = TemplateResolver.Resolve(template, new Dictionary<string, string>(), context, ["step1"]);

		// Assert
		result.Should().Be("Use the output: processed content");
	}

	[Fact]
	public void Resolve_StepRawOutputExpression_ReplacesWithRawContent()
	{
		// Arrange
		var context = new OrchestrationExecutionContext
		{
			Parameters = new Dictionary<string, string>()
		};
		context.AddResult("step1", ExecutionResult.Succeeded("processed content", rawContent: "raw content"));
		var template = "Use the raw output: {{step1.rawOutput}}";

		// Act
		var result = TemplateResolver.Resolve(template, new Dictionary<string, string>(), context, ["step1"]);

		// Assert
		result.Should().Be("Use the raw output: raw content");
	}

	[Fact]
	public void Resolve_StepRawOutput_FallsBackToContent_WhenRawContentNull()
	{
		// Arrange
		var context = new OrchestrationExecutionContext
		{
			Parameters = new Dictionary<string, string>()
		};
		context.AddResult("step1", ExecutionResult.Succeeded("processed content"));
		var template = "Use the raw output: {{step1.rawOutput}}";

		// Act
		var result = TemplateResolver.Resolve(template, new Dictionary<string, string>(), context, ["step1"]);

		// Assert
		result.Should().Be("Use the raw output: processed content");
	}

	[Fact]
	public void Resolve_UnknownParameter_LeavesAsIs()
	{
		// Arrange
		var context = new OrchestrationExecutionContext
		{
			Parameters = new Dictionary<string, string>()
		};
		var parameters = new Dictionary<string, string>();
		var template = "Value is {{param.unknown}}";

		// Act
		var result = TemplateResolver.Resolve(template, parameters, context, []);

		// Assert
		result.Should().Be("Value is {{param.unknown}}");
	}

	[Fact]
	public void Resolve_UnknownStep_LeavesAsIs()
	{
		// Arrange
		var context = new OrchestrationExecutionContext
		{
			Parameters = new Dictionary<string, string>()
		};
		var template = "Value is {{unknown.output}}";

		// Act
		var result = TemplateResolver.Resolve(template, new Dictionary<string, string>(), context, []);

		// Assert
		result.Should().Be("Value is {{unknown.output}}");
	}

	[Fact]
	public void Resolve_MixedExpressions_ResolvesAll()
	{
		// Arrange
		var context = new OrchestrationExecutionContext
		{
			Parameters = new Dictionary<string, string> { ["topic"] = "AI" }
		};
		var parameters = new Dictionary<string, string> { ["topic"] = "AI" };
		context.AddResult("research", ExecutionResult.Succeeded("research findings"));
		context.AddResult("outline", ExecutionResult.Succeeded("document outline", rawContent: "raw outline"));
		var template = "Write about {{param.topic}} using {{research.output}} and follow {{outline.rawOutput}}";

		// Act
		var result = TemplateResolver.Resolve(template, parameters, context, ["research", "outline"]);

		// Assert
		result.Should().Be("Write about AI using research findings and follow raw outline");
	}

	[Fact]
	public void Resolve_NoExpressions_ReturnsUnchanged()
	{
		// Arrange
		var context = new OrchestrationExecutionContext
		{
			Parameters = new Dictionary<string, string>()
		};
		var template = "This is a plain text template with no expressions.";

		// Act
		var result = TemplateResolver.Resolve(template, new Dictionary<string, string>(), context, []);

		// Assert
		result.Should().Be("This is a plain text template with no expressions.");
	}

	[Fact]
	public void Resolve_NonDependencyStep_FallsBackToTryGetResult()
	{
		// Arrange
		var context = new OrchestrationExecutionContext
		{
			Parameters = new Dictionary<string, string>()
		};
		context.AddResult("otherStep", ExecutionResult.Succeeded("fallback content", rawContent: "fallback raw"));
		var template = "Use {{otherStep.output}} and {{otherStep.rawOutput}}";

		// Act — otherStep is NOT in dependsOn, but exists in context
		var result = TemplateResolver.Resolve(template, new Dictionary<string, string>(), context, ["unrelatedStep"]);

		// Assert — falls back to TryGetResult path
		result.Should().Be("Use fallback content and fallback raw");
	}
}
