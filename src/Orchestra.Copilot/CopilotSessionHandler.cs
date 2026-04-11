using System.Text.Json;
using System.Threading.Channels;
using GitHub.Copilot.SDK;
using Orchestra.Engine;

namespace Orchestra.Copilot;

/// <summary>
/// Handles Copilot SDK session events and translates them to engine-agnostic AgentEvents.
/// Extracted from CopilotAgent to reduce complexity and improve testability.
///
/// Threading assumption: the Copilot SDK invokes <see cref="HandleEvent"/> callbacks
/// sequentially (single-threaded). The internal state (<see cref="_accumulatedContent"/>,
/// <see cref="_toolCallNames"/>, etc.) is NOT thread-safe. If the SDK's threading model
/// changes to allow concurrent callback dispatch, this class must be updated to use
/// ConcurrentDictionary and thread-safe string accumulation.
/// </summary>
internal sealed class CopilotSessionHandler
{
	private readonly ChannelWriter<AgentEvent> _writer;
	private readonly IOrchestrationReporter _reporter;
	private readonly string _requestedModel;
	private readonly TaskCompletionSource _done;
	private readonly Dictionary<string, string> _toolCallNames = [];
	private readonly System.Text.StringBuilder _accumulatedContent = new();

	private string? _finalContent;
	private string? _selectedModel;
	private string? _actualModel;
	private AgentUsage? _usage;

	public CopilotSessionHandler(
		ChannelWriter<AgentEvent> writer,
		IOrchestrationReporter reporter,
		string requestedModel,
		TaskCompletionSource done)
	{
		_writer = writer;
		_reporter = reporter;
		_requestedModel = requestedModel;
		_done = done;
	}

	/// <summary>
	/// The final text content from the session.
	/// Uses the SDK's AssistantMessageEvent content when available and non-empty.
	/// Falls back to accumulated MessageDelta content when the SDK reports empty content
	/// (which can happen in multi-turn conversations with tool calls where the SDK's
	/// AssistantMessageEvent only captures the last turn's direct text output, potentially
	/// missing content emitted after tool results are processed).
	/// </summary>
	public string? FinalContent =>
		!string.IsNullOrEmpty(_finalContent)
			? _finalContent
			: _accumulatedContent.Length > 0
				? _accumulatedContent.ToString()
				: _finalContent;
	public string? SelectedModel => _selectedModel;
	public string? ActualModel => _actualModel;
	public AgentUsage? Usage => _usage;

	/// <summary>
	/// Handles a session event from the Copilot SDK.
	/// </summary>
	public void HandleEvent(SessionEvent evt)
	{
		switch (evt)
		{
			case SessionStartEvent start:
				HandleSessionStart(start);
				break;

			case SessionModelChangeEvent modelChange:
				HandleModelChange(modelChange);
				break;

			case AssistantUsageEvent usageEvt:
				HandleUsage(usageEvt);
				break;

			case AssistantMessageDeltaEvent delta:
				HandleMessageDelta(delta);
				break;

			case AssistantReasoningDeltaEvent reasoningDelta:
				HandleReasoningDelta(reasoningDelta);
				break;

			case AssistantMessageEvent msg:
				HandleMessage(msg);
				break;

			case AssistantReasoningEvent reasoning:
				HandleReasoning(reasoning);
				break;

			case ToolExecutionStartEvent toolStart:
				HandleToolExecutionStart(toolStart);
				break;

			case ToolExecutionCompleteEvent toolComplete:
				HandleToolExecutionComplete(toolComplete);
				break;

			case SubagentSelectedEvent subagentSelected:
				HandleSubagentSelected(subagentSelected);
				break;

			case SubagentStartedEvent subagentStarted:
				HandleSubagentStarted(subagentStarted);
				break;

			case SubagentCompletedEvent subagentCompleted:
				HandleSubagentCompleted(subagentCompleted);
				break;

			case SubagentFailedEvent subagentFailed:
				HandleSubagentFailed(subagentFailed);
				break;

			case SubagentDeselectedEvent:
				HandleSubagentDeselected();
				break;

			case SessionWarningEvent warning:
				HandleWarning(warning);
				break;

		case SessionInfoEvent info:
			HandleInfo(info);
			break;

		case SessionMcpServersLoadedEvent mcpLoaded:
			HandleMcpServersLoaded(mcpLoaded);
			break;

		case SessionMcpServerStatusChangedEvent mcpStatusChanged:
			HandleMcpServerStatusChanged(mcpStatusChanged);
			break;

		case SessionErrorEvent err:
			HandleError(err);
			break;

			case SessionIdleEvent:
				HandleIdle();
				break;
		}
	}

