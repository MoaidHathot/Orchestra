using FluentAssertions;

namespace Orchestra.Engine.Tests.EngineTools;

public class SetStatusToolTests
{
	[Fact]
	public void Name_ReturnsExpectedName()
	{
		var tool = new SetStatusTool();

		tool.Name.Should().Be("orchestra_set_status");
	}

	[Fact]
	public void Description_IsNotEmpty()
	{
		var tool = new SetStatusTool();

		tool.Description.Should().NotBeNullOrWhiteSpace();
	}

	[Fact]
	public void ParametersSchema_IsValidJson()
	{
		var tool = new SetStatusTool();

		var act = () => System.Text.Json.JsonDocument.Parse(tool.ParametersSchema);

		act.Should().NotThrow();
	}

	[Fact]
	public void Execute_StatusFailed_SetsContextToFailed()
	{
		var tool = new SetStatusTool();
		var context = new EngineToolContext();

		tool.Execute("""{"status": "failed", "reason": "MCP tools unavailable"}""", context);

		context.HasStatusOverride.Should().BeTrue();
		context.StatusOverride.Should().Be(ExecutionStatus.Failed);
		context.StatusReason.Should().Be("MCP tools unavailable");
	}

	[Fact]
	public void Execute_StatusFailed_ReturnsConfirmationMessage()
	{
		var tool = new SetStatusTool();
		var context = new EngineToolContext();

		var result = tool.Execute("""{"status": "failed", "reason": "Cannot proceed"}""", context);

		result.Should().Contain("failed");
	}

	[Fact]
	public void Execute_StatusFailedWithoutReason_UsesDefaultReason()
	{
		var tool = new SetStatusTool();
		var context = new EngineToolContext();

		tool.Execute("""{"status": "failed"}""", context);

		context.HasStatusOverride.Should().BeTrue();
		context.StatusOverride.Should().Be(ExecutionStatus.Failed);
		context.StatusReason.Should().Be("Step marked as failed by LLM");
	}

	[Fact]
	public void Execute_StatusSuccess_SetsContextToSucceeded()
	{
		var tool = new SetStatusTool();
		var context = new EngineToolContext();

		tool.Execute("""{"status": "success", "reason": "All tasks completed"}""", context);

		context.HasStatusOverride.Should().BeTrue();
		context.StatusOverride.Should().Be(ExecutionStatus.Succeeded);
		context.StatusReason.Should().Be("All tasks completed");
	}

	[Fact]
	public void Execute_StatusSuccess_ReturnsConfirmationMessage()
	{
		var tool = new SetStatusTool();
		var context = new EngineToolContext();

		var result = tool.Execute("""{"status": "success", "reason": "Done"}""", context);

		result.Should().Contain("success");
	}

	[Fact]
	public void Execute_StatusSuccessWithoutReason_UsesDefaultReason()
	{
		var tool = new SetStatusTool();
		var context = new EngineToolContext();

		tool.Execute("""{"status": "success"}""", context);

		context.HasStatusOverride.Should().BeTrue();
		context.StatusOverride.Should().Be(ExecutionStatus.Succeeded);
		context.StatusReason.Should().Be("Step marked as succeeded by LLM");
	}

	[Fact]
	public void Execute_CaseInsensitiveSuccess_SetsSucceeded()
	{
		var tool = new SetStatusTool();
		var context = new EngineToolContext();

		tool.Execute("""{"status": "SUCCESS", "reason": "test"}""", context);

		context.StatusOverride.Should().Be(ExecutionStatus.Succeeded);
	}

	[Fact]
	public void Execute_UnknownStatus_DoesNotSetContext()
	{
		var tool = new SetStatusTool();
		var context = new EngineToolContext();

		var result = tool.Execute("""{"status": "pending", "reason": "Not sure"}""", context);

		context.HasStatusOverride.Should().BeFalse();
		result.Should().Contain("Unknown status");
	}

	[Fact]
	public void Execute_InvalidJson_ReturnsErrorMessage()
	{
		var tool = new SetStatusTool();
		var context = new EngineToolContext();

		var result = tool.Execute("not json", context);

		context.HasStatusOverride.Should().BeFalse();
		result.Should().Contain("Invalid arguments");
	}

	[Fact]
	public void Execute_CaseInsensitiveStatus_SetsFailed()
	{
		var tool = new SetStatusTool();
		var context = new EngineToolContext();

		tool.Execute("""{"status": "FAILED", "reason": "test"}""", context);

		context.StatusOverride.Should().Be(ExecutionStatus.Failed);
	}

	[Fact]
	public void Execute_StatusNoAction_SetsContextToNoAction()
	{
		var tool = new SetStatusTool();
		var context = new EngineToolContext();

		tool.Execute("""{"status": "no_action", "reason": "No incidents to process"}""", context);

		context.HasStatusOverride.Should().BeTrue();
		context.StatusOverride.Should().Be(ExecutionStatus.NoAction);
		context.StatusReason.Should().Be("No incidents to process");
	}

