using System.Text.Json;
using System.Threading.Channels;
using GitHub.Copilot.SDK;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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
internal sealed partial class CopilotSessionHandler
{
	private readonly ChannelWriter<AgentEvent> _writer;
	private readonly IOrchestrationReporter _reporter;
	private readonly string _requestedModel;
	private readonly TaskCompletionSource _done;
	private readonly ILogger<CopilotSessionHandler> _logger;
	private readonly Dictionary<string, string> _toolCallNames = [];
	private readonly System.Text.StringBuilder _accumulatedContent = new();

	/// <summary>
	/// Stack of currently-active sub-agent invocations (innermost on top). Pushed on
	/// <c>SubagentStartedEvent</c>, popped on <c>SubagentCompletedEvent</c>/<c>SubagentFailedEvent</c>
	/// matched by <c>ToolCallId</c>. Used to attribute reasoning deltas (which carry no SDK
	/// linkage) and as a fallback for content/tool events when the SDK's
	/// <c>ParentToolCallId</c> is absent. Stored as a <see cref="List{T}"/> so we can also
	/// look up arbitrary frames when the SDK pins a delta to a non-top frame.
	/// </summary>
	private readonly List<SubagentFrame> _subagentStack = [];

	private readonly record struct SubagentFrame(
		string ToolCallId,
		string AgentName,
		string? AgentDisplayName);

	private string? _finalContent;
	private string? _selectedModel;
	private string? _actualModel;
	private AgentUsage? _usage;

