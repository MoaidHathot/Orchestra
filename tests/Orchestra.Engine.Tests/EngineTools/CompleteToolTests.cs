using FluentAssertions;

namespace Orchestra.Engine.Tests.EngineTools;

public class CompleteToolTests
{
	[Fact]
	public void Name_ReturnsExpectedName()
	{
		var tool = new CompleteTool();

		tool.Name.Should().Be("orchestra_complete");
	}

	[Fact]
	public void Description_IsNotEmpty()
	{
		var tool = new CompleteTool();

		tool.Description.Should().NotBeNullOrWhiteSpace();
	}

	[Fact]
	public void ParametersSchema_IsValidJson()
	{
		var tool = new CompleteTool();

		var act = () => System.Text.Json.JsonDocument.Parse(tool.ParametersSchema);

		act.Should().NotThrow();
	}

	[Fact]
	public void Execute_StatusSuccess_SetsOrchestrationComplete()
	{
		var tool = new CompleteTool();
		var context = new EngineToolContext();

		tool.Execute("""{"status": "success", "reason": "Nothing to process"}""", context);

		context.OrchestrationCompleteRequested.Should().BeTrue();
		context.OrchestrationCompleteStatus.Should().Be(ExecutionStatus.Succeeded);
		context.OrchestrationCompleteReason.Should().Be("Nothing to process");
	}

	[Fact]
	public void Execute_StatusSuccess_AlsoSetsStepStatus()
	{
		var tool = new CompleteTool();
		var context = new EngineToolContext();

		tool.Execute("""{"status": "success", "reason": "All done"}""", context);

		context.HasStatusOverride.Should().BeTrue();
		context.StatusOverride.Should().Be(ExecutionStatus.Succeeded);
	}

	[Fact]
	public void Execute_StatusSuccess_ReturnsConfirmationMessage()
	{
		var tool = new CompleteTool();
		var context = new EngineToolContext();

		var result = tool.Execute("""{"status": "success", "reason": "Done"}""", context);

		result.Should().Contain("success");
		result.Should().Contain("cancelled");
	}

	[Fact]
	public void Execute_StatusFailed_SetsOrchestrationComplete()
	{
		var tool = new CompleteTool();
		var context = new EngineToolContext();

		tool.Execute("""{"status": "failed", "reason": "Critical error"}""", context);

		context.OrchestrationCompleteRequested.Should().BeTrue();
		context.OrchestrationCompleteStatus.Should().Be(ExecutionStatus.Failed);
		context.OrchestrationCompleteReason.Should().Be("Critical error");
	}

	[Fact]
	public void Execute_StatusFailed_AlsoSetsStepStatus()
	{
		var tool = new CompleteTool();
		var context = new EngineToolContext();

		tool.Execute("""{"status": "failed", "reason": "Error detected"}""", context);

		context.HasStatusOverride.Should().BeTrue();
		context.StatusOverride.Should().Be(ExecutionStatus.Failed);
	}

	[Fact]
	public void Execute_StatusFailed_ReturnsConfirmationMessage()
	{
		var tool = new CompleteTool();
		var context = new EngineToolContext();

		var result = tool.Execute("""{"status": "failed", "reason": "Error"}""", context);

		result.Should().Contain("failed");
		result.Should().Contain("cancelled");
	}

	[Fact]
	public void Execute_StatusSuccessWithoutReason_UsesDefaultReason()
	{
		var tool = new CompleteTool();
		var context = new EngineToolContext();

		tool.Execute("""{"status": "success"}""", context);

		context.OrchestrationCompleteRequested.Should().BeTrue();
		context.OrchestrationCompleteReason.Should().Be("Orchestration completed early by LLM");
	}

	[Fact]
	public void Execute_StatusFailedWithoutReason_UsesDefaultReason()
	{
		var tool = new CompleteTool();
		var context = new EngineToolContext();

		tool.Execute("""{"status": "failed"}""", context);

		context.OrchestrationCompleteRequested.Should().BeTrue();
		context.OrchestrationCompleteReason.Should().Be("Orchestration halted by LLM");
	}

	[Fact]
	public void Execute_CaseInsensitiveSuccess_SetsOrchestrationComplete()
	{
		var tool = new CompleteTool();
		var context = new EngineToolContext();

		tool.Execute("""{"status": "SUCCESS", "reason": "test"}""", context);

		context.OrchestrationCompleteRequested.Should().BeTrue();
		context.OrchestrationCompleteStatus.Should().Be(ExecutionStatus.Succeeded);
	}

	[Fact]
	public void Execute_CaseInsensitiveFailed_SetsOrchestrationComplete()
	{
		var tool = new CompleteTool();
		var context = new EngineToolContext();

		tool.Execute("""{"status": "FAILED", "reason": "test"}""", context);

		context.OrchestrationCompleteRequested.Should().BeTrue();
		context.OrchestrationCompleteStatus.Should().Be(ExecutionStatus.Failed);
	}

	[Fact]
	public void Execute_UnknownStatus_DoesNotSetOrchestrationComplete()
	{
		var tool = new CompleteTool();
		var context = new EngineToolContext();

		var result = tool.Execute("""{"status": "no_action", "reason": "test"}""", context);

		context.OrchestrationCompleteRequested.Should().BeFalse();
		result.Should().Contain("Unknown status");
	}

	[Fact]
	public void Execute_InvalidJson_ReturnsErrorMessage()
	{
		var tool = new CompleteTool();
		var context = new EngineToolContext();

		var result = tool.Execute("not json", context);

		context.OrchestrationCompleteRequested.Should().BeFalse();
		result.Should().Contain("Invalid arguments");
	}
}
