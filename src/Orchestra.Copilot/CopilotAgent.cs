using System.Text.Json;
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
	private readonly IOrchestrationReporter _reporter;

	internal CopilotAgent(
		CopilotClient client,
		string model,
		string? systemPrompt,
		Mcp[] mcps,
		ReasoningLevel? reasoningLevel,
		SystemPromptMode? systemPromptMode,
		IOrchestrationReporter reporter)
	{
		_client = client;
		_model = model;
		_systemPrompt = systemPrompt;
		_mcps = mcps;
		_reasoningLevel = reasoningLevel;
		_systemPromptMode = systemPromptMode;
		_reporter = reporter;
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
				OnPermissionRequest = PermissionHandler.ApproveAll,
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

			// Debug: Log MCP count before check
		Console.WriteLine($"[MCP-DEBUG] Agent has {_mcps.Length} MCPs configured");
		
		if (_mcps.Length > 0)
			{
				config.McpServers = BuildMcpServers();
				// Debug: Log MCP server configuration
				foreach (var (name, serverConfig) in config.McpServers)
				{
					if (serverConfig is McpLocalServerConfig local)
					{
						Console.WriteLine($"[MCP] Configuring local server '{name}': Command={local.Command}, Args=[{string.Join(", ", local.Args ?? [])}], Cwd={local.Cwd ?? "(null)"}");
					}
					else if (serverConfig is McpRemoteServerConfig remote)
					{
						Console.WriteLine($"[MCP] Configuring remote server '{name}': Url={remote.Url}");
					}
				}
			}
		else
			{
				Console.WriteLine($"[MCP-DEBUG] No MCPs to configure for this agent");
			}

			await using var session = await _client.CreateSessionAsync(config, cancellationToken);

		var done = new TaskCompletionSource();
		string? finalContent = null;
		string? selectedModel = null;
		string? actualModel = null;
		AgentUsage? usage = null;
		var toolCallNames = new Dictionary<string, string>();

		session.On(evt =>
		{
			switch (evt)
			{
				case SessionStartEvent start:
					selectedModel = start.Data.SelectedModel;
					_reporter.ReportSessionStarted(_model, selectedModel);
					writer.TryWrite(new AgentEvent
					{
						Type = AgentEventType.SessionStart,
						Model = selectedModel,
					});
					break;

				case SessionModelChangeEvent modelChange:
					_reporter.ReportModelChange(modelChange.Data.PreviousModel, modelChange.Data.NewModel);
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
					var startToolName = toolStart.Data.McpToolName ?? toolStart.Data.ToolName;
					if (toolStart.Data.ToolCallId is not null)
						toolCallNames[toolStart.Data.ToolCallId] = startToolName;

					string? serializedArgs = null;
					if (toolStart.Data.Arguments is not null)
					{
						try { serializedArgs = JsonSerializer.Serialize(toolStart.Data.Arguments); }
						catch { /* ignore serialization failures */ }
					}

					writer.TryWrite(new AgentEvent
					{
						Type = AgentEventType.ToolExecutionStart,
						ToolCallId = toolStart.Data.ToolCallId,
						ToolName = startToolName,
						ToolArguments = serializedArgs,
						McpServerName = toolStart.Data.McpServerName,
					});
					break;

			case ToolExecutionCompleteEvent toolComplete:
					// Correlate tool name from start event via ToolCallId
					string? completeToolName = null;
					if (toolComplete.Data.ToolCallId is not null)
						toolCallNames.TryGetValue(toolComplete.Data.ToolCallId, out completeToolName);

					writer.TryWrite(new AgentEvent
					{
						Type = AgentEventType.ToolExecutionComplete,
						ToolCallId = toolComplete.Data.ToolCallId,
						ToolName = completeToolName,
						ToolSuccess = toolComplete.Data.Success,
						ToolResult = toolComplete.Data.Result?.Content ?? toolComplete.Data.Result?.DetailedContent,
						ToolError = toolComplete.Data.Error?.Message,
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

		// Build available models list and report mismatch if detected
		IReadOnlyList<AvailableModelInfo>? availableModels = null;

		if (actualModel is not null && !string.Equals(actualModel, _model, StringComparison.OrdinalIgnoreCase))
		{
			try
			{
				var models = await _client.ListModelsAsync(cancellationToken);
				availableModels = models
					.OrderBy(m => m.Id)
					.Select(m => new AvailableModelInfo
					{
						Id = m.Id,
						Name = m.Name,
						BillingMultiplier = m.Billing?.Multiplier,
						ReasoningEfforts = m.SupportedReasoningEfforts is { Count: > 0 }
							? [.. m.SupportedReasoningEfforts]
							: null,
					})
					.ToList();
			}
			catch
			{
				// Unable to list models — continue without them
			}

			_reporter.ReportModelMismatch(new ModelMismatchInfo
			{
				ConfiguredModel = _model,
				ActualModel = actualModel,
				SystemPromptMode = _systemPromptMode?.ToString() ?? "(SDK default)",
				ReasoningLevel = _reasoningLevel?.ToString() ?? "(none)",
				SystemPromptPreview = _systemPrompt is not null
					? $"{_systemPrompt[..Math.Min(_systemPrompt.Length, 80)]}..."
					: "(none)",
				McpServers = _mcps.Length > 0
					? _mcps.Select(m => m.Name).ToArray()
					: null,
				AvailableModels = availableModels,
			});
		}

		return new AgentResult
		{
			Content = finalContent ?? string.Empty,
			SelectedModel = selectedModel,
			ActualModel = actualModel,
			Usage = usage,
			AvailableModels = availableModels,
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
						Cwd = local.WorkingDirectory,
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
