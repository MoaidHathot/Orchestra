using System.Threading.Channels;
using NSubstitute;

namespace Orchestra.Engine.Tests.TestHelpers;

/// <summary>
/// A test implementation of AgentBuilder that creates mock agents.
/// </summary>
public class MockAgentBuilder : AgentBuilder
{
	private Func<string, CancellationToken, AgentTask>? _sendAsyncHandler;

	// Captured from the last BuildAgentAsync(config) call for test assertions
	private AgentBuildConfig? _capturedConfig;

	/// <summary>
	/// Gets the full config that was passed to the last BuildAgentAsync(config) call.
	/// </summary>
	public AgentBuildConfig? CapturedConfig => _capturedConfig;

	/// <summary>
	/// Gets the SystemPromptMode that was configured.
	/// Returns config-based value if available, otherwise falls back to fluent API state.
	/// </summary>
	public SystemPromptMode? CapturedSystemPromptMode => _capturedConfig?.SystemPromptMode ?? SystemPromptMode;

	/// <summary>
	/// Gets the engine tools that were configured.
	/// Returns config-based tools if available, otherwise falls back to fluent API state.
	/// </summary>
	public IReadOnlyCollection<IEngineTool> CapturedEngineTools => _capturedConfig?.EngineTools ?? EngineTools;

	/// <summary>
	/// Gets the engine tool context that was configured.
	/// Returns config-based context if available, otherwise falls back to fluent API state.
	/// </summary>
	public EngineToolContext? CapturedEngineToolContext => _capturedConfig?.EngineToolCtx ?? EngineToolCtx;

	/// <summary>
	/// Gets the MCPs that were configured.
	/// Returns config-based MCPs if available, otherwise falls back to fluent API state.
	/// </summary>
	public Mcp[] CapturedMcps => _capturedConfig?.Mcps ?? Mcps;

	/// <summary>
	/// Configures the mock agent to return specific events and result.
	/// </summary>
	public MockAgentBuilder WithResponse(string content, AgentEvent[]? events = null, AgentUsage? usage = null, string? actualModel = null)
	{
		_sendAsyncHandler = (prompt, ct) =>
		{
			var channel = Channel.CreateUnbounded<AgentEvent>();
			var resultTask = Task.Run(async () =>
			{
				// Write events if provided
				if (events is not null)
				{
					foreach (var evt in events)
					{
						await channel.Writer.WriteAsync(evt, ct);
					}
				}
				else
				{
					// Default: write content as deltas
					await channel.Writer.WriteAsync(new AgentEvent
					{
						Type = AgentEventType.MessageDelta,
						Content = content
					}, ct);
				}

				channel.Writer.Complete();

				return new AgentResult
				{
					Content = content,
					ActualModel = actualModel ?? Model,
					Usage = usage
				};
			}, ct);

			return new AgentTask(channel.Reader, resultTask);
		};

		return this;
	}

	/// <summary>
	/// Configures the mock agent to simulate the LLM calling an engine tool.
	/// The engine tool will be actually executed against the EngineToolContext,
	/// producing real side effects that the PromptExecutor can inspect.
	/// </summary>
	public MockAgentBuilder WithEngineToolCall(string toolName, string arguments, string content)
	{
		_sendAsyncHandler = (prompt, ct) =>
		{
			var channel = Channel.CreateUnbounded<AgentEvent>();
			var resultTask = Task.Run(async () =>
			{
				// Use config-based engine tools/context if available, otherwise fall back to fluent API
				var engineTools = _capturedConfig?.EngineTools ?? EngineTools;
				var engineToolCtx = _capturedConfig?.EngineToolCtx ?? EngineToolCtx;

				// Execute the engine tool against the real context
				string toolResult = "Tool not found";
				if (engineToolCtx is not null)
				{
					foreach (var tool in engineTools)
					{
						if (string.Equals(tool.Name, toolName, StringComparison.OrdinalIgnoreCase))
						{
							toolResult = tool.Execute(arguments, engineToolCtx);
							break;
						}
					}
				}

				var callId = Guid.NewGuid().ToString();

				// Write tool execution events
				await channel.Writer.WriteAsync(new AgentEvent
				{
					Type = AgentEventType.ToolExecutionStart,
					ToolCallId = callId,
					ToolName = toolName,
					ToolArguments = arguments
				}, ct);

				await channel.Writer.WriteAsync(new AgentEvent
				{
					Type = AgentEventType.ToolExecutionComplete,
					ToolCallId = callId,
					ToolName = toolName,
					ToolSuccess = true,
					ToolResult = toolResult
				}, ct);

				// Write final content
				await channel.Writer.WriteAsync(new AgentEvent
				{
					Type = AgentEventType.MessageDelta,
					Content = content
				}, ct);

				channel.Writer.Complete();

				return new AgentResult
				{
					Content = content,
					ActualModel = Model,
				};
			}, ct);

			return new AgentTask(channel.Reader, resultTask);
		};

		return this;
	}

