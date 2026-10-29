using FluentAssertions;
using Microsoft.Extensions.AI;
using Orchestra.Engine;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;

namespace Orchestra.OpenCode.Tests;

public class OpenCodeEngineToolFunctionTests
{
	[Fact]
	public async Task BridgeCatalog_AdvertisesOnlyEnabledStepTools()
	{
		await using var bridge = await OpenCodeEngineToolBridge.StartAsync(new(), "127.0.0.1", NullLoggerFactory.Instance, default,
			[new SaveToFileTool(), new ReadFromFileTool()]);
		await using var client = await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions { Endpoint = new Uri(bridge.McpUrl) }));
		var tools = await client.ListToolsAsync();
		tools.Select(t => t.Name).Should().BeEquivalentTo("orchestra_save_file", "orchestra_read_file");
	}

	[Fact]
	public async Task AsyncTool_UsesCancellableNonBlockingDispatch()
	{
		var tool = new WaitingTool();
		var holder = new EngineToolContextHolder();
		holder.Set([tool], new());
		var function = new OpenCodeEngineToolFunction(tool, holder);
		using var cancellation = new CancellationTokenSource();
		var invoke = function.InvokeAsync(new AIFunctionArguments(), cancellation.Token).AsTask();
		await tool.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
		await cancellation.CancelAsync();
		Func<Task> result = async () => await invoke.WaitAsync(TimeSpan.FromSeconds(2));
		await result.Should().ThrowAsync<OperationCanceledException>();
	}

	private sealed class WaitingTool : IAsyncEngineTool
	{
		public string Name => "wait";
		public string Description => "wait";
		public string ParametersSchema => "{\"type\":\"object\"}";
		public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public string Execute(string args, EngineToolContext context) => throw new InvalidOperationException("Synchronous dispatch is not allowed.");
		public async Task<string> ExecuteAsync(string args, EngineToolContext context, CancellationToken token)
		{
			Started.TrySetResult();
			await Task.Delay(Timeout.Infinite, token);
			return "done";
		}
	}

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
