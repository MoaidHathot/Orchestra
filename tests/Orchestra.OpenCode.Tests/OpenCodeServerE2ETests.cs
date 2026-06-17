using FluentAssertions;
using Microsoft.Extensions.Logging;
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

	[OpenCodeAvailableFact]
	public async Task Reasoning_RegistersAgent_AndRunCompletes()
	{
		var recorder = new RecordingLoggerProvider();
		using var loggerFactory = LoggerFactory.Create(b => b.AddProvider(recorder).SetMinimumLevel(LogLevel.Debug));

		var builder = new OpenCodeAgentBuilder(loggerFactory, Options());
		await using var scope = await builder.CreateRunScopeAsync();
		var agent = await builder.BuildAgentAsync(new AgentBuildConfig
		{
			Model = Model,
			SystemPrompt = "You are a terse assistant.",
			ReasoningLevel = ReasoningLevel.High,
		});

		using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
		var task = agent.SendAsync("Reply with exactly: reasoning-ok", cts.Token);
		await foreach (var _ in task.WithCancellation(cts.Token)) { }
		var result = await task.GetResultAsync();

		result.Content.Should().NotBeNullOrWhiteSpace();
		// A successful run on the dedicated, config-spawned server proves the per-step agent
		// (orchestra-primary, carrying reasoningEffort) was registered and routed to — otherwise
		// OpenCode rejects the prompt with "Agent not found".
		recorder.Messages.Should().NotContain(m => m.Contains("connect-only mode runs the step without them"));
	}

	[OpenCodeAvailableFact]
	public async Task ConfigContent_RegistersAgentsAtSpawn()
	{
		// Spawn-only probe (no ServerUrl) validating OPENCODE_CONFIG_CONTENT registers agents.
		if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ORCHESTRA_OPENCODE_URL")))
			return;

		var options = new OpenCodeAgentPoolOptions { DefaultMinInstances = 0, EngineToolBridgeEnabled = false };
		var configContent = """
			{ "agent": {
			  "orchestra-primary": { "mode": "primary", "description": "probe primary", "prompt": "hi", "reasoningEffort": "high" },
			  "orchestra-sub-probe": { "mode": "subagent", "description": "probe sub", "prompt": "you are a sub" }
			} }
			""";

		var plan = OpenCodeServerBootstrap.Resolve(options);
		await using var process = new OpenCodeServerProcess(plan, options, new OpenCodeHttpClientFactory(), NullLogger.Instance, configContent);
		using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(1));
		await process.StartAsync(cts.Token);

		var agents = await process.Client.ListAgentNamesAsync(cts.Token);
		agents.Should().Contain("orchestra-primary");
		agents.Should().Contain("orchestra-sub-probe");
	}

	private sealed class RecordingLoggerProvider : ILoggerProvider
	{
		public System.Collections.Concurrent.ConcurrentBag<string> Messages { get; } = [];
		public ILogger CreateLogger(string categoryName) => new Recorder(Messages);
		public void Dispose() { }

		private sealed class Recorder(System.Collections.Concurrent.ConcurrentBag<string> sink) : ILogger
		{
			public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullLogger.Instance.BeginScope(state);
			public bool IsEnabled(LogLevel logLevel) => true;
			public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
				=> sink.Add(formatter(state, exception));
		}
	}
}
