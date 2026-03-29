using FluentAssertions;

namespace Orchestra.Engine.Tests.Executor;

public class OrchestrationExecutionContextTests
{
	#region Result Storage and Retrieval

	[Fact]
	public void AddResult_StoresResult()
	{
		// Arrange
		var context = new OrchestrationExecutionContext { Parameters = new Dictionary<string, string>() };
		var result = ExecutionResult.Succeeded("test content");

		// Act
		context.AddResult("step1", result);

		// Assert
		context.Results.Should().ContainKey("step1");
		context.Results["step1"].Should().BeSameAs(result);
	}

	[Fact]
	public void GetResult_WhenExists_ReturnsResult()
	{
		// Arrange
		var context = new OrchestrationExecutionContext { Parameters = new Dictionary<string, string>() };
		var result = ExecutionResult.Succeeded("test content");
		context.AddResult("step1", result);

		// Act
		var retrieved = context.GetResult("step1");

		// Assert
		retrieved.Should().BeSameAs(result);
	}

	[Fact]
	public void GetResult_WhenNotExists_ThrowsInvalidOperationException()
	{
		// Arrange
		var context = new OrchestrationExecutionContext { Parameters = new Dictionary<string, string>() };

		// Act
		var act = () => context.GetResult("nonexistent");

		// Assert
		act.Should().Throw<InvalidOperationException>()
			.WithMessage("*No result found for step 'nonexistent'*");
	}

	[Fact]
	public void AddResult_OverwritesExistingResult()
	{
		// Arrange
		var context = new OrchestrationExecutionContext { Parameters = new Dictionary<string, string>() };
		var result1 = ExecutionResult.Succeeded("first");
		var result2 = ExecutionResult.Succeeded("second");

		// Act
		context.AddResult("step1", result1);
		context.AddResult("step1", result2);

		// Assert
		context.GetResult("step1").Content.Should().Be("second");
	}

	[Fact]
	public void ClearResult_RemovesResult()
	{
		// Arrange
		var context = new OrchestrationExecutionContext { Parameters = new Dictionary<string, string>() };
		context.AddResult("step1", ExecutionResult.Succeeded("test"));

		// Act
		context.ClearResult("step1");

		// Assert
		var act = () => context.GetResult("step1");
		act.Should().Throw<InvalidOperationException>();
	}

	[Fact]
	public void ClearResult_WhenNotExists_DoesNotThrow()
	{
		// Arrange
		var context = new OrchestrationExecutionContext { Parameters = new Dictionary<string, string>() };

		// Act
		var act = () => context.ClearResult("nonexistent");

		// Assert
		act.Should().NotThrow();
	}

	#endregion

	#region HasAnyDependencyFailed

	[Fact]
	public void HasAnyDependencyFailed_WhenAllSucceeded_ReturnsFalse()
	{
		// Arrange
		var context = new OrchestrationExecutionContext { Parameters = new Dictionary<string, string>() };
		context.AddResult("dep1", ExecutionResult.Succeeded("result1"));
		context.AddResult("dep2", ExecutionResult.Succeeded("result2"));

		// Act
		var result = context.HasAnyDependencyFailed(["dep1", "dep2"]);

		// Assert
		result.Should().BeFalse();
	}

	[Fact]
	public void HasAnyDependencyFailed_WhenOneFailed_ReturnsTrue()
	{
		// Arrange
		var context = new OrchestrationExecutionContext { Parameters = new Dictionary<string, string>() };
		context.AddResult("dep1", ExecutionResult.Succeeded("result1"));
		context.AddResult("dep2", ExecutionResult.Failed("error"));

		// Act
		var result = context.HasAnyDependencyFailed(["dep1", "dep2"]);

		// Assert
		result.Should().BeTrue();
	}

	[Fact]
	public void HasAnyDependencyFailed_WhenOneSkipped_ReturnsTrue()
	{
		// Arrange
		var context = new OrchestrationExecutionContext { Parameters = new Dictionary<string, string>() };
		context.AddResult("dep1", ExecutionResult.Succeeded("result1"));
		context.AddResult("dep2", ExecutionResult.Skipped("reason"));

		// Act
		var result = context.HasAnyDependencyFailed(["dep1", "dep2"]);

		// Assert
		result.Should().BeTrue();
	}

	[Fact]
	public void HasAnyDependencyFailed_WhenNoDependencies_ReturnsFalse()
	{
		// Arrange
		var context = new OrchestrationExecutionContext { Parameters = new Dictionary<string, string>() };

		// Act
		var result = context.HasAnyDependencyFailed([]);

		// Assert
		result.Should().BeFalse();
	}

