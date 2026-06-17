using FluentAssertions;
using Microsoft.Extensions.AI;
using Orchestra.Engine;

namespace Orchestra.OpenCode.Tests;

public class OpenCodeEngineToolFunctionTests
{
	[Fact]
	public async Task Invoke_NoActiveBinding_ReturnsUnavailable()
	{
		var holder = new EngineToolContextHolder();
		var fn = new OpenCodeEngineToolFunction(new SetStatusTool(), holder);

		var result = await fn.InvokeAsync(new AIFunctionArguments(), CancellationToken.None);

		result.Should().BeOfType<string>().Which.Should().Contain("no active orchestration step");
	}

	[Fact]
	public async Task Invoke_ToolNotEnabledForStep_ReturnsNotEnabled()
	{
		var holder = new EngineToolContextHolder();
		// Binding only has set_status; request the complete tool which isn't enabled.
		holder.Set([new SetStatusTool()], new EngineToolContext());
		var fn = new OpenCodeEngineToolFunction(new CompleteTool(), holder);

		var result = await fn.InvokeAsync(new AIFunctionArguments(), CancellationToken.None);

		result.Should().BeOfType<string>().Which.Should().Contain("not enabled");
	}

	[Fact]
	public async Task Invoke_DispatchesToEngineTool_AndMutatesContext()
	{
		var holder = new EngineToolContextHolder();
		var context = new EngineToolContext { StepName = "step1" };
		holder.Set([new SetStatusTool()], context);
		var fn = new OpenCodeEngineToolFunction(new SetStatusTool(), holder);

		var args = new AIFunctionArguments(new Dictionary<string, object?>
		{
			["status"] = "success",
			["reason"] = "all done",
		});

		var result = await fn.InvokeAsync(args, CancellationToken.None);

		result.Should().BeOfType<string>().Which.Should().Contain("success");
		context.HasStatusOverride.Should().BeTrue();
		context.StatusOverride.Should().Be(ExecutionStatus.Succeeded);
		context.StatusReason.Should().Be("all done");
		context.StepCompletionRequested.Should().BeTrue();
	}

	[Fact]
	public void Definition_ExposesEngineToolNameDescriptionAndSchema()
	{
		var tool = new SetStatusTool();
		var fn = new OpenCodeEngineToolFunction(tool, new EngineToolContextHolder());

		fn.Name.Should().Be(tool.Name);
		fn.Description.Should().Be(tool.Description);
		fn.JsonSchema.GetProperty("properties").GetProperty("status").Should().NotBeNull();
	}
}
