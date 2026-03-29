using FluentAssertions;

namespace Orchestra.Engine.Tests.EngineTools;

public class EngineToolContextTests
{
	[Fact]
	public void NewContext_HasNoStatusOverride()
	{
		var context = new EngineToolContext();

		context.HasStatusOverride.Should().BeFalse();
		context.StatusOverride.Should().BeNull();
		context.StatusReason.Should().BeNull();
	}

	[Fact]
	public void SetStatus_Failed_SetsOverride()
	{
		var context = new EngineToolContext();

		context.SetStatus(ExecutionStatus.Failed, "Something went wrong");

		context.HasStatusOverride.Should().BeTrue();
		context.StatusOverride.Should().Be(ExecutionStatus.Failed);
		context.StatusReason.Should().Be("Something went wrong");
	}

	[Fact]
	public void SetStatus_FailedTwice_KeepsFirstFailure()
	{
		var context = new EngineToolContext();

		context.SetStatus(ExecutionStatus.Failed, "First failure");
		context.SetStatus(ExecutionStatus.Failed, "Second failure");

		context.StatusReason.Should().Be("First failure");
	}

	[Fact]
	public void SetStatus_Succeeded_SetsOverride()
	{
		var context = new EngineToolContext();

		context.SetStatus(ExecutionStatus.Succeeded, "Task completed");

		context.HasStatusOverride.Should().BeTrue();
		context.StatusOverride.Should().Be(ExecutionStatus.Succeeded);
		context.StatusReason.Should().Be("Task completed");
	}

	[Fact]
	public void SetStatus_SucceededThenFailed_TransitionsToFailed()
	{
		var context = new EngineToolContext();

		context.SetStatus(ExecutionStatus.Succeeded, "Done");
		context.SetStatus(ExecutionStatus.Failed, "Actually failed");

		context.StatusOverride.Should().Be(ExecutionStatus.Failed);
		context.StatusReason.Should().Be("Actually failed");
	}

	[Fact]
	public void SetStatus_FailedThenSucceeded_StaysFailed()
	{
		var context = new EngineToolContext();

		context.SetStatus(ExecutionStatus.Failed, "Failed");
		context.SetStatus(ExecutionStatus.Succeeded, "Trying to reset");

		context.StatusOverride.Should().Be(ExecutionStatus.Failed);
		context.StatusReason.Should().Be("Failed");
	}

	[Fact]
	public void SetStatus_WithNullReason_SetsNullReason()
	{
		var context = new EngineToolContext();

		context.SetStatus(ExecutionStatus.Failed);

		context.HasStatusOverride.Should().BeTrue();
		context.StatusReason.Should().BeNull();
	}
}
