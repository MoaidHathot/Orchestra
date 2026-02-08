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
	private readonly ReasoningLevel? _reasoningLevel;
	private readonly SystemPromptMode? _systemPromptMode;

	internal CopilotAgent(
		CopilotClient client,
		string model,
		string? systemPrompt,
		Mcp[] mcps,
		ReasoningLevel? reasoningLevel,
		SystemPromptMode? systemPromptMode)
	{
		_client = client;
		_model = model;
		_systemPrompt = systemPrompt;
		_mcps = mcps;
		_reasoningLevel = reasoningLevel;
		_systemPromptMode = systemPromptMode;
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
			Console.WriteLine($"Creating Copilot session with model '{_model}'...");
			var config = new SessionConfig
			{
				Model = _model,
				Streaming = true,
			};

			if (_reasoningLevel is not null)
			{
				config.ReasoningEffort = _reasoningLevel.Value.ToString().ToLowerInvariant();
			}

			if (_systemPrompt is not null)
			{
				config.SystemMessage = new SystemMessageConfig
				{
					Content = _systemPrompt,
				};

				if(_systemPromptMode is not null)
				{
					config.SystemMessage.Mode = _systemPromptMode.Value == SystemPromptMode.Append
						? SystemMessageMode.Append
						: SystemMessageMode.Replace;
				}
			}

			if (_mcps.Length > 0)
			{
				config.McpServers = BuildMcpServers();
			}

			await using var session = await _client.CreateSessionAsync(config, cancellationToken);

		var done = new TaskCompletionSource();
		string? finalContent = null;
		string? selectedModel = null;
		string? actualModel = null;
		AgentUsage? usage = null;

		session.On(evt =>
		{
			switch (evt)
			{
				case SessionStartEvent start:
					selectedModel = start.Data.SelectedModel;
					if (selectedModel is not null)
					{
						Console.WriteLine($"  Session started — selected model: {selectedModel}");
					}
					writer.TryWrite(new AgentEvent
					{
						Type = AgentEventType.SessionStart,
						Model = selectedModel,
					});
					break;

				case SessionModelChangeEvent modelChange:
					Console.WriteLine($"  Model changed: {modelChange.Data.PreviousModel} -> {modelChange.Data.NewModel}");
					writer.TryWrite(new AgentEvent
					{
						Type = AgentEventType.ModelChange,
						Model = modelChange.Data.NewModel,
						PreviousModel = modelChange.Data.PreviousModel,
					});
					break;

				case AssistantUsageEvent usageEvt:
					actualModel = usageEvt.Data.Model;
					usage = new AgentUsage
					{
						InputTokens = usageEvt.Data.InputTokens,
						OutputTokens = usageEvt.Data.OutputTokens,
						CacheReadTokens = usageEvt.Data.CacheReadTokens,
						CacheWriteTokens = usageEvt.Data.CacheWriteTokens,
						Cost = usageEvt.Data.Cost,
						Duration = usageEvt.Data.Duration,
					};
					Console.WriteLine($"  Usage — model: {actualModel}, in: {usage.InputTokens}, out: {usage.OutputTokens}, cache-read: {usage.CacheReadTokens}, cache-write: {usage.CacheWriteTokens}");
					writer.TryWrite(new AgentEvent
					{
						Type = AgentEventType.Usage,
						Model = actualModel,
						Usage = usage,
					});
					break;

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

		await session.SendAsync(new MessageOptions { Prompt = prompt }, cancellationToken);

		using var registration = cancellationToken.Register(() => done.TrySetCanceled(cancellationToken));
		await done.Task;

		if (actualModel is not null && !string.Equals(actualModel, _model, StringComparison.OrdinalIgnoreCase))
		{
			Console.WriteLine();
			Console.WriteLine($"  ╔══════════════════════════════════════════════════════════════");
			Console.WriteLine($"  ║ MODEL MISMATCH DETECTED");
			Console.WriteLine($"  ╠══════════════════════════════════════════════════════════════");
			Console.WriteLine($"  ║ Configured model : {_model}");
			Console.WriteLine($"  ║ Actual model used: {actualModel}");
			Console.WriteLine($"  ║");
			Console.WriteLine($"  ║ Step configuration:");
			Console.WriteLine($"  ║   System prompt mode: {(_systemPromptMode?.ToString() ?? "(SDK default)")}");
			Console.WriteLine($"  ║   Reasoning level   : {(_reasoningLevel?.ToString() ?? "(none)")}");
			Console.WriteLine($"  ║   System prompt      : {(_systemPrompt is not null ? $"{_systemPrompt[..Math.Min(_systemPrompt.Length, 80)]}..." : "(none)")}");
			Console.WriteLine($"  ║   MCP servers        : {(_mcps.Length > 0 ? string.Join(", ", _mcps.Select(m => m.Name)) : "(none)")}");
			Console.WriteLine($"  ║");

			try
			{
				var models = await _client.ListModelsAsync(cancellationToken);
				Console.WriteLine($"  ║ Available models ({models.Count}):");
				foreach (var m in models.OrderBy(m => m.Id))
				{
					var billing = m.Billing is not null ? $" [{m.Billing.Multiplier}x]" : "";
					var reasoning = m.SupportedReasoningEfforts is { Count: > 0 }
						? $" reasoning:[{string.Join(",", m.SupportedReasoningEfforts)}]"
						: "";
					Console.WriteLine($"  ║   - {m.Id,-40} {m.Name}{billing}{reasoning}");
				}
			}
			catch (Exception ex)
			{
				Console.WriteLine($"  ║ (Could not list available models: {ex.Message})");
			}

			Console.WriteLine($"  ╚══════════════════════════════════════════════════════════════");
			Console.WriteLine();
		}

		return new AgentResult
		{
			Content = finalContent ?? string.Empty,
			SelectedModel = selectedModel,
			ActualModel = actualModel,
			Usage = usage,
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
