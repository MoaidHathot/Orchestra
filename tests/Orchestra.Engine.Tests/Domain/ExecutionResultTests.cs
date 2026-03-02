using FluentAssertions;

namespace Orchestra.Engine.Tests.Domain;

public class ExecutionResultTests
{
	#region Succeeded Factory Method

	[Fact]
	public void Succeeded_WithContent_CreatesSuccessResult()
	{
		// Act
		var result = ExecutionResult.Succeeded("Test content");

		// Assert
		result.Status.Should().Be(ExecutionStatus.Succeeded);
		result.Content.Should().Be("Test content");
		result.ErrorMessage.Should().BeNull();
	}

	[Fact]
	public void Succeeded_WithAllParameters_SetsAllProperties()
	{
		// Arrange
		var rawDeps = new Dictionary<string, string> { ["step1"] = "output1" };
		var usage = new TokenUsage { InputTokens = 100, OutputTokens = 50 };
		var trace = new StepExecutionTrace
		{
			SystemPrompt = "system",
			UserPromptRaw = "user raw",
			UserPromptProcessed = "user processed"
		};

		// Act
		var result = ExecutionResult.Succeeded(
			content: "Final content",
			rawContent: "Raw content",
			rawDependencyOutputs: rawDeps,
			promptSent: "The prompt",
			actualModel: "claude-opus-4.5",
			usage: usage,
			trace: trace);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Succeeded);
		result.Content.Should().Be("Final content");
		result.RawContent.Should().Be("Raw content");
		result.RawDependencyOutputs.Should().ContainKey("step1");
		result.RawDependencyOutputs["step1"].Should().Be("output1");
		result.PromptSent.Should().Be("The prompt");
		result.ActualModel.Should().Be("claude-opus-4.5");
		result.Usage.Should().BeSameAs(usage);
		result.Trace.Should().BeSameAs(trace);
	}

	[Fact]
	public void Succeeded_WithDefaults_HasEmptyDependencyOutputs()
	{
		// Act
		var result = ExecutionResult.Succeeded("content");

		// Assert
		result.RawDependencyOutputs.Should().BeEmpty();
		result.RawContent.Should().BeNull();
		result.PromptSent.Should().BeNull();
		result.ActualModel.Should().BeNull();
		result.Usage.Should().BeNull();
		result.Trace.Should().BeNull();
	}

	#endregion

	#region Failed Factory Method

	[Fact]
	public void Failed_WithErrorMessage_CreatesFailedResult()
	{
		// Act
		var result = ExecutionResult.Failed("Something went wrong");

		// Assert
		result.Status.Should().Be(ExecutionStatus.Failed);
		result.Content.Should().BeEmpty();
		result.ErrorMessage.Should().Be("Something went wrong");
	}

	[Fact]
	public void Failed_WithAllParameters_SetsAllProperties()
	{
		// Arrange
		var rawDeps = new Dictionary<string, string> { ["dep1"] = "value1" };
		var trace = new StepExecutionTrace
		{
			SystemPrompt = "sys",
			UserPromptRaw = "usr raw",
			UserPromptProcessed = "usr processed"
		};

		// Act
		var result = ExecutionResult.Failed(
			errorMessage: "Error occurred",
			rawDependencyOutputs: rawDeps,
			promptSent: "attempted prompt",
			actualModel: "gpt-4",
			trace: trace);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Failed);
		result.ErrorMessage.Should().Be("Error occurred");
		result.Content.Should().BeEmpty();
		result.RawDependencyOutputs.Should().ContainKey("dep1");
		result.PromptSent.Should().Be("attempted prompt");
		result.ActualModel.Should().Be("gpt-4");
		result.Trace.Should().BeSameAs(trace);
	}

	[Fact]
	public void Failed_DoesNotIncludeUsage()
	{
		// Act
		var result = ExecutionResult.Failed("error");

		// Assert - Failed results don't have token usage
		result.Usage.Should().BeNull();
	}

	#endregion

	#region Skipped Factory Method

	[Fact]
	public void Skipped_WithReason_CreatesSkippedResult()
	{
		// Act
		var result = ExecutionResult.Skipped("Dependency failed");

		// Assert
		result.Status.Should().Be(ExecutionStatus.Skipped);
		result.Content.Should().BeEmpty();
		result.ErrorMessage.Should().Be("Dependency failed");
	}

	[Fact]
	public void Skipped_HasEmptyDependencyOutputs()
	{
		// Act
		var result = ExecutionResult.Skipped("skipped");

		// Assert
		result.RawDependencyOutputs.Should().BeEmpty();
		result.RawContent.Should().BeNull();
		result.PromptSent.Should().BeNull();
		result.ActualModel.Should().BeNull();
		result.Usage.Should().BeNull();
		result.Trace.Should().BeNull();
	}

	#endregion

	#region Required Properties

	[Fact]
	public void ExecutionResult_RequiresContent()
	{
		// This test verifies that Content is a required property
		// by checking that the factory methods always set it
		var succeeded = ExecutionResult.Succeeded("content");
		var failed = ExecutionResult.Failed("error");
		var skipped = ExecutionResult.Skipped("reason");

		succeeded.Content.Should().NotBeNull();
		failed.Content.Should().NotBeNull();
		skipped.Content.Should().NotBeNull();
	}

	[Fact]
	public void ExecutionResult_RequiresStatus()
	{
		// This test verifies that Status is always set
		var succeeded = ExecutionResult.Succeeded("content");
		var failed = ExecutionResult.Failed("error");
		var skipped = ExecutionResult.Skipped("reason");

		succeeded.Status.Should().Be(ExecutionStatus.Succeeded);
		failed.Status.Should().Be(ExecutionStatus.Failed);
		skipped.Status.Should().Be(ExecutionStatus.Skipped);
	}

	#endregion
}