	[Fact]
	public void Execute_StatusNoAction_ReturnsConfirmationMessage()
	{
		var tool = new SetStatusTool();
		var context = new EngineToolContext();

		var result = tool.Execute("""{"status": "no_action", "reason": "Nothing to do"}""", context);

		result.Should().Contain("no_action");
	}

	[Fact]
	public void Execute_StatusNoActionWithoutReason_UsesDefaultReason()
	{
		var tool = new SetStatusTool();
		var context = new EngineToolContext();

		tool.Execute("""{"status": "no_action"}""", context);

		context.HasStatusOverride.Should().BeTrue();
		context.StatusOverride.Should().Be(ExecutionStatus.NoAction);
		context.StatusReason.Should().Be("No action needed");
	}

	[Fact]
	public void Execute_CaseInsensitiveNoAction_SetsNoAction()
	{
		var tool = new SetStatusTool();
		var context = new EngineToolContext();

		tool.Execute("""{"status": "NO_ACTION", "reason": "test"}""", context);

		context.StatusOverride.Should().Be(ExecutionStatus.NoAction);
	}

	[Fact]
	public void ParametersSchema_ContainsNoActionInEnum()
	{
		var tool = new SetStatusTool();

		tool.ParametersSchema.Should().Contain("no_action");
	}

	[Fact]
	public void Description_MentionsNoAction()
	{
		var tool = new SetStatusTool();

		tool.Description.Should().Contain("no_action");
	}

	// ── Fix A: orchestra_set_status is terminal-by-default ──────────────────────────
	// Calling the tool with any terminal status must (a) cancel the linked
	// StepCompletionCts so the running agent stops immediately and (b) advertise
	// "terminal" semantics in its description and return text. This prevents the
	// failure mode where the LLM marks the step success/failed, keeps running on
	// the same or a later session, and then overwrites the prior status.

	[Fact]
	public void Description_DocumentsTerminalSemantics()
	{
		var tool = new SetStatusTool();

		// Loose match — exact phrasing may evolve. The key requirements: the model
		// must be told (1) this terminates immediately and (2) only call it once.
		tool.Description.Should().ContainAll("TERMINATES", "once");
	}

	[Fact]
	public void Execute_StatusSuccess_RequestsStepCompletion()
	{
		var tool = new SetStatusTool();
		using var cts = new CancellationTokenSource();
		var context = new EngineToolContext { StepCompletionCts = cts };

		tool.Execute("""{"status": "success", "reason": "Done"}""", context);

		context.StepCompletionRequested.Should().BeTrue("orchestra_set_status('success') must signal step completion so the agent is cancelled before it can make further tool calls");
		cts.IsCancellationRequested.Should().BeTrue("the linked CTS must be cancelled so PromptExecutor's catch picks up the captured StatusOverride");
	}

	[Fact]
	public void Execute_StatusFailed_RequestsStepCompletion()
	{
		var tool = new SetStatusTool();
		using var cts = new CancellationTokenSource();
		var context = new EngineToolContext { StepCompletionCts = cts };

		tool.Execute("""{"status": "failed", "reason": "Cannot proceed"}""", context);

		context.StepCompletionRequested.Should().BeTrue();
		cts.IsCancellationRequested.Should().BeTrue();
	}

	[Fact]
	public void Execute_StatusNoAction_RequestsStepCompletion()
	{
		var tool = new SetStatusTool();
		using var cts = new CancellationTokenSource();
		var context = new EngineToolContext { StepCompletionCts = cts };

		tool.Execute("""{"status": "no_action", "reason": "Nothing to do"}""", context);

		context.StepCompletionRequested.Should().BeTrue();
		cts.IsCancellationRequested.Should().BeTrue();
	}

	[Fact]
	public void Execute_UnknownStatus_DoesNotRequestStepCompletion()
	{
		var tool = new SetStatusTool();
		using var cts = new CancellationTokenSource();
		var context = new EngineToolContext { StepCompletionCts = cts };

		tool.Execute("""{"status": "pending", "reason": "Not sure"}""", context);

		context.StepCompletionRequested.Should().BeFalse("only recognised terminal statuses should terminate the step; unknown values are no-ops");
		cts.IsCancellationRequested.Should().BeFalse();
	}

	[Fact]
	public void Execute_StatusSuccess_ReturnTextSignalsTermination()
	{
		var tool = new SetStatusTool();
		var context = new EngineToolContext();

		var result = tool.Execute("""{"status": "success", "reason": "Done"}""", context);

		// The exact phrasing isn't an API contract; just require that the LLM-visible
		// return text no longer claims work may continue.
		result.Should().NotContain("continue with any remaining work");
		result.Should().Contain("terminated");
	}
}
