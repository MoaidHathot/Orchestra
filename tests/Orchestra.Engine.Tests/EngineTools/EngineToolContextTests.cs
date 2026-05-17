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
	public void SetStatus_SucceededThenFailed_KeepsFirstSucceeded()
	{
		// Fix B: first terminal status wins. Without this, a CLI swap that re-runs the
		// prompt could observe the LLM declaring success on attempt 1 and failure on
		// attempt 2, and the second call would silently overwrite the first — the
		// exact bug reported on run 505940e23cc1.
		var context = new EngineToolContext();

		context.SetStatus(ExecutionStatus.Succeeded, "Done");
		context.SetStatus(ExecutionStatus.Failed, "Trying to overwrite success");

		context.StatusOverride.Should().Be(ExecutionStatus.Succeeded, "first terminal status must be sticky for all terminal values");
		context.StatusReason.Should().Be("Done");
	}

	[Fact]
	public void SetStatus_FailedThenSucceeded_StaysFailed()
	{
		// Existing first-wins behaviour (Failed was sticky pre-Fix-B). Regression guard
		// for the case the prior implementation already covered.
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

	[Fact]
	public void SetStatus_NoAction_SetsOverride()
	{
		var context = new EngineToolContext();

		context.SetStatus(ExecutionStatus.NoAction, "No incidents to process");

		context.HasStatusOverride.Should().BeTrue();
		context.StatusOverride.Should().Be(ExecutionStatus.NoAction);
		context.StatusReason.Should().Be("No incidents to process");
	}

	[Fact]
	public void SetStatus_NoActionThenFailed_KeepsFirstNoAction()
	{
		// Fix B: no_action is also first-wins. Previously NoAction could be overridden
		// by Failed; that allowed late-arriving overrides to flip the step's outcome.
		var context = new EngineToolContext();

		context.SetStatus(ExecutionStatus.NoAction, "Nothing to do");
		context.SetStatus(ExecutionStatus.Failed, "Trying to overwrite no_action");

		context.StatusOverride.Should().Be(ExecutionStatus.NoAction);
		context.StatusReason.Should().Be("Nothing to do");
	}

	[Fact]
	public void SetStatus_FailedThenNoAction_StaysFailed()
	{
		var context = new EngineToolContext();

		context.SetStatus(ExecutionStatus.Failed, "Failed");
		context.SetStatus(ExecutionStatus.NoAction, "Trying to reset");

		context.StatusOverride.Should().Be(ExecutionStatus.Failed);
		context.StatusReason.Should().Be("Failed");
	}

	[Fact]
	public void SetStatus_NoActionThenSucceeded_KeepsFirstNoAction()
	{
		var context = new EngineToolContext();

		context.SetStatus(ExecutionStatus.NoAction, "Nothing to do");
		context.SetStatus(ExecutionStatus.Succeeded, "Trying to overwrite no_action");

		context.StatusOverride.Should().Be(ExecutionStatus.NoAction);
		context.StatusReason.Should().Be("Nothing to do");
	}

	[Fact]
	public void SetStatus_SucceededThenNoAction_KeepsFirstSucceeded()
	{
		var context = new EngineToolContext();

		context.SetStatus(ExecutionStatus.Succeeded, "Done");
		context.SetStatus(ExecutionStatus.NoAction, "Trying to overwrite success");

		context.StatusOverride.Should().Be(ExecutionStatus.Succeeded);
		context.StatusReason.Should().Be("Done");
	}

	[Fact]
	public void NewContext_OrchestrationCompleteNotRequested()
	{
		var context = new EngineToolContext();

		context.OrchestrationCompleteRequested.Should().BeFalse();
		context.OrchestrationCompleteStatus.Should().BeNull();
		context.OrchestrationCompleteReason.Should().BeNull();
	}

	[Fact]
	public void CompleteOrchestration_Success_SetsAllProperties()
	{
		var context = new EngineToolContext();

		context.CompleteOrchestration(ExecutionStatus.Succeeded, "Nothing to process");

		context.OrchestrationCompleteRequested.Should().BeTrue();
		context.OrchestrationCompleteStatus.Should().Be(ExecutionStatus.Succeeded);
		context.OrchestrationCompleteReason.Should().Be("Nothing to process");
	}

	[Fact]
	public void CompleteOrchestration_Failed_SetsAllProperties()
	{
		var context = new EngineToolContext();

		context.CompleteOrchestration(ExecutionStatus.Failed, "Critical error detected");

		context.OrchestrationCompleteRequested.Should().BeTrue();
		context.OrchestrationCompleteStatus.Should().Be(ExecutionStatus.Failed);
		context.OrchestrationCompleteReason.Should().Be("Critical error detected");
	}

	[Fact]
	public void CompleteOrchestration_WithNullReason_SetsNullReason()
	{
		var context = new EngineToolContext();

		context.CompleteOrchestration(ExecutionStatus.Succeeded);

		context.OrchestrationCompleteRequested.Should().BeTrue();
		context.OrchestrationCompleteStatus.Should().Be(ExecutionStatus.Succeeded);
		context.OrchestrationCompleteReason.Should().BeNull();
	}

	#region StepName (Fix #6)

	[Fact]
	public void NewContext_StepNameIsNull()
	{
		var context = new EngineToolContext();

		context.StepName.Should().BeNull();
	}

	[Fact]
	public void Context_WithStepName_ReturnsStepName()
	{
		var context = new EngineToolContext { StepName = "research" };

		context.StepName.Should().Be("research");
	}

	#endregion
}