	[Fact]
	public void HasAnyDependencyFailed_WhenDependencyNotYetComplete_ReturnsFalse()
	{
		// Arrange
		var context = new OrchestrationExecutionContext { Parameters = new Dictionary<string, string>() };
		context.AddResult("dep1", ExecutionResult.Succeeded("result1"));
		// dep2 not added yet

		// Act
		var result = context.HasAnyDependencyFailed(["dep1", "dep2"]);

		// Assert - Missing dependencies are not considered "failed"
		result.Should().BeFalse();
	}

	[Fact]
	public void HasAnyDependencyFailed_WhenOneCancelled_ReturnsTrue()
	{
		// Arrange
		var context = new OrchestrationExecutionContext { Parameters = new Dictionary<string, string>() };
		context.AddResult("dep1", ExecutionResult.Succeeded("result1"));
		context.AddResult("dep2", ExecutionResult.Cancelled());

		// Act
		var result = context.HasAnyDependencyFailed(["dep1", "dep2"]);

		// Assert
		result.Should().BeTrue();
	}

	#endregion

	#region GetDependencyOutputs

	[Fact]
	public void GetDependencyOutputs_WhenNoDependencies_ReturnsEmptyDictionary()
	{
		// Arrange
		var context = new OrchestrationExecutionContext { Parameters = new Dictionary<string, string>() };

		// Act
		var result = context.GetDependencyOutputs([]);

		// Assert
		result.Should().BeEmpty();
	}

	[Fact]
	public void GetDependencyOutputs_SingleDependency_ReturnsDictionaryWithContent()
	{
		// Arrange
		var context = new OrchestrationExecutionContext { Parameters = new Dictionary<string, string>() };
		context.AddResult("dep1", ExecutionResult.Succeeded("output content"));

		// Act
		var result = context.GetDependencyOutputs(["dep1"]);

		// Assert
		result.Should().ContainKey("dep1");
		result["dep1"].Should().Be("output content");
	}

	[Fact]
	public void GetDependencyOutputs_MultipleDependencies_ReturnsDictionaryWithAllOutputs()
	{
		// Arrange
		var context = new OrchestrationExecutionContext { Parameters = new Dictionary<string, string>() };
		context.AddResult("dep1", ExecutionResult.Succeeded("output1"));
		context.AddResult("dep2", ExecutionResult.Succeeded("output2"));

		// Act
		var result = context.GetDependencyOutputs(["dep1", "dep2"]);

		// Assert
		result.Should().HaveCount(2);
		result.Should().ContainKey("dep1");
		result["dep1"].Should().Be("output1");
		result.Should().ContainKey("dep2");
		result["dep2"].Should().Be("output2");
	}

	[Fact]
	public void GetDependencyOutputs_SkipsFailedDependencies()
	{
		// Arrange
		var context = new OrchestrationExecutionContext { Parameters = new Dictionary<string, string>() };
		context.AddResult("dep1", ExecutionResult.Succeeded("output1"));
		context.AddResult("dep2", ExecutionResult.Failed("error"));

		// Act
		var result = context.GetDependencyOutputs(["dep1", "dep2"]);

		// Assert
		result.Should().ContainKey("dep1");
		result["dep1"].Should().Be("output1");
		result.Should().NotContainKey("dep2");
	}

	[Fact]
	public void GetDependencyOutputs_WhenAllFailed_ReturnsEmptyDictionary()
	{
		// Arrange
		var context = new OrchestrationExecutionContext { Parameters = new Dictionary<string, string>() };
		context.AddResult("dep1", ExecutionResult.Failed("error1"));
		context.AddResult("dep2", ExecutionResult.Skipped("reason"));

		// Act
		var result = context.GetDependencyOutputs(["dep1", "dep2"]);

		// Assert
		result.Should().BeEmpty();
	}

	#endregion

	#region GetRawDependencyOutputs

	[Fact]
	public void GetRawDependencyOutputs_ReturnsRawContentWhenAvailable()
	{
		// Arrange
		var context = new OrchestrationExecutionContext { Parameters = new Dictionary<string, string>() };
		var result = ExecutionResult.Succeeded("processed", rawContent: "raw content");
		context.AddResult("dep1", result);

		// Act
		var outputs = context.GetRawDependencyOutputs(["dep1"]);

		// Assert
		outputs.Should().ContainKey("dep1");
		outputs["dep1"].Should().Be("raw content");
	}

