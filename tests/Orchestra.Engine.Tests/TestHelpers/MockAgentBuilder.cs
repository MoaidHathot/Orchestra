using System.Threading.Channels;
using NSubstitute;

namespace Orchestra.Engine.Tests.TestHelpers;

/// <summary>
/// A test implementation of AgentBuilder that creates mock agents.
/// </summary>
public class MockAgentBuilder : AgentBuilder
{
	private Func<string, CancellationToken, AgentTask>? _sendAsyncHandler;

	/// <summary>
	/// Gets the SystemPromptMode that was configured on this builder.
	/// Useful for testing that the correct mode is passed through.
	/// </summary>
	public SystemPromptMode? CapturedSystemPromptMode => SystemPromptMode;

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
