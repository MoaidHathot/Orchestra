using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Orchestra.Engine.Tests.TestHelpers;

namespace Orchestra.Engine.Tests.Executor;

public class OptInToolsTests
{
	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task HostTools_AreOnlyIncludedForOptedInSteps(bool enabled)
	{
		var builder = new MockAgentBuilder().WithResponse("done");
		var tools = EngineToolRegistry.CreateDefault().Register(new OptionalTool());
		var executor = new OrchestrationExecutor(new OrchestrationScheduler(), builder, NullOrchestrationReporter.Instance, NullLoggerFactory.Instance, engineToolRegistry: tools);
		var result = await executor.ExecuteAsync(new Orchestration
		{
			Name = "opt-in", Description = "Host tools", Steps = [new PromptOrchestrationStep
			{
				Name = "step", Type = OrchestrationStepType.Prompt, Model = "test", SystemPrompt = "", UserPrompt = "Work",
				EnableTools = enabled ? ["optional"] : [],
			}],
		});
		result.Status.Should().Be(ExecutionStatus.Succeeded);
		builder.CapturedConfig!.EngineTools.Any(t => t.Name == "orchestra_optional").Should().Be(enabled);
	}
	private sealed class OptionalTool : IOptInEngineTool
	{
		public string Name => "orchestra_optional";
		public string OptInName => "optional";
		public string Description => "Host optional tool";
		public string ParametersSchema => "{\"type\":\"object\",\"properties\":{}}";
		public string Execute(string arguments, EngineToolContext context) => "done";
	}
}