	[Fact]
	public void GetRawDependencyOutputs_FallsBackToContentWhenNoRaw()
	{
		// Arrange
		var context = new OrchestrationExecutionContext { Parameters = new Dictionary<string, string>() };
		var result = ExecutionResult.Succeeded("processed content");
		context.AddResult("dep1", result);

		// Act
		var outputs = context.GetRawDependencyOutputs(["dep1"]);

		// Assert
		outputs.Should().ContainKey("dep1");
		outputs["dep1"].Should().Be("processed content");
	}

	[Fact]
	public void GetRawDependencyOutputs_SkipsFailedDependencies()
	{
		// Arrange
		var context = new OrchestrationExecutionContext { Parameters = new Dictionary<string, string>() };
		context.AddResult("dep1", ExecutionResult.Succeeded("output1"));
		context.AddResult("dep2", ExecutionResult.Failed("error"));

		// Act
		var outputs = context.GetRawDependencyOutputs(["dep1", "dep2"]);

		// Assert
		outputs.Should().ContainKey("dep1");
		outputs.Should().NotContainKey("dep2");
	}

	#endregion

	#region Loop Feedback

	[Fact]
	public void SetLoopFeedback_StoresFeedback()
	{
		// Arrange
		var context = new OrchestrationExecutionContext { Parameters = new Dictionary<string, string>() };

		// Act
		context.SetLoopFeedback("step1", "feedback content");

		// Assert - Can be consumed
		var feedback = context.ConsumeLoopFeedback("step1");
		feedback.Should().Be("feedback content");
	}

	[Fact]
	public void ConsumeLoopFeedback_WhenNotSet_ReturnsNull()
	{
		// Arrange
		var context = new OrchestrationExecutionContext { Parameters = new Dictionary<string, string>() };

		// Act
		var feedback = context.ConsumeLoopFeedback("step1");

		// Assert
		feedback.Should().BeNull();
	}

	[Fact]
	public void ConsumeLoopFeedback_RemovesFeedbackAfterConsumption()
	{
		// Arrange
		var context = new OrchestrationExecutionContext { Parameters = new Dictionary<string, string>() };
		context.SetLoopFeedback("step1", "feedback content");

		// Act
		var first = context.ConsumeLoopFeedback("step1");
		var second = context.ConsumeLoopFeedback("step1");

		// Assert
		first.Should().Be("feedback content");
		second.Should().BeNull();
	}

	[Fact]
	public void SetLoopFeedback_OverwritesPreviousFeedback()
	{
		// Arrange
		var context = new OrchestrationExecutionContext { Parameters = new Dictionary<string, string>() };

		// Act
		context.SetLoopFeedback("step1", "first feedback");
		context.SetLoopFeedback("step1", "second feedback");

		// Assert
		var feedback = context.ConsumeLoopFeedback("step1");
		feedback.Should().Be("second feedback");
	}

	#endregion

	#region Parameters

	[Fact]
	public void Parameters_AreAccessible()
	{
		// Arrange
		var parameters = new Dictionary<string, string>
		{
			["param1"] = "value1",
			["param2"] = "value2"
		};

		var context = new OrchestrationExecutionContext { Parameters = parameters };

		// Assert
		context.Parameters.Should().ContainKey("param1");
		context.Parameters["param1"].Should().Be("value1");
	}

	#endregion

	#region DefaultSystemPromptMode

	[Fact]
	public void DefaultSystemPromptMode_WhenNotSet_IsNull()
	{
		// Arrange
		var context = new OrchestrationExecutionContext { Parameters = new Dictionary<string, string>() };

		// Assert
		context.DefaultSystemPromptMode.Should().BeNull();
	}

	[Fact]
	public void DefaultSystemPromptMode_WhenSetToReplace_ReturnsReplace()
	{
		// Arrange
		var context = new OrchestrationExecutionContext
		{
			Parameters = new Dictionary<string, string>(),
			DefaultSystemPromptMode = SystemPromptMode.Replace
		};

		// Assert
		context.DefaultSystemPromptMode.Should().Be(SystemPromptMode.Replace);
	}

	[Fact]
	public void DefaultSystemPromptMode_WhenSetToAppend_ReturnsAppend()
	{
		// Arrange
		var context = new OrchestrationExecutionContext
		{
			Parameters = new Dictionary<string, string>(),
			DefaultSystemPromptMode = SystemPromptMode.Append
		};

		// Assert
		context.DefaultSystemPromptMode.Should().Be(SystemPromptMode.Append);
	}

	#endregion
}