	/// <summary>
	/// Configures the mock agent to throw an exception.
	/// </summary>
	public MockAgentBuilder WithException(Exception exception)
	{
		_sendAsyncHandler = (prompt, ct) =>
		{
			var channel = Channel.CreateUnbounded<AgentEvent>();
			var resultTask = Task.FromException<AgentResult>(exception);
			channel.Writer.Complete();
			return new AgentTask(channel.Reader, resultTask);
		};

		return this;
	}

	/// <summary>
	/// Configures a custom handler for full control over agent behavior.
	/// </summary>
	public MockAgentBuilder WithHandler(Func<string, CancellationToken, AgentTask> handler)
	{
		_sendAsyncHandler = handler;
		return this;
	}

	public override Task<IAgent> BuildAgentAsync(CancellationToken cancellationToken = default)
	{
		var agent = Substitute.For<IAgent>();
		agent.SendAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
			.Returns(callInfo =>
			{
				var prompt = callInfo.ArgAt<string>(0);
				var ct = callInfo.ArgAt<CancellationToken>(1);
				return _sendAsyncHandler?.Invoke(prompt, ct)
					?? CreateDefaultTask("Default response");
			});

		return Task.FromResult(agent);
	}

	public override Task<IAgent> BuildAgentAsync(AgentBuildConfig config, CancellationToken cancellationToken = default)
	{
		// Capture config for test assertions and engine tool execution
		_capturedConfig = config;

		return BuildAgentAsync(cancellationToken);
	}

	private static AgentTask CreateDefaultTask(string content)
	{
		var channel = Channel.CreateUnbounded<AgentEvent>();
		var resultTask = Task.Run(async () =>
		{
			await channel.Writer.WriteAsync(new AgentEvent
			{
				Type = AgentEventType.MessageDelta,
				Content = content
			});
			channel.Writer.Complete();

			return new AgentResult { Content = content };
		});

		return new AgentTask(channel.Reader, resultTask);
	}
}

/// <summary>
/// Extension methods for creating common mock scenarios.
/// </summary>
public static class MockAgentBuilderExtensions
{
	/// <summary>
	/// Creates a mock builder with a simple text response.
	/// </summary>
	public static MockAgentBuilder CreateWithResponse(string content)
	{
		return new MockAgentBuilder().WithResponse(content);
	}

	/// <summary>
	/// Creates a mock builder with tool execution events.
	/// </summary>
	public static MockAgentBuilder CreateWithToolCalls(string finalContent, params (string toolName, string? arguments, string? result)[] toolCalls)
	{
		var events = new List<AgentEvent>();

		foreach (var (toolName, arguments, result) in toolCalls)
		{
			var callId = Guid.NewGuid().ToString();

			events.Add(new AgentEvent
			{
				Type = AgentEventType.ToolExecutionStart,
				ToolCallId = callId,
				ToolName = toolName,
				ToolArguments = arguments
			});

			events.Add(new AgentEvent
			{
				Type = AgentEventType.ToolExecutionComplete,
				ToolCallId = callId,
				ToolName = toolName,
				ToolSuccess = true,
				ToolResult = result
			});
		}

		events.Add(new AgentEvent
		{
			Type = AgentEventType.MessageDelta,
			Content = finalContent
		});

		return new MockAgentBuilder().WithResponse(finalContent, events.ToArray());
	}
}
