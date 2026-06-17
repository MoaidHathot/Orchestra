using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Orchestra.Engine;

namespace Orchestra.OpenCode.Tests;

/// <summary>
/// End-to-end tests against a real <c>opencode serve</c> instance. Skipped automatically when
/// OpenCode is not installed/reachable (see <see cref="OpenCodeAvailableFactAttribute"/>) and
/// tagged <c>Category=E2E</c> so CI can filter them out. Requires OpenCode to be authenticated
/// to the target provider (e.g. its GitHub Copilot connection).
/// </summary>
[Trait("Category", "E2E")]
public class OpenCodeServerE2ETests
{
	private const string Model = "github-copilot/claude-opus-4.8";

	private static OpenCodeAgentPoolOptions Options() => new()
	{
		DefaultMinInstances = 0,
		DefaultMaxInstancesPerRun = 1,
		FallbackProvider = "github-copilot",
		// Disable the engine-tool bridge for the basic streaming test to keep it hermetic.
		EngineToolBridgeEnabled = false,
		ServerUrl = Environment.GetEnvironmentVariable("ORCHESTRA_OPENCODE_URL"),
	};

	[OpenCodeAvailableFact]
	public async Task PromptStep_StreamsContent_AndReturnsResult()
	{
		var builder = new OpenCodeAgentBuilder(NullLoggerFactory.Instance, Options());
		await using var scope = await builder.CreateRunScopeAsync();
		var agent = await builder.BuildAgentAsync(new AgentBuildConfig
		{
			Model = Model,
			SystemPrompt = "You are a terse assistant. Reply with exactly the requested text and nothing else.",
		});

		using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
		var task = agent.SendAsync("Reply with exactly: hello-opencode", cts.Token);

		var sawDelta = false;
		await foreach (var evt in task.WithCancellation(cts.Token))
		{
			if (evt.Type == AgentEventType.MessageDelta && !string.IsNullOrEmpty(evt.Content))
				sawDelta = true;
		}

		var result = await task.GetResultAsync();

		result.Content.Should().NotBeNullOrWhiteSpace();
		result.Content.Should().Contain("hello-opencode");
		sawDelta.Should().BeTrue("the adapter should stream MessageDelta events");
		result.ActualModel.Should().NotBeNullOrWhiteSpace();
	}

	[OpenCodeAvailableFact]
	public async Task EngineToolBridge_SetStatus_RecordsDeclaredStatus()
	{
		var options = Options();
		options.EngineToolBridgeEnabled = true;

		var builder = new OpenCodeAgentBuilder(NullLoggerFactory.Instance, options);
		await using var scope = await builder.CreateRunScopeAsync();

		var engineCtx = new EngineToolContext { StepName = "e2e-step" };

		var agent = await builder.BuildAgentAsync(new AgentBuildConfig
		{
			Model = Model,
			SystemPrompt = "When you are done, call the orchestra_set_status tool with status 'success' and a short reason.",
			EngineTools = [new SetStatusTool(), new CompleteTool(), new SaveToFileTool(), new ReadFromFileTool()],
			EngineToolCtx = engineCtx,
		});

		using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
		var task = agent.SendAsync("Acknowledge and then mark the step successful via the status tool.", cts.Token);
		await foreach (var _ in task.WithCancellation(cts.Token)) { }
		await task.GetResultAsync();

		engineCtx.HasStatusOverride.Should().BeTrue("the model should have called orchestra_set_status via the MCP bridge");
		engineCtx.StatusOverride.Should().Be(ExecutionStatus.Succeeded);
	}
}