	private void HandleSessionStart(SessionStartEvent start)
	{
		_selectedModel = start.Data.SelectedModel;
		_reporter.ReportSessionStarted(_requestedModel, _selectedModel);
		_writer.TryWrite(new AgentEvent
		{
			Type = AgentEventType.SessionStart,
			Model = _selectedModel,
		});
	}

	private void HandleModelChange(SessionModelChangeEvent modelChange)
	{
		_reporter.ReportModelChange(modelChange.Data.PreviousModel, modelChange.Data.NewModel);
		_writer.TryWrite(new AgentEvent
		{
			Type = AgentEventType.ModelChange,
			Model = modelChange.Data.NewModel,
			PreviousModel = modelChange.Data.PreviousModel,
		});
	}

	private void HandleUsage(AssistantUsageEvent usageEvt)
	{
		_actualModel = usageEvt.Data.Model;
		_usage = new AgentUsage
		{
			InputTokens = usageEvt.Data.InputTokens,
			OutputTokens = usageEvt.Data.OutputTokens,
			CacheReadTokens = usageEvt.Data.CacheReadTokens,
			CacheWriteTokens = usageEvt.Data.CacheWriteTokens,
			Cost = usageEvt.Data.Cost,
			Duration = usageEvt.Data.Duration,
		};
		_writer.TryWrite(new AgentEvent
		{
			Type = AgentEventType.Usage,
			Model = _actualModel,
			Usage = _usage,
		});
	}

	private void HandleMessageDelta(AssistantMessageDeltaEvent delta)
	{
		_accumulatedContent.Append(delta.Data.DeltaContent);
		_writer.TryWrite(new AgentEvent
		{
			Type = AgentEventType.MessageDelta,
			Content = delta.Data.DeltaContent,
		});
	}

	private void HandleReasoningDelta(AssistantReasoningDeltaEvent reasoningDelta)
	{
		_writer.TryWrite(new AgentEvent
		{
			Type = AgentEventType.ReasoningDelta,
			Content = reasoningDelta.Data.DeltaContent,
		});
	}

	private void HandleMessage(AssistantMessageEvent msg)
	{
		_finalContent = msg.Data.Content;
		_writer.TryWrite(new AgentEvent
		{
			Type = AgentEventType.Message,
			Content = msg.Data.Content,
		});
	}

	private void HandleReasoning(AssistantReasoningEvent reasoning)
	{
		_writer.TryWrite(new AgentEvent
		{
			Type = AgentEventType.Reasoning,
			Content = reasoning.Data.Content,
		});
	}

	private void HandleToolExecutionStart(ToolExecutionStartEvent toolStart)
	{
		var toolName = toolStart.Data.McpToolName ?? toolStart.Data.ToolName;
		if (toolStart.Data.ToolCallId is not null)
			_toolCallNames[toolStart.Data.ToolCallId] = toolName;

		string? serializedArgs = null;
		if (toolStart.Data.Arguments is not null)
		{
			try { serializedArgs = JsonSerializer.Serialize(toolStart.Data.Arguments); }
			catch { /* ignore serialization failures - arguments are optional for trace */ }
		}

		_writer.TryWrite(new AgentEvent
		{
			Type = AgentEventType.ToolExecutionStart,
			ToolCallId = toolStart.Data.ToolCallId,
			ToolName = toolName,
			ToolArguments = serializedArgs,
			McpServerName = toolStart.Data.McpServerName,
		});
	}

	private void HandleToolExecutionComplete(ToolExecutionCompleteEvent toolComplete)
	{
		// Correlate tool name from start event via ToolCallId
		string? toolName = null;
		if (toolComplete.Data.ToolCallId is not null)
		{
			_toolCallNames.Remove(toolComplete.Data.ToolCallId, out toolName);
		}

		_writer.TryWrite(new AgentEvent
		{
			Type = AgentEventType.ToolExecutionComplete,
			ToolCallId = toolComplete.Data.ToolCallId,
			ToolName = toolName,
			ToolSuccess = toolComplete.Data.Success,
			ToolResult = toolComplete.Data.Result?.Content ?? toolComplete.Data.Result?.DetailedContent,
			ToolError = toolComplete.Data.Error?.Message,
		});
	}

