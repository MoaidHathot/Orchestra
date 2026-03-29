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
}
