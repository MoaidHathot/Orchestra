using FluentAssertions;

namespace Orchestra.Engine.Tests.Formatting;

public class DefaultPromptFormatterTests
{
	private readonly IPromptFormatter _formatter = DefaultPromptFormatter.Instance;

	#region FormatDependencyOutputs

	[Fact]
	public void FormatDependencyOutputs_EmptyDictionary_ReturnsEmptyString()
	{
		// Arrange
		var outputs = new Dictionary<string, string>();

		// Act
		var result = _formatter.FormatDependencyOutputs(outputs);

		// Assert
		result.Should().BeEmpty();
	}

	[Fact]
	public void FormatDependencyOutputs_SingleDependency_ReturnsContentDirectly()
	{
		// Arrange
		var outputs = new Dictionary<string, string>
		{
			["step1"] = "output content"
		};

		// Act
		var result = _formatter.FormatDependencyOutputs(outputs);

		// Assert
		result.Should().Be("output content");
	}

	[Fact]
	public void FormatDependencyOutputs_MultipleDependencies_FormatsWithHeaders()
	{
		// Arrange
		var outputs = new Dictionary<string, string>
		{
			["step1"] = "output1",
			["step2"] = "output2"
		};

		// Act
		var result = _formatter.FormatDependencyOutputs(outputs);

		// Assert
		result.Should().Contain("## Output from 'step1':");
		result.Should().Contain("output1");
		result.Should().Contain("## Output from 'step2':");
		result.Should().Contain("output2");
		result.Should().Contain("---"); // Separator
	}

	#endregion

	#region BuildUserPrompt

	[Fact]
	public void BuildUserPrompt_NoDependenciesNoFeedback_ReturnsUserPromptOnly()
	{
		// Arrange
		var userPrompt = "Do something";
		var dependencyOutputs = string.Empty;

		// Act
		var result = _formatter.BuildUserPrompt(userPrompt, dependencyOutputs);

		// Assert
		result.Should().Be("Do something");
	}

	[Fact]
	public void BuildUserPrompt_WithDependencyOutputs_IncludesContext()
	{
		// Arrange
		var userPrompt = "Summarize the output";
		var dependencyOutputs = "Previous step output";

		// Act
		var result = _formatter.BuildUserPrompt(userPrompt, dependencyOutputs);

		// Assert
		result.Should().Contain("Summarize the output");
		result.Should().Contain("Context from previous steps:");
		result.Should().Contain("Previous step output");
	}

	[Fact]
	public void BuildUserPrompt_WithLoopFeedback_IncludesFeedbackSection()
	{
		// Arrange
		var userPrompt = "Write code";
		var dependencyOutputs = "Requirements doc";
		var loopFeedback = "Please add more error handling";

		// Act
		var result = _formatter.BuildUserPrompt(userPrompt, dependencyOutputs, loopFeedback);

		// Assert
		result.Should().Contain("Write code");
		result.Should().Contain("Requirements doc");
		result.Should().Contain("Feedback from previous attempt");
		result.Should().Contain("Please add more error handling");
	}

	[Fact]
	public void BuildUserPrompt_WithInputHandlerPrompt_UsesAlternateFormat()
	{
		// Arrange
		var userPrompt = "Generate tests";
		var dependencyOutputs = "Source code";
		var inputHandlerPrompt = "Extract function signatures first";

		// Act
		var result = _formatter.BuildUserPrompt(userPrompt, dependencyOutputs, inputHandlerPrompt: inputHandlerPrompt);

		// Assert
		result.Should().Contain("Extract function signatures first");
		result.Should().Contain("Previous step outputs:");
		result.Should().Contain("Task:");
		result.Should().Contain("Generate tests");
	}

	[Fact]
	public void BuildUserPrompt_WithInputHandlerAndFeedback_IncludesBoth()
	{
		// Arrange
		var userPrompt = "Generate tests";
		var dependencyOutputs = "Source code";
		var loopFeedback = "Add edge cases";
		var inputHandlerPrompt = "Extract function signatures first";

		// Act
		var result = _formatter.BuildUserPrompt(userPrompt, dependencyOutputs, loopFeedback, inputHandlerPrompt);

		// Assert
		result.Should().Contain("Extract function signatures first");
		result.Should().Contain("Feedback from previous attempt");
		result.Should().Contain("Add edge cases");
	}

	[Fact]
	public void BuildUserPrompt_EmptyDependenciesWithFeedback_IncludesFeedback()
	{
		// Arrange
		var userPrompt = "Write code";
		var dependencyOutputs = string.Empty;
		var loopFeedback = "Improve performance";

		// Act
		var result = _formatter.BuildUserPrompt(userPrompt, dependencyOutputs, loopFeedback);

		// Assert
		result.Should().Contain("Feedback from previous attempt");
		result.Should().Contain("Improve performance");
	}

	#endregion

	#region BuildTransformationSystemPrompt

	[Fact]
	public void BuildTransformationSystemPrompt_IncludesInstructions()
	{
		// Arrange
		var instructions = "Convert to JSON format";

		// Act
		var result = _formatter.BuildTransformationSystemPrompt(instructions);

		// Assert
		result.Should().Contain("Convert to JSON format");
		result.Should().Contain("TRANSFORMATION INSTRUCTIONS");
	}

	[Fact]
	public void BuildTransformationSystemPrompt_IncludesCriticalRules()
	{
		// Arrange
		var instructions = "Extract key points";

		// Act
		var result = _formatter.BuildTransformationSystemPrompt(instructions);

		// Assert
		result.Should().Contain("CRITICAL RULES");
		result.Should().Contain("content transformation function");
		result.Should().Contain("OUTPUT FORMAT");
	}

	#endregion

	#region WrapContentForTransformation

	[Fact]
	public void WrapContentForTransformation_WrapsWithTags()
	{
		// Arrange
		var content = "Some content to transform";

		// Act
		var result = _formatter.WrapContentForTransformation(content);

		// Assert
		result.Should().Contain("<INPUT_CONTENT>");
		result.Should().Contain("Some content to transform");
		result.Should().Contain("</INPUT_CONTENT>");
	}

	[Fact]
	public void WrapContentForTransformation_IncludesTransformInstruction()
	{
		// Arrange
		var content = "Data to process";

		// Act
		var result = _formatter.WrapContentForTransformation(content);

		// Assert
		result.Should().Contain("Transform the content above");
	}

	#endregion

	#region Singleton

	[Fact]
	public void Instance_ReturnsSameInstance()
	{
		// Act
		var instance1 = DefaultPromptFormatter.Instance;
		var instance2 = DefaultPromptFormatter.Instance;

		// Assert
		instance1.Should().BeSameAs(instance2);
	}

	#endregion
}