	public CopilotSessionHandler(
		ChannelWriter<AgentEvent> writer,
		IOrchestrationReporter reporter,
		string requestedModel,
		TaskCompletionSource done,
		ILogger<CopilotSessionHandler>? logger = null)
	{
		_writer = writer;
		_reporter = reporter;
		_requestedModel = requestedModel;
		_done = done;
		_logger = logger ?? NullLogger<CopilotSessionHandler>.Instance;
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

		case SessionCompactionStartEvent:
			HandleCompactionStart();
			break;

		case SessionCompactionCompleteEvent compactionComplete:
			HandleCompactionComplete(compactionComplete);
			break;

		// ── Hook lifecycle events ──
		case HookStartEvent hookStart:
			HandleHookStart(hookStart);
			break;

		case HookEndEvent hookEnd:
			HandleHookEnd(hookEnd);
			break;

		// ── Turn tracking events ──
		case AssistantTurnStartEvent turnStart:
			HandleTurnStart(turnStart);
			break;

		case AssistantTurnEndEvent turnEnd:
			HandleTurnEnd(turnEnd);
			break;

		// ── External tool events (host-side tool execution) ──
		case ExternalToolRequestedEvent externalToolRequested:
			HandleExternalToolRequested(externalToolRequested);
			break;

		// ── Session usage info ──
		case SessionUsageInfoEvent usageInfo:
			HandleSessionUsageInfo(usageInfo);
			break;

		// ── Informational events (silently consumed — no engine-level processing needed) ──
		case PendingMessagesModifiedEvent:
		case SessionCustomAgentsUpdatedEvent:
		case SessionToolsUpdatedEvent:
		case UserMessageEvent:
		case AssistantStreamingDeltaEvent:
		case ExternalToolCompletedEvent:     // UI dismissal signal for external tools
		case AssistantIntentEvent:
		case CapabilitiesChangedEvent:
		case CommandCompletedEvent:
		case CommandExecuteEvent:
		case CommandQueuedEvent:
		case CommandsChangedEvent:
		case ElicitationCompletedEvent:
		case ElicitationRequestedEvent:
		case ExitPlanModeCompletedEvent:
		case ExitPlanModeRequestedEvent:
		case McpOauthCompletedEvent:
		case McpOauthRequiredEvent:
		case PermissionCompletedEvent:
		case PermissionRequestedEvent:
		case SamplingCompletedEvent:
		case SamplingRequestedEvent:
		case SessionBackgroundTasksChangedEvent:
		case SessionContextChangedEvent:
		case SessionExtensionsLoadedEvent:
		case SessionHandoffEvent:
		case SessionModeChangedEvent:
		case SessionPlanChangedEvent:
		case SessionRemoteSteerableChangedEvent:
		case SessionResumeEvent:
		case SessionSkillsLoadedEvent:
		case SessionSnapshotRewindEvent:
		case SessionTitleChangedEvent:
		case SessionTruncationEvent:
		case SessionWorkspaceFileChangedEvent:
		case SkillInvokedEvent:
		case SystemMessageEvent:
		case SystemNotificationEvent:
		case ToolExecutionPartialResultEvent:
		case ToolExecutionProgressEvent:
		case ToolUserRequestedEvent:
		case UserInputCompletedEvent:
		case UserInputRequestedEvent:
		case AbortEvent:
			break;

		case SessionErrorEvent err:
			HandleError(err);
			break;

		case SessionShutdownEvent shutdown:
			HandleShutdown(shutdown);
			break;

		case SessionTaskCompleteEvent taskComplete:
			HandleTaskComplete(taskComplete);
			break;

			case SessionIdleEvent:
				HandleIdle();
				break;

			default:
				HandleUnknownEvent(evt);
				break;
		}
	}

	private void HandleSessionStart(SessionStartEvent start)
	{
		_selectedModel = start.Data.SelectedModel;
		_reporter.ReportSessionStarted(_requestedModel, _selectedModel);
		EmitEvent(new AgentEvent
		{
			Type = AgentEventType.SessionStart,
			Model = _selectedModel,
		});
	}

	// ── Actor attribution helpers ──

	/// <summary>
	/// The actor for events that carry no SDK <c>ParentToolCallId</c>: the top of the
	/// sub-agent stack if any, otherwise the main agent.
	/// </summary>
	private ActorContext CurrentActor()
	{
		if (_subagentStack.Count == 0)
			return ActorContext.Main;

		var top = _subagentStack[^1];
		return new ActorContext(
			AgentName: top.AgentName,
			AgentDisplayName: top.AgentDisplayName,
			ToolCallId: top.ToolCallId,
			Depth: _subagentStack.Count);
	}

	/// <summary>
	/// Resolves the actor for an event that may carry an SDK-supplied <paramref name="parentToolCallId"/>.
	/// When the SDK pins the event to a specific sub-agent frame we honor it (most precise).
	/// When the pinned frame is not on the stack we log a warning and fall back to the current top.
	/// </summary>
	private ActorContext ResolveActor(string? parentToolCallId)
	{
		if (parentToolCallId is null)
			return CurrentActor();

		// Find the matching frame (search top-down — innermost match wins).
		for (var i = _subagentStack.Count - 1; i >= 0; i--)
		{
			var frame = _subagentStack[i];
			if (frame.ToolCallId == parentToolCallId)
			{
				return new ActorContext(
					AgentName: frame.AgentName,
					AgentDisplayName: frame.AgentDisplayName,
					ToolCallId: frame.ToolCallId,
					Depth: i + 1);
			}
		}

		LogParentToolCallIdNotFound(parentToolCallId);
		return CurrentActor();
	}

	/// <summary>
	/// Stamps <paramref name="evt"/> with the supplied actor context and writes it to the channel.
	/// Centralised so every emission goes through the same attribution path.
	/// </summary>
	private void EmitEvent(AgentEvent evt, ActorContext? actor = null)
	{
		var ctx = actor ?? CurrentActor();
		var stamped = new AgentEvent
		{
			Type = evt.Type,
			Content = evt.Content,
			ErrorMessage = evt.ErrorMessage,
			Model = evt.Model,
			PreviousModel = evt.PreviousModel,
			Usage = evt.Usage,
			ToolCallId = evt.ToolCallId,
			ToolName = evt.ToolName,
			ToolArguments = evt.ToolArguments,
			McpServerName = evt.McpServerName,
			ToolSuccess = evt.ToolSuccess,
			ToolResult = evt.ToolResult,
			ToolError = evt.ToolError,
			DiagnosticType = evt.DiagnosticType,
			McpServerStatuses = evt.McpServerStatuses,
			McpServerStatus = evt.McpServerStatus,
			SubagentName = evt.SubagentName,
			SubagentDisplayName = evt.SubagentDisplayName,
			SubagentDescription = evt.SubagentDescription,
			SubagentTools = evt.SubagentTools,
			CompactionTokensBefore = evt.CompactionTokensBefore,
			CompactionTokensAfter = evt.CompactionTokensAfter,
			HookInvocationId = evt.HookInvocationId,
			HookType = evt.HookType,
			HookSuccess = evt.HookSuccess,
			TurnId = evt.TurnId,
			TokenLimit = evt.TokenLimit,
			CurrentTokens = evt.CurrentTokens,
			ActorAgentName = ctx.AgentName,
			ActorAgentDisplayName = ctx.AgentDisplayName,
			ActorToolCallId = ctx.ToolCallId,
			ActorDepth = ctx.Depth,
		};
		_writer.TryWrite(stamped);
	}

	private void HandleModelChange(SessionModelChangeEvent modelChange)
	{
		_reporter.ReportModelChange(modelChange.Data.PreviousModel, modelChange.Data.NewModel);
		EmitEvent(new AgentEvent
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
		EmitEvent(new AgentEvent
		{
			Type = AgentEventType.Usage,
			Model = _actualModel,
			Usage = _usage,
		});
	}

	private void HandleMessageDelta(AssistantMessageDeltaEvent delta)
	{
		_accumulatedContent.Append(delta.Data.DeltaContent);
		EmitEvent(new AgentEvent
		{
			Type = AgentEventType.MessageDelta,
			Content = delta.Data.DeltaContent,
		}, ResolveActor(delta.Data.ParentToolCallId));
	}

	private void HandleReasoningDelta(AssistantReasoningDeltaEvent reasoningDelta)
	{
		// Reasoning deltas have no SDK linkage to the originating sub-agent;
		// the active sub-agent stack is the only attribution signal available.
		EmitEvent(new AgentEvent
		{
			Type = AgentEventType.ReasoningDelta,
			Content = reasoningDelta.Data.DeltaContent,
		});
	}

	private void HandleMessage(AssistantMessageEvent msg)
	{
		_finalContent = msg.Data.Content;
		EmitEvent(new AgentEvent
		{
			Type = AgentEventType.Message,
			Content = msg.Data.Content,
		}, ResolveActor(msg.Data.ParentToolCallId));
	}

	private void HandleReasoning(AssistantReasoningEvent reasoning)
	{
		// Same SDK gap as ReasoningDelta — fall back to the stack.
		EmitEvent(new AgentEvent
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

		EmitEvent(new AgentEvent
		{
			Type = AgentEventType.ToolExecutionStart,
			ToolCallId = toolStart.Data.ToolCallId,
			ToolName = toolName,
			ToolArguments = serializedArgs,
			McpServerName = toolStart.Data.McpServerName,
		}, ResolveActor(toolStart.Data.ParentToolCallId));
	}

	private void HandleToolExecutionComplete(ToolExecutionCompleteEvent toolComplete)
	{
		// Correlate tool name from start event via ToolCallId
		string? toolName = null;
		if (toolComplete.Data.ToolCallId is not null)
		{
			_toolCallNames.Remove(toolComplete.Data.ToolCallId, out toolName);
		}

		EmitEvent(new AgentEvent
		{
			Type = AgentEventType.ToolExecutionComplete,
			ToolCallId = toolComplete.Data.ToolCallId,
			ToolName = toolName,
			ToolSuccess = toolComplete.Data.Success,
			ToolResult = toolComplete.Data.Result?.Content ?? toolComplete.Data.Result?.DetailedContent,
			ToolError = toolComplete.Data.Error?.Message,
		}, ResolveActor(toolComplete.Data.ParentToolCallId));
	}

	private void HandleSubagentSelected(SubagentSelectedEvent subagentSelected)
	{
		// "Selected" is a parent-side decision — attribute to the current scope.
		EmitEvent(new AgentEvent
		{
			Type = AgentEventType.SubagentSelected,
			SubagentName = subagentSelected.Data.AgentName,
			SubagentDisplayName = subagentSelected.Data.AgentDisplayName,
			SubagentTools = subagentSelected.Data.Tools,
		});
	}

	private void HandleSubagentStarted(SubagentStartedEvent subagentStarted)
	{
		// Stamp the SubagentStarted event with the *parent* actor (the one delegating)
		// so the Portal can place the sub-agent card inside the parent's timeline.
		var parentActor = CurrentActor();

		var toolCallId = subagentStarted.Data.ToolCallId;
		if (!string.IsNullOrEmpty(toolCallId))
		{
			LogSubagentStackPush(toolCallId, subagentStarted.Data.AgentName, _subagentStack.Count + 1);
			_subagentStack.Add(new SubagentFrame(
				ToolCallId: toolCallId,
				AgentName: subagentStarted.Data.AgentName,
				AgentDisplayName: subagentStarted.Data.AgentDisplayName));
		}

		EmitEvent(new AgentEvent
		{
			Type = AgentEventType.SubagentStarted,
			ToolCallId = toolCallId,
			SubagentName = subagentStarted.Data.AgentName,
			SubagentDisplayName = subagentStarted.Data.AgentDisplayName,
			SubagentDescription = subagentStarted.Data.AgentDescription,
		}, parentActor);
	}

	private void HandleSubagentCompleted(SubagentCompletedEvent subagentCompleted)
	{
		var toolCallId = subagentCompleted.Data.ToolCallId;
		PopSubagent(toolCallId);

		// After popping, the current actor is the parent — emit accordingly.
		EmitEvent(new AgentEvent
		{
			Type = AgentEventType.SubagentCompleted,
			ToolCallId = toolCallId,
			SubagentName = subagentCompleted.Data.AgentName,
			SubagentDisplayName = subagentCompleted.Data.AgentDisplayName,
		});
	}

	private void HandleSubagentFailed(SubagentFailedEvent subagentFailed)
	{
		var toolCallId = subagentFailed.Data.ToolCallId;
		PopSubagent(toolCallId);

		EmitEvent(new AgentEvent
		{
			Type = AgentEventType.SubagentFailed,
			ToolCallId = toolCallId,
			SubagentName = subagentFailed.Data.AgentName,
			SubagentDisplayName = subagentFailed.Data.AgentDisplayName,
			ErrorMessage = subagentFailed.Data.Error,
		});
	}

	private void HandleSubagentDeselected()
	{
		// Deselected is a parent-side signal that the sub-agent was dismissed without
		// a matching Started/Completed pair (e.g. permission denied). It does NOT pop
		// the stack — only Completed/Failed do.
		EmitEvent(new AgentEvent
		{
			Type = AgentEventType.SubagentDeselected,
		});
	}

	/// <summary>
	/// Pops the matching frame from <see cref="_subagentStack"/>. If the topmost frame
	/// matches we pop it; otherwise we search for the matching frame and pop it (this
	/// can happen if the SDK reorders Completed events) and log a warning. If no match
	/// is found we leave the stack intact and log.
	/// </summary>
	private void PopSubagent(string? toolCallId)
	{
		if (string.IsNullOrEmpty(toolCallId) || _subagentStack.Count == 0)
			return;

		if (_subagentStack[^1].ToolCallId == toolCallId)
		{
			LogSubagentStackPop(toolCallId, _subagentStack[^1].AgentName, _subagentStack.Count - 1);
			_subagentStack.RemoveAt(_subagentStack.Count - 1);
			return;
		}

		// Out-of-order completion — find and remove. This is unusual but defensible.
		for (var i = _subagentStack.Count - 1; i >= 0; i--)
		{
			if (_subagentStack[i].ToolCallId == toolCallId)
			{
				LogSubagentStackOutOfOrderPop(toolCallId, _subagentStack[i].AgentName, i);
				_subagentStack.RemoveAt(i);
				return;
			}
		}

		LogSubagentStackPopMissing(toolCallId);
	}

	private void HandleWarning(SessionWarningEvent warning)
	{
		_reporter.ReportSessionWarning(warning.Data.WarningType, warning.Data.Message);
		EmitEvent(new AgentEvent
		{
			Type = AgentEventType.Warning,
			ErrorMessage = warning.Data.Message,
			DiagnosticType = warning.Data.WarningType,
		});
	}

	private void HandleInfo(SessionInfoEvent info)
	{
		_reporter.ReportSessionInfo(info.Data.InfoType, info.Data.Message);
		EmitEvent(new AgentEvent
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
		EmitEvent(new AgentEvent
		{
			Type = AgentEventType.McpServersLoaded,
			McpServerStatuses = statuses,
		});
	}

	private void HandleMcpServerStatusChanged(SessionMcpServerStatusChangedEvent mcpStatusChanged)
	{
		var status = mcpStatusChanged.Data.Status.ToString();
		_reporter.ReportMcpServerStatusChanged(mcpStatusChanged.Data.ServerName, status);
		EmitEvent(new AgentEvent
		{
			Type = AgentEventType.McpServerStatusChanged,
			McpServerName = mcpStatusChanged.Data.ServerName,
			McpServerStatus = status,
		});
	}

	private void HandleCompactionStart()
	{
		EmitEvent(new AgentEvent
		{
			Type = AgentEventType.CompactionStart,
		});
	}

	private void HandleCompactionComplete(SessionCompactionCompleteEvent compactionComplete)
	{
		EmitEvent(new AgentEvent
		{
			Type = AgentEventType.CompactionComplete,
			CompactionTokensBefore = (int?)compactionComplete.Data.PreCompactionTokens,
			CompactionTokensAfter = (int?)compactionComplete.Data.PostCompactionTokens,
		});
	}

	private void HandleHookStart(HookStartEvent hookStart)
	{
		EmitEvent(new AgentEvent
		{
			Type = AgentEventType.HookStart,
			HookInvocationId = hookStart.Data.HookInvocationId,
			HookType = hookStart.Data.HookType,
		});
	}

	private void HandleHookEnd(HookEndEvent hookEnd)
	{
		EmitEvent(new AgentEvent
		{
			Type = AgentEventType.HookEnd,
			HookInvocationId = hookEnd.Data.HookInvocationId,
			HookType = hookEnd.Data.HookType,
			HookSuccess = hookEnd.Data.Success,
			ErrorMessage = hookEnd.Data.Error?.ToString(),
		});
	}

	private void HandleTurnStart(AssistantTurnStartEvent turnStart)
	{
		EmitEvent(new AgentEvent
		{
			Type = AgentEventType.TurnStart,
			TurnId = turnStart.Data.TurnId,
		});
	}

	private void HandleSessionUsageInfo(SessionUsageInfoEvent usageInfo)
	{
		EmitEvent(new AgentEvent
		{
			Type = AgentEventType.SessionUsageInfo,
			TokenLimit = usageInfo.Data.TokenLimit,
			CurrentTokens = usageInfo.Data.CurrentTokens,
		});
	}

	private void HandleTurnEnd(AssistantTurnEndEvent turnEnd)
	{
		EmitEvent(new AgentEvent
		{
			Type = AgentEventType.TurnEnd,
			TurnId = turnEnd.Data.TurnId,
		});
	}

	private void HandleExternalToolRequested(ExternalToolRequestedEvent externalTool)
	{
		var toolName = externalTool.Data.ToolName;
		if (externalTool.Data.ToolCallId is not null)
			_toolCallNames[externalTool.Data.ToolCallId] = toolName;

		string? serializedArgs = null;
		if (externalTool.Data.Arguments is not null)
		{
			try { serializedArgs = JsonSerializer.Serialize(externalTool.Data.Arguments); }
			catch { /* ignore serialization failures - arguments are optional for trace */ }
		}

		EmitEvent(new AgentEvent
		{
			Type = AgentEventType.ToolExecutionStart,
			ToolCallId = externalTool.Data.ToolCallId,
			ToolName = toolName,
			ToolArguments = serializedArgs,
		});
	}

	private void HandleError(SessionErrorEvent err)
	{
		var message = err.Data.Message ?? "(no message)";

		// Loud ERROR log: a fatal session-level error from the CLI MUST be visible.
		LogSessionError(_requestedModel, message);

		EmitEvent(new AgentEvent
		{
			Type = AgentEventType.Error,
			ErrorMessage = message,
		});

		// Fault the TCS so RunSessionAsync throws and the orchestration step fails
		// with a clear error category instead of silently succeeding with empty content.
		_done.TrySetException(new CopilotSessionFailedException(
			CopilotSessionFailureKind.SessionError,
			_requestedModel,
			$"Copilot session failed: {message}"));
	}

	private void HandleShutdown(SessionShutdownEvent shutdown)
	{
		var errorReason = shutdown.Data.ErrorReason;
		var shutdownType = shutdown.Data.ShutdownType.ToString();

		if (!string.IsNullOrEmpty(errorReason))
		{
			// Abnormal shutdown — the CLI is terminating because of an error.
			// This is a fatal failure; the orchestration step MUST fail.
			LogAbnormalShutdown(_requestedModel, shutdownType, errorReason);

			EmitEvent(new AgentEvent
			{
				Type = AgentEventType.Error,
				ErrorMessage = $"Session shutdown abnormally ({shutdownType}): {errorReason}",
			});

			_done.TrySetException(new CopilotSessionFailedException(
				CopilotSessionFailureKind.AbnormalShutdown,
				_requestedModel,
				$"Copilot session shutdown abnormally ({shutdownType}): {errorReason}",
				reason: errorReason));
			return;
		}

		// Clean shutdown — normal end of session.
		EmitEvent(new AgentEvent
		{
			Type = AgentEventType.SessionIdle,
			Content = $"Session shutdown ({shutdownType})",
		});
		_done.TrySetResult();
	}

	private void HandleTaskComplete(SessionTaskCompleteEvent taskComplete)
	{
		EmitEvent(new AgentEvent
		{
			Type = AgentEventType.SessionIdle,
			Content = taskComplete.Data.Summary,
		});
		// Task is done — complete the TCS. SessionIdleEvent may or may not follow.
		_done.TrySetResult();
	}

	private void HandleUnknownEvent(SessionEvent evt)
	{
		// Log unhandled event types so we don't silently drop signals
		// that might indicate session termination or errors.
		var message = $"Unhandled SDK event: {evt.GetType().Name}";
		EmitEvent(new AgentEvent
		{
			Type = AgentEventType.Warning,
			DiagnosticType = "unhandled_sdk_event",
			ErrorMessage = message,
			Content = message,
		});
	}

	private void HandleIdle()
	{
		EmitEvent(new AgentEvent
		{
			Type = AgentEventType.SessionIdle,
		});
		_done.TrySetResult();
	}

	#region Source-Generated Logging

	[LoggerMessage(
		EventId = 1,
		Level = LogLevel.Error,
		Message = "Copilot session failed (model={Model}): {Message}")]
	private partial void LogSessionError(string model, string message);

	[LoggerMessage(
		EventId = 2,
		Level = LogLevel.Error,
		Message = "Copilot session shutdown abnormally (model={Model}, type={ShutdownType}): {Reason}")]
	private partial void LogAbnormalShutdown(string model, string shutdownType, string reason);

	[LoggerMessage(
		EventId = 3,
		Level = LogLevel.Debug,
		Message = "Sub-agent stack push: toolCallId={ToolCallId} agent={AgentName} depth={Depth}")]
	private partial void LogSubagentStackPush(string toolCallId, string agentName, int depth);

	[LoggerMessage(
		EventId = 4,
		Level = LogLevel.Debug,
		Message = "Sub-agent stack pop: toolCallId={ToolCallId} agent={AgentName} remainingDepth={RemainingDepth}")]
	private partial void LogSubagentStackPop(string toolCallId, string agentName, int remainingDepth);

	[LoggerMessage(
		EventId = 5,
		Level = LogLevel.Warning,
		Message = "Sub-agent completion arrived out of order: toolCallId={ToolCallId} agent={AgentName} index={Index}. Stack repaired.")]
	private partial void LogSubagentStackOutOfOrderPop(string toolCallId, string agentName, int index);

	[LoggerMessage(
		EventId = 6,
		Level = LogLevel.Warning,
		Message = "Sub-agent completion for unknown toolCallId={ToolCallId}. Stack left intact (event predates a SubagentStarted, or was already popped).")]
	private partial void LogSubagentStackPopMissing(string toolCallId);

	[LoggerMessage(
		EventId = 7,
		Level = LogLevel.Warning,
		Message = "SDK ParentToolCallId={ParentToolCallId} not found in active sub-agent stack; falling back to current actor.")]
	private partial void LogParentToolCallIdNotFound(string parentToolCallId);

	#endregion
}
