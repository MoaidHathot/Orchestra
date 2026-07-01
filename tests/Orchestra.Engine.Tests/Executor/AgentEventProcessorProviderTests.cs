using FluentAssertions;
using Orchestra.Engine;

namespace Orchestra.Engine.Tests.Executor;

/// <summary>
/// Verifies the <see cref="AgentEventProcessor"/> stamps the configured/actual provider pair
/// onto both the full and partial traces it builds, so the executor's provider labels reach
/// the step record and the UI.
/// </summary>
public class AgentEventProcessorProviderTests
{
	[Fact]
	public void BuildTrace_IncludesConfiguredAndActualProvider()
	{
		var processor = new AgentEventProcessor(NullOrchestrationReporter.Instance, "step")
		{
			ConfiguredProvider = "opencode",
			ActualProvider = "copilot",
		};

		var trace = processor.BuildTrace("sys", "user");

		trace.ConfiguredProvider.Should().Be("opencode");
		trace.ActualProvider.Should().Be("copilot");
	}

	[Fact]
	public void BuildPartialTrace_IncludesConfiguredAndActualProvider()
	{
		var processor = new AgentEventProcessor(NullOrchestrationReporter.Instance, "step")
		{
			ConfiguredProvider = "copilot",
			ActualProvider = "copilot",
		};

		var trace = processor.BuildPartialTrace("sys", "user");

		trace.ConfiguredProvider.Should().Be("copilot");
		trace.ActualProvider.Should().Be("copilot");
	}
}
