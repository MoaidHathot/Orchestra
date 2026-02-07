using System.Threading.Channels;
using GitHub.Copilot.SDK;
using Orchestra.Engine;

namespace Orchestra.Copilot;

public class CopilotAgent : IAgent
{
	private readonly CopilotClient _client;
	private readonly string _model;
	private readonly string? _systemPrompt;
	private readonly Mcp[] _mcps;

	internal CopilotAgent(CopilotClient client, string model, string? systemPrompt, Mcp[] mcps)
	{
		_client = client;
		_model = model;
		_systemPrompt = systemPrompt;
		_mcps = mcps;
	}

	public AgentTask SendAsync(string prompt, CancellationToken cancellationToken = default)
	{
		var channel = Channel.CreateUnbounded<AgentEvent>();
		var resultTask = RunSessionAsync(prompt, channel.Writer, cancellationToken);
		return new AgentTask(channel.Reader, resultTask);
	}

	private async Task<AgentResult> RunSessionAsync(
		string prompt,
		ChannelWriter<AgentEvent> writer,
		CancellationToken cancellationToken)
	{
		try
		{
			var config = new SessionConfig
			{
				Model = _model,
				Streaming = true,
			};

			if (_systemPrompt is not null)
			{
				config.SystemMessage = new SystemMessageConfig
				{
					Content = _systemPrompt,
				};
			}

			if (_mcps.Length > 0)
			{
				config.McpServers = BuildMcpServers();
			}

			await using var session = await _client.CreateSessionAsync(config);

			var done = new TaskCompletionSource();
			string? finalContent = null;

			session.On(evt =>
			{
				switch (evt)
				{
					case AssistantMessageDeltaEvent delta:
						writer.TryWrite(new AgentEvent
						{
							Type = AgentEventType.MessageDelta,
							Content = delta.Data.DeltaContent,
						});
						break;

					case AssistantReasoningDeltaEvent reasoningDelta:
						writer.TryWrite(new AgentEvent
						{
							Type = AgentEventType.ReasoningDelta,
							Content = reasoningDelta.Data.DeltaContent,
						});
						break;

					case AssistantMessageEvent msg:
						finalContent = msg.Data.Content;
						writer.TryWrite(new AgentEvent
						{
							Type = AgentEventType.Message,
							Content = msg.Data.Content,
						});
						break;

					case AssistantReasoningEvent reasoning:
						writer.TryWrite(new AgentEvent
						{
							Type = AgentEventType.Reasoning,
							Content = reasoning.Data.Content,
						});
						break;

					case ToolExecutionStartEvent toolStart:
						writer.TryWrite(new AgentEvent
						{
							Type = AgentEventType.ToolExecutionStart,
						});
						break;

					case ToolExecutionCompleteEvent toolComplete:
						writer.TryWrite(new AgentEvent
						{
							Type = AgentEventType.ToolExecutionComplete,
						});
						break;

					case SessionErrorEvent err:
						writer.TryWrite(new AgentEvent
						{
							Type = AgentEventType.Error,
							ErrorMessage = err.Data.Message,
						});
						break;

					case SessionIdleEvent:
						writer.TryWrite(new AgentEvent
						{
							Type = AgentEventType.SessionIdle,
						});
						done.TrySetResult();
						break;
				}
			});

			await session.SendAsync(new MessageOptions { Prompt = prompt });

			using var registration = cancellationToken.Register(() => done.TrySetCanceled(cancellationToken));
			await done.Task;

			return new AgentResult
			{
				Content = finalContent ?? string.Empty,
			};
		}
		finally
		{
			writer.TryComplete();
		}
	}

	private Dictionary<string, object> BuildMcpServers()
	{
		var servers = new Dictionary<string, object>();

		foreach (var mcp in _mcps)
		{
			switch (mcp)
			{
				case LocalMcp local:
					servers[mcp.Name] = new McpLocalServerConfig
					{
						Command = local.Command,
						Args = [.. local.Arguments],
					};
					break;

				case RemoteMcp remote:
					servers[mcp.Name] = new McpRemoteServerConfig
					{
						Url = remote.Endpoint,
						Headers = remote.Headers,
					};
					break;
			}
		}

		return servers;
	}
}