	private void HandleSubagentSelected(SubagentSelectedEvent subagentSelected)
	{
		_writer.TryWrite(new AgentEvent
		{
			Type = AgentEventType.SubagentSelected,
			SubagentName = subagentSelected.Data.AgentName,
			SubagentDisplayName = subagentSelected.Data.AgentDisplayName,
			SubagentTools = subagentSelected.Data.Tools,
		});
	}

	private void HandleSubagentStarted(SubagentStartedEvent subagentStarted)
	{
		_writer.TryWrite(new AgentEvent
		{
			Type = AgentEventType.SubagentStarted,
			ToolCallId = subagentStarted.Data.ToolCallId,
			SubagentName = subagentStarted.Data.AgentName,
			SubagentDisplayName = subagentStarted.Data.AgentDisplayName,
			SubagentDescription = subagentStarted.Data.AgentDescription,
		});
	}

	private void HandleSubagentCompleted(SubagentCompletedEvent subagentCompleted)
	{
		_writer.TryWrite(new AgentEvent
		{
			Type = AgentEventType.SubagentCompleted,
			ToolCallId = subagentCompleted.Data.ToolCallId,
			SubagentName = subagentCompleted.Data.AgentName,
			SubagentDisplayName = subagentCompleted.Data.AgentDisplayName,
		});
	}

	private void HandleSubagentFailed(SubagentFailedEvent subagentFailed)
	{
		_writer.TryWrite(new AgentEvent
		{
			Type = AgentEventType.SubagentFailed,
			ToolCallId = subagentFailed.Data.ToolCallId,
			SubagentName = subagentFailed.Data.AgentName,
			SubagentDisplayName = subagentFailed.Data.AgentDisplayName,
			ErrorMessage = subagentFailed.Data.Error,
		});
	}

	private void HandleSubagentDeselected()
	{
		_writer.TryWrite(new AgentEvent
		{
			Type = AgentEventType.SubagentDeselected,
		});
	}

	private void HandleWarning(SessionWarningEvent warning)
	{
		_reporter.ReportSessionWarning(warning.Data.WarningType, warning.Data.Message);
		_writer.TryWrite(new AgentEvent
		{
			Type = AgentEventType.Warning,
			ErrorMessage = warning.Data.Message,
			DiagnosticType = warning.Data.WarningType,
		});
	}

	private void HandleInfo(SessionInfoEvent info)
	{
		_reporter.ReportSessionInfo(info.Data.InfoType, info.Data.Message);
		_writer.TryWrite(new AgentEvent
		{
			Type = AgentEventType.Info,
			Content = info.Data.Message,
			DiagnosticType = info.Data.InfoType,
		});
	}

	private void HandleMcpServersLoaded(SessionMcpServersLoadedEvent mcpLoaded)
	{
		var statuses = mcpLoaded.Data.Servers.Select(s => new McpServerStatusInfo(
			Name: s.Name,
			Status: s.Status.ToString(),
			Source: s.Source,
			Error: s.Error
		)).ToList();

		_reporter.ReportMcpServersLoaded(statuses);
		_writer.TryWrite(new AgentEvent
		{
			Type = AgentEventType.McpServersLoaded,
			McpServerStatuses = statuses,
		});
	}

	private void HandleMcpServerStatusChanged(SessionMcpServerStatusChangedEvent mcpStatusChanged)
	{
		var status = mcpStatusChanged.Data.Status.ToString();
		_reporter.ReportMcpServerStatusChanged(mcpStatusChanged.Data.ServerName, status);
		_writer.TryWrite(new AgentEvent
		{
			Type = AgentEventType.McpServerStatusChanged,
			McpServerName = mcpStatusChanged.Data.ServerName,
			McpServerStatus = status,
		});
	}

	private void HandleError(SessionErrorEvent err)
	{
		_writer.TryWrite(new AgentEvent
		{
			Type = AgentEventType.Error,
			ErrorMessage = err.Data.Message,
		});
		// Complete the TCS so RunSessionAsync does not hang indefinitely
		// waiting for SessionIdleEvent that may never arrive after a fatal error.
		_done.TrySetResult();
	}

	private void HandleIdle()
	{
		_writer.TryWrite(new AgentEvent
		{
			Type = AgentEventType.SessionIdle,
		});
		_done.TrySetResult();
	}
}
