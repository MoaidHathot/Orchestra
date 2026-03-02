using System.Text.Json;
using System.Threading.Channels;
using GitHub.Copilot.SDK;
using Orchestra.Engine;

namespace Orchestra.Copilot;

/// <summary>
/// Handles Copilot SDK session events and translates them to engine-agnostic AgentEvents.
/// Extracted from CopilotAgent to reduce complexity and improve testability.
/// </summary>
internal sealed class CopilotSessionHandler
{
	private readonly ChannelWriter<AgentEvent> _writer;
	private readonly IOrchestrationReporter _reporter;
	private readonly string _requestedModel;
	private readonly TaskCompletionSource _done;
	private readonly Dictionary<string, string> _toolCallNames = [];

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

	public string? FinalContent => _finalContent;
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
			_toolCallNames.TryGetValue(toolComplete.Data.ToolCallId, out toolName);

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

	private void HandleError(SessionErrorEvent err)
	{
		_writer.TryWrite(new AgentEvent
		{
			Type = AgentEventType.Error,
			ErrorMessage = err.Data.Message,
		});
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
