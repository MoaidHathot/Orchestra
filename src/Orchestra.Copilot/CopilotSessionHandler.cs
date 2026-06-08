using System.Text.Json;
using System.Threading.Channels;
using GitHub.Copilot;
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
	/// Currently-active sub-agent invocations in start order (latest active frame last).
	/// This preserves the existing "current actor = most recently started active sub-agent"
	/// fallback for SDK events that carry no <c>ParentToolCallId</c>, while also allowing
	/// exact lifecycle correlation by <c>ToolCallId</c> for concurrent sibling completions.
	/// </summary>
	private readonly List<SubagentFrame> _activeSubagentFrames = [];
	private readonly Dictionary<string, SubagentFrame> _activeSubagentFramesByToolCallId = [];

	private readonly record struct SubagentFrame(
		string ToolCallId,
		string AgentName,
		string? AgentDisplayName);

	private string? _finalContent;
	private string? _selectedModel;
	private string? _actualModel;
	private AgentUsage? _usage;

	// ── Turn-progress tracking (Phase 3.1) ──
	//
	// Tracks the wall-clock start of the current assistant turn plus the content-length
	// snapshot taken at TurnStart, so HandleTurnEnd can log how much the LLM produced
	// during the turn and how long the turn took. Without this, an in-flight Prompt step
	// that streams hundreds of KB of content over an hour leaves no trace until the
	// session finally completes (or, as we recently observed, fails with no final event).
	//
	// Per-turn counters are reset at each TurnStart and reset to 0 if a TurnEnd arrives
	// without a matching TurnStart (defensive: SDK is documented to emit them in pairs).
	private DateTimeOffset? _currentTurnStartedAt;
	private int _currentTurnStartContentLength;
	private int _currentTurnReasoningDeltaCount;
	private int _currentTurnMessageDeltaCount;
	private string? _currentTurnId;

	// ── Session start tracking for usage-info heartbeats (Phase 3.2) ──
	//
	// The CLI emits SessionUsageInfo roughly every ~10 minutes during a long generation.
	// Logging the elapsed-since-session-start alongside the token counters gives a
	// timeline of "model is alive and growing context" that is invaluable when triaging
	// long-running prompt steps that never reach a completion event.
	private DateTimeOffset? _sessionStartedAt;
	private int _sessionUsageInfoCount;

	// ── Resume metadata exposed for CopilotAgent's swap-and-resume path ──
	//
	// When the CLI emits SessionResumeEvent (only on ResumeSessionAsync paths), we capture
	// the SDK's payload so the agent can decide whether to honor the resume or fall back
	// to a cold restart (AlreadyInUse=true means another client still owns the session lock).
	// A TaskCompletionSource lets the agent await the first resume event with a grace window
	// rather than busy-polling. NOT signalled on CreateSessionAsync paths.
	private readonly TaskCompletionSource<SessionResumeData> _resumeEventReceived =
		new(TaskCreationOptions.RunContinuationsAsynchronously);
	private SessionResumeData? _lastResumeData;

	/// <summary>
	/// Resolves with the first <c>SessionResumeEvent</c> payload received from the SDK, or
	/// never completes if the session was created via <c>CreateSessionAsync</c> (which does
	/// not emit a resume event). Use with a timeout / WhenAny to bound the wait.
	/// </summary>
	public Task<SessionResumeData> ResumeEventReceived => _resumeEventReceived.Task;

	/// <summary>
	/// Last <c>SessionResumeData</c> seen on this handler, or null if no resume event has
	/// arrived yet. Snapshot accessor for tests / diagnostics.
	/// </summary>
	public SessionResumeData? LastResumeData => _lastResumeData;

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
	/// SDK 1.0.0 end-of-session billing summary projected from
	/// <c>SessionShutdownEvent</c>. Replaces the per-usage QuotaSnapshots / TotalNanoAiu
	/// that SDK 0.3.0 surfaced on every <c>AssistantUsageEvent</c> — those moved to a
	/// single terminal payload. <c>null</c> until <c>HandleShutdown</c> fires (e.g. when
	/// the session ends via <c>SessionIdleEvent</c> without a shutdown envelope, or when
	/// it faults before the shutdown event is emitted).
	/// </summary>
	public AgentSessionShutdownSummary? ShutdownSummary => _shutdownSummary;
	private AgentSessionShutdownSummary? _shutdownSummary;

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

			case AutoModeSwitchRequestedEvent autoModeReq:
				HandleAutoModeSwitchRequested(autoModeReq);
				break;

			case AutoModeSwitchCompletedEvent autoModeDone:
				HandleAutoModeSwitchCompleted(autoModeDone);
				break;

			case SystemNotificationEvent notification:
				HandleSystemNotification(notification);
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

		// ── Model-call failure (SDK 1.0.0) ──
		// Observational only: the CLI's own retry budget normally recovers; if it
		// can't, a SessionErrorEvent with exhaustedCliRetries=true follows and the
		// existing swap loop kicks in. Emitting an AgentEvent gives the Portal /
		// operator logs an early warning of upstream flakiness.
		case ModelCallFailureEvent modelCallFailure:
			HandleModelCallFailure(modelCallFailure);
			break;

		// ── Informational events (silently consumed — no engine-level processing needed) ──
		//
		// These events are acknowledged but produce no AgentEvent and no audit-log entry.
		// They are listed here (rather than letting them fall through to HandleUnknownEvent)
		// to suppress the "[unhandled_sdk_event]" warning while documenting that we have
		// reviewed each one and decided no engine-level action is needed today.
		//
		// A few entries carry richer payloads that may be worth elevating in the future:
		//
		//   * AssistantMessageStartEvent — start-of-message marker (MessageId, Phase) paired
		//     with the already-handled AssistantMessageDeltaEvent / AssistantMessageEvent.
		//     Nothing actionable; could be wired into per-message lifecycle telemetry.
		//
		//   * HookProgressEvent — interim progress messages from long-running hooks. Adding
		//     these to the audit log would balloon it; silently consumed by default.
		//
		//   * McpAppToolCallCompleteEvent — SDK 1.0.0 emits this alongside the regular
		//     ToolExecutionCompleteEvent for MCP tool calls, with extra structured fields
		//     (ServerName, Arguments, Result, Error, Success, DurationMs, ToolMeta). The
		//     regular event is already wired into HandleToolExecutionComplete, so this is
		//     redundant for today's needs — but it is the cleanest hook point if we ever
		//     add a step-level "fail on MCP tool error" feature (see StepErrorCategory.ToolError,
		//     which is declared but currently unused).
		//
		//   * SessionPermissionsChangedEvent — fires when "always allow" UI grants flip
		//     AllowAllPermissions. Orchestra sets permissions at session creation in a
		//     controlled host, so this should never fire in our setup; if it does, it is
		//     a security-relevant signal worth elevating to a SessionWarning entry.
		//
		// Everything else here is IDE / UI / canvas / scheduling plumbing that does not
		// apply to Orchestra's automated host execution.
		case PendingMessagesModifiedEvent:
		case SessionCustomAgentsUpdatedEvent:
		case SessionToolsUpdatedEvent:
		case UserMessageEvent:
		case AssistantStreamingDeltaEvent:
		case ExternalToolCompletedEvent:     // UI dismissal signal for external tools
		case AssistantIntentEvent:
		case AssistantMessageStartEvent:     // SDK 1.0.0 start-of-message marker (paired with AssistantMessageDeltaEvent)
		case CapabilitiesChangedEvent:
		case CommandCompletedEvent:
		case CommandExecuteEvent:
		case CommandQueuedEvent:
		case CommandsChangedEvent:
		case ElicitationCompletedEvent:
		case ElicitationRequestedEvent:
		case ExitPlanModeCompletedEvent:
		case ExitPlanModeRequestedEvent:
		case HookProgressEvent:              // SDK 1.0.0 hook progress message stream
		case McpAppToolCallCompleteEvent:    // SDK 1.0.0 structured MCP tool-call summary (duplicate of ToolExecutionCompleteEvent)
		case McpOauthCompletedEvent:
		case McpOauthRequiredEvent:
		case PermissionCompletedEvent:
		case PermissionRequestedEvent:
		case SamplingCompletedEvent:
		case SamplingRequestedEvent:
		case SessionAutopilotObjectiveChangedEvent:    // Autopilot mode — Orchestra does not use autopilot
		case SessionBackgroundTasksChangedEvent:
		case SessionCanvasOpenedEvent:                 // IDE canvas UI — N/A for headless host
		case SessionCanvasRegistryChangedEvent:        // IDE canvas UI — N/A for headless host
		case SessionContextChangedEvent:
		case SessionCustomNotificationEvent:           // Extension-defined notifications — none configured today
		case SessionExtensionsAttachmentsPushedEvent:  // Extension attachments — N/A for orchestrated steps
		case SessionExtensionsLoadedEvent:
		case SessionHandoffEvent:
		case SessionModeChangedEvent:
		case SessionPermissionsChangedEvent:           // Permission grant changes — should never fire in Orchestra's controlled host; see comment above
		case SessionPlanChangedEvent:
		case SessionRemoteSteerableChangedEvent:
		case SessionScheduleCancelledEvent:            // SDK scheduling — Orchestra uses its own scheduler
		case SessionScheduleCreatedEvent:              // SDK scheduling — Orchestra uses its own scheduler
		case SessionSkillsLoadedEvent:
		case SessionSnapshotRewindEvent:
		case SessionTitleChangedEvent:
		case SessionTruncationEvent:
		case SessionWorkspaceFileChangedEvent:
		case SkillInvokedEvent:
		case SystemMessageEvent:
		case ToolExecutionPartialResultEvent:
		case ToolExecutionProgressEvent:
		case ToolUserRequestedEvent:
		case UserInputCompletedEvent:
		case UserInputRequestedEvent:
		case AbortEvent:
			break;

		case SessionResumeEvent resumeEvt:
			HandleSessionResume(resumeEvt);
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
		_sessionStartedAt = DateTimeOffset.UtcNow;
		_reporter.ReportSessionStarted(_requestedModel, _selectedModel);
		EmitEvent(new AgentEvent
		{
			Type = AgentEventType.SessionStart,
			Model = _selectedModel,
		});
	}

	// ── Actor attribution helpers ──

	/// <summary>
	/// The actor for events that carry no SDK <c>ParentToolCallId</c>: the most recently
	/// started active sub-agent if any, otherwise the main agent.
	/// </summary>
	private ActorContext CurrentActor()
	{
		if (_activeSubagentFrames.Count == 0)
			return ActorContext.Main;

		return CreateActorContext(_activeSubagentFrames[^1], _activeSubagentFrames.Count);
	}

	/// <summary>
	/// Resolves the actor for an event that may carry an SDK-supplied <paramref name="parentToolCallId"/>.
	/// When the SDK pins the event to a specific active sub-agent frame we honor it (most precise).
	/// When the pinned frame is no longer active we log a warning and fall back to the current actor.
	/// </summary>
	private ActorContext ResolveActor(string? parentToolCallId)
	{
		if (parentToolCallId is null)
			return CurrentActor();

		if (_activeSubagentFramesByToolCallId.TryGetValue(parentToolCallId, out var frame))
		{
			var depth = FindActiveSubagentDepth(parentToolCallId);
			if (depth is not null)
				return CreateActorContext(frame, depth.Value);
		}

		LogParentToolCallIdNotFound(parentToolCallId);
		return CurrentActor();
	}

	// ── Obsolete-API isolation helpers ──
	//
	// SDK 0.3.0 marked the four ParentToolCallId properties below as `[Obsolete]` with the
	// generic message "This member is deprecated and will be removed in a future version."
	// but does NOT yet ship a replacement (no ActorContext / lineage struct, no Subagent*
	// event surfaces a parentToolCallId in lieu of these). Until the SDK provides one, we
	// continue to read the property — it is the only signal that lets us pin a streaming
	// event to a specific sub-agent invocation when the active stack is ambiguous.
	//
	// The reads are isolated in these helpers so a future migration touches one place per
	// data shape rather than every emission site.

#pragma warning disable CS0618 // Type or member is obsolete
	private static string? ReadParentToolCallId(AssistantMessageDeltaData data) => data.ParentToolCallId;
	private static string? ReadParentToolCallId(AssistantMessageData data) => data.ParentToolCallId;
	private static string? ReadParentToolCallId(ToolExecutionStartData data) => data.ParentToolCallId;
	private static string? ReadParentToolCallId(ToolExecutionCompleteData data) => data.ParentToolCallId;
#pragma warning restore CS0618

	private ActorContext CreateActorContext(SubagentFrame frame, int depth) => new(
		AgentName: frame.AgentName,
		AgentDisplayName: frame.AgentDisplayName,
		ToolCallId: frame.ToolCallId,
		Depth: depth);

	private int? FindActiveSubagentDepth(string toolCallId)
	{
		for (var i = _activeSubagentFrames.Count - 1; i >= 0; i--)
		{
			if (_activeSubagentFrames[i].ToolCallId == toolCallId)
				return i + 1;
		}

		return null;
	}

	private void ActivateSubagent(SubagentFrame frame)
	{
		if (_activeSubagentFramesByToolCallId.ContainsKey(frame.ToolCallId))
		{
			LogSubagentFrameReplaced(frame.ToolCallId, frame.AgentName);
			DeactivateSubagent(frame.ToolCallId, warnIfMissing: false);
		}

		_activeSubagentFrames.Add(frame);
		_activeSubagentFramesByToolCallId[frame.ToolCallId] = frame;
		LogSubagentFrameActivated(frame.ToolCallId, frame.AgentName, _activeSubagentFrames.Count);
	}

	private void DeactivateSubagent(string? toolCallId, bool warnIfMissing = true)
	{
		if (string.IsNullOrEmpty(toolCallId))
			return;

		var index = -1;
		for (var i = _activeSubagentFrames.Count - 1; i >= 0; i--)
		{
			if (_activeSubagentFrames[i].ToolCallId == toolCallId)
			{
				index = i;
				break;
			}
		}

		var removedFromMap = _activeSubagentFramesByToolCallId.Remove(toolCallId, out var frame);
		if (index >= 0)
		{
			frame = _activeSubagentFrames[index];
			_activeSubagentFrames.RemoveAt(index);
		}

		if (!removedFromMap && index < 0)
		{
			if (warnIfMissing)
				LogSubagentFrameMissing(toolCallId);
			return;
		}

		LogSubagentFrameRemoved(toolCallId, frame.AgentName, _activeSubagentFrames.Count);
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
			AutoModeRequestId = evt.AutoModeRequestId,
			AutoModeErrorCode = evt.AutoModeErrorCode,
			AutoModeResponse = evt.AutoModeResponse,
			NotificationKind = evt.NotificationKind,
			NotificationMessage = evt.NotificationMessage,
			QuotaSnapshots = evt.QuotaSnapshots,
			SwapAttempt = evt.SwapAttempt,
			SwapBudget = evt.SwapBudget,
			SwapReason = evt.SwapReason,
			SwapMode = evt.SwapMode,
			PriorSessionId = evt.PriorSessionId,
			ResumedEventCount = evt.ResumedEventCount,
			ResumeAlreadyInUse = evt.ResumeAlreadyInUse,
			ModelCallFailureSource = evt.ModelCallFailureSource,
			ModelCallFailureMessage = evt.ModelCallFailureMessage,
			ModelCallFailureModel = evt.ModelCallFailureModel,
			ModelCallFailureStatusCode = evt.ModelCallFailureStatusCode,
			// SDK 1.0.0 richer diagnostic fields — pass through verbatim from the emitter.
			InterTokenLatencyMs = evt.InterTokenLatencyMs,
			InfoTip = evt.InfoTip,
			InfoUrl = evt.InfoUrl,
			WarningUrl = evt.WarningUrl,
			ToolExecutionModel = evt.ToolExecutionModel,
			ToolExecutionTurnId = evt.ToolExecutionTurnId,
			ToolDisplayVerbatim = evt.ToolDisplayVerbatim,
			ToolSandboxed = evt.ToolSandboxed,
			ToolDescription = evt.ToolDescription,
			ResumeSessionWasActive = evt.ResumeSessionWasActive,
			ResumeContinuePendingWork = evt.ResumeContinuePendingWork,
			MessageModel = evt.MessageModel,
			MessageOutputTokens = evt.MessageOutputTokens,
			MessageRequestId = evt.MessageRequestId,
			MessageTurnId = evt.MessageTurnId,
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

		// SDK 1.0.0 changes the wire types under AssistantUsageData:
		//   * QuotaSnapshots and CopilotUsage.TotalNanoAiu are removed (those moved to
		//     SessionShutdownEvent for end-of-session roll-ups instead).
		//   * Duration, TimeToFirstToken (renamed from TtftMs), and InterTokenLatency
		//     are TimeSpan? instead of double?. We project Duration to seconds and
		//     TimeToFirstToken to milliseconds to preserve the existing AgentUsage shape.
		//   * Cost is decorated with the SDK's GHCP001 "evaluation-only" attribute;
		//     suppressed locally because the field is still wire-compatible with 0.3.0
		//     and our consumers already treat it as best-effort.
		#pragma warning disable GHCP001 // AssistantUsageData.Cost is marked evaluation-only by the SDK
		var cost = usageEvt.Data.Cost;
		#pragma warning restore GHCP001
		var durationSeconds = usageEvt.Data.Duration is { } d
			? (double?)d.TotalSeconds
			: null;
		var ttftMs = usageEvt.Data.TimeToFirstToken is { } ttft
			? (double?)ttft.TotalMilliseconds
			: null;
		// SDK 1.0.0 added AssistantUsageData.InterTokenLatency (TimeSpan?) — the
		// average inter-token gap during a streaming response. Project to ms for
		// AgentEvent.InterTokenLatencyMs.
		var interTokenLatencyMs = usageEvt.Data.InterTokenLatency is { } itl
			? (double?)itl.TotalMilliseconds
			: null;

		_usage = new AgentUsage
		{
			InputTokens = usageEvt.Data.InputTokens,
			OutputTokens = usageEvt.Data.OutputTokens,
			CacheReadTokens = usageEvt.Data.CacheReadTokens,
			CacheWriteTokens = usageEvt.Data.CacheWriteTokens,
			Cost = cost,
			Duration = durationSeconds,
			ReasoningTokens = usageEvt.Data.ReasoningTokens,
			TotalNanoAiu = null, // moved to SessionShutdownData.TotalNanoAiu in SDK 1.0.0
			TimeToFirstTokenMs = ttftMs,
			QuotaSnapshots = null, // removed from per-usage events in SDK 1.0.0
		};
		EmitEvent(new AgentEvent
		{
			Type = AgentEventType.Usage,
			Model = _actualModel,
			Usage = _usage,
			InterTokenLatencyMs = interTokenLatencyMs,
		});
	}

	private void HandleMessageDelta(AssistantMessageDeltaEvent delta)
	{
		_accumulatedContent.Append(delta.Data.DeltaContent);
		_currentTurnMessageDeltaCount++;
		EmitEvent(new AgentEvent
		{
			Type = AgentEventType.MessageDelta,
			Content = delta.Data.DeltaContent,
		}, ResolveActor(ReadParentToolCallId(delta.Data)));
	}

	private void HandleReasoningDelta(AssistantReasoningDeltaEvent reasoningDelta)
	{
		_currentTurnReasoningDeltaCount++;
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
			// SDK 1.0.0 richer diagnostic fields: surface the model, output-token
			// count, request id, and turn id so downstream consumers can correlate
			// per-message billing and trace upstream provider issues.
			MessageModel = msg.Data.Model,
			MessageOutputTokens = msg.Data.OutputTokens,
			MessageRequestId = msg.Data.RequestId,
			MessageTurnId = msg.Data.TurnId,
		}, ResolveActor(ReadParentToolCallId(msg.Data)));
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
			// SDK 1.0.0 richer fields: which model issued the tool call, which turn
			// it belongs to, and whether the runtime hints the output should be shown
			// verbatim.
			ToolExecutionModel = toolStart.Data.Model,
			ToolExecutionTurnId = toolStart.Data.TurnId,
			ToolDisplayVerbatim = toolStart.Data.DisplayVerbatim,
		}, ResolveActor(ReadParentToolCallId(toolStart.Data)));
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
			// SDK 1.0.0 richer fields: same model/turn correlation as ToolExecutionStart,
			// plus Sandboxed and the human-readable Description.
			ToolExecutionModel = toolComplete.Data.Model,
			ToolExecutionTurnId = toolComplete.Data.TurnId,
			ToolSandboxed = toolComplete.Data.Sandboxed,
			ToolDescription = toolComplete.Data.ToolDescription?.Description,
		}, ResolveActor(ReadParentToolCallId(toolComplete.Data)));
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
			ActivateSubagent(new SubagentFrame(
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
		DeactivateSubagent(toolCallId);

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
		DeactivateSubagent(toolCallId);

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

	private void HandleWarning(SessionWarningEvent warning)
	{
		_reporter.ReportSessionWarning(warning.Data.WarningType, warning.Data.Message);
		EmitEvent(new AgentEvent
		{
			Type = AgentEventType.Warning,
			ErrorMessage = warning.Data.Message,
			DiagnosticType = warning.Data.WarningType,
			// SDK 1.0.0 added Url so the CLI can include a remediation / docs link.
			WarningUrl = warning.Data.Url,
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
			// SDK 1.0.0 added Tip + Url alongside the existing Message/InfoType so the
			// CLI can attach hyperlinks and one-liner remediation hints.
			InfoTip = info.Data.Tip,
			InfoUrl = info.Data.Url,
		});
	}

	private void HandleMcpServersLoaded(SessionMcpServersLoadedEvent mcpLoaded)
	{
		// SDK 1.0.0 changed McpServersLoadedServer.Source from string to a typed
		// McpServerSource? enum. Project it back to a string for the engine-level
		// shape so downstream consumers (Portal, audit log) don't need a per-version
		// adapter — the well-known values are stable.
		var statuses = mcpLoaded.Data.Servers.Select(s => new McpServerStatusInfo(
			Name: s.Name,
			Status: s.Status.ToString(),
			Source: s.Source?.ToString(),
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
		// SDK 1.0.0 changed HookEndData.Error from string? to a structured HookEndError
		// record carrying Message and Stack. We surface Message (the operator-readable
		// text); Stack is available if a future AgentEvent field wants to capture it.
		EmitEvent(new AgentEvent
		{
			Type = AgentEventType.HookEnd,
			HookInvocationId = hookEnd.Data.HookInvocationId,
			HookType = hookEnd.Data.HookType,
			HookSuccess = hookEnd.Data.Success,
			ErrorMessage = hookEnd.Data.Error?.Message,
		});
	}

	private void HandleTurnStart(AssistantTurnStartEvent turnStart)
	{
		// Snapshot per-turn counters so HandleTurnEnd can report deltas-since-start
		// and elapsed wall time. See _currentTurnStartedAt comment for context.
		_currentTurnStartedAt = DateTimeOffset.UtcNow;
		_currentTurnStartContentLength = _accumulatedContent.Length;
		_currentTurnReasoningDeltaCount = 0;
		_currentTurnMessageDeltaCount = 0;
		_currentTurnId = turnStart.Data.TurnId;

		EmitEvent(new AgentEvent
		{
			Type = AgentEventType.TurnStart,
			TurnId = turnStart.Data.TurnId,
		});
	}

	private void HandleSessionUsageInfo(SessionUsageInfoEvent usageInfo)
	{
		_sessionUsageInfoCount++;
		var elapsedSinceSessionStartMs = _sessionStartedAt is { } sessionStart
			? (long)(DateTimeOffset.UtcNow - sessionStart).TotalMilliseconds
			: -1L;
		var elapsedSinceTurnStartMs = _currentTurnStartedAt is { } turnStart
			? (long)(DateTimeOffset.UtcNow - turnStart).TotalMilliseconds
			: -1L;

		// Heartbeat-style log: the SDK fires SessionUsageInfo roughly every ~10 minutes
		// during a long generation. Capturing tokenLimit/currentTokens here gives a
		// growth-over-time signal that surfaces in the host log even when the step never
		// reaches a completion event. Verbose level keeps it cheap.
		//
		// SDK 1.0.0 changed TokenLimit and CurrentTokens from double to long; we keep the
		// AgentEvent shape (double?) so downstream consumers don't break and just widen
		// the long into a double on the way out.
		LogSessionUsageInfo(
			_requestedModel,
			_sessionUsageInfoCount,
			usageInfo.Data.TokenLimit,
			usageInfo.Data.CurrentTokens,
			elapsedSinceSessionStartMs,
			elapsedSinceTurnStartMs);

		EmitEvent(new AgentEvent
		{
			Type = AgentEventType.SessionUsageInfo,
			TokenLimit = usageInfo.Data.TokenLimit,
			CurrentTokens = usageInfo.Data.CurrentTokens,
		});
	}

	private void HandleAutoModeSwitchRequested(AutoModeSwitchRequestedEvent evt)
	{
		EmitEvent(new AgentEvent
		{
			Type = AgentEventType.AutoModeSwitchRequested,
			AutoModeRequestId = evt.Data.RequestId,
			AutoModeErrorCode = evt.Data.ErrorCode,
		});
	}

	private void HandleAutoModeSwitchCompleted(AutoModeSwitchCompletedEvent evt)
	{
		// SDK 1.0.0 changed AutoModeSwitchCompletedData.Response from string to a
		// strongly-typed AutoModeSwitchResponse struct (values: Yes, YesAlways, No).
		// The struct's .Value property carries the string form we previously consumed.
		EmitEvent(new AgentEvent
		{
			Type = AgentEventType.AutoModeSwitchCompleted,
			AutoModeRequestId = evt.Data.RequestId,
			AutoModeResponse = evt.Data.Response.Value,
		});
	}

	private void HandleSystemNotification(SystemNotificationEvent evt)
	{
		// SDK 0.3.0: SystemNotificationData.Kind is a typed discriminator; .Type carries
		// the kind name (e.g. "agent_completed", "shell_completed", "new_inbox_message").
		// Surfacing both .Type and .Content lets the Portal render rich rows.
		EmitEvent(new AgentEvent
		{
			Type = AgentEventType.SystemNotification,
			NotificationKind = evt.Data.Kind?.Type,
			NotificationMessage = evt.Data.Content,
		});
	}

	private void HandleTurnEnd(AssistantTurnEndEvent turnEnd)
	{
		// Per-turn accumulator log (Phase 3.1): captures elapsed time, content streamed,
		// and per-delta-type counts so a post-mortem can quickly see "this turn took 56
		// minutes and streamed 594KB of content" without re-reading the full event tape.
		var elapsedMs = _currentTurnStartedAt is { } start
			? (long)(DateTimeOffset.UtcNow - start).TotalMilliseconds
			: -1L;
		var contentGrowth = _accumulatedContent.Length - _currentTurnStartContentLength;

		LogTurnEnded(
			_requestedModel,
			turnEnd.Data.TurnId ?? _currentTurnId ?? "(unknown)",
			elapsedMs,
			Math.Max(0, contentGrowth),
			_currentTurnMessageDeltaCount,
			_currentTurnReasoningDeltaCount);

		// Reset per-turn state; counters re-initialize on the next TurnStart anyway,
		// but clearing here keeps stray late events from being attributed to a finished turn.
		_currentTurnStartedAt = null;
		_currentTurnStartContentLength = 0;
		_currentTurnReasoningDeltaCount = 0;
		_currentTurnMessageDeltaCount = 0;
		_currentTurnId = null;

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

	/// <summary>
	/// Handles SDK 1.0.0's <c>ModelCallFailureEvent</c>. This fires when an individual
	/// model API call faults (HTTP error, timeout, rate-limit) WITHOUT ending the
	/// session — the CLI's own retry loop normally recovers. We capture it for
	/// observability so the Portal and operator logs can show upstream flakiness
	/// ahead of an eventual <see cref="SessionErrorEvent"/>.
	/// </summary>
	/// <remarks>
	/// We deliberately do NOT fault the TaskCompletionSource here. If the CLI's retry
	/// budget runs out, it surfaces a <c>SessionErrorEvent</c> with the
	/// "retried N times" / "Failed to get response from the AI model" pattern which
	/// our existing <see cref="LooksLikeCliExhaustedRetries"/> matcher classifies as
	/// swap-eligible. Eagerly faulting on the first ModelCallFailure would pre-empt
	/// the CLI's recovery and consume a swap budget for what is usually a transient
	/// per-call blip.
	/// </remarks>
	private void HandleModelCallFailure(ModelCallFailureEvent failure)
	{
		var source = failure.Data.Source.Value;
		var message = failure.Data.ErrorMessage;
		var model = failure.Data.Model;
		var statusCode = failure.Data.StatusCode;

		LogModelCallFailure(
			source ?? "(unknown)",
			model ?? "(unknown)",
			statusCode,
			message ?? "(no message)");

		EmitEvent(new AgentEvent
		{
			Type = AgentEventType.ModelCallFailure,
			ModelCallFailureSource = source,
			ModelCallFailureMessage = message,
			ModelCallFailureModel = model,
			ModelCallFailureStatusCode = statusCode,
		});
	}

	private void HandleSessionResume(SessionResumeEvent resumeEvt)
	{
		_lastResumeData = resumeEvt.Data;
		// Resolve the awaitable so CopilotAgent's swap-and-resume path can react to
		// AlreadyInUse=true within its grace window without polling.
		_resumeEventReceived.TrySetResult(resumeEvt.Data);

		var alreadyInUse = resumeEvt.Data.AlreadyInUse ?? false;
		LogSessionResumed(
			_requestedModel,
			alreadyInUse,
			resumeEvt.Data.EventCount,
			resumeEvt.Data.SelectedModel ?? "(unchanged)",
			resumeEvt.Data.ResumeTime.ToString("O"));

		EmitEvent(new AgentEvent
		{
			Type = AgentEventType.SessionResumed,
			Model = resumeEvt.Data.SelectedModel,
			ResumedEventCount = (int)Math.Min(int.MaxValue, resumeEvt.Data.EventCount),
			ResumeAlreadyInUse = alreadyInUse,
			// SDK 1.0.0 added SessionWasActive + ContinuePendingWork to the resume
			// envelope. SessionWasActive=true means the prior CLI still had work in
			// flight when we resumed; ContinuePendingWork tells us whether the runtime
			// is going to re-deliver that pending message after the resume.
			ResumeSessionWasActive = resumeEvt.Data.SessionWasActive,
			ResumeContinuePendingWork = resumeEvt.Data.ContinuePendingWork,
		});
	}

	private void HandleError(SessionErrorEvent err)
	{
		var message = err.Data.Message ?? "(no message)";

		// Detect the CLI's "I exhausted my internal retries" pattern. The bundled
		// copilot.exe retries upstream model API calls internally and surfaces a
		// session.error with a message like:
		//   "Failed to get response from the AI model; retried 5 times (total retry wait time: ...)"
		// When that happens the CLI is about to exit. A fresh CLI process re-rolls
		// upstream provider routing / connection pool so swapping the CLI usually
		// clears it. Flag the details record so CopilotAgent's swap loop can react.
		var exhaustedCliRetries = LooksLikeCliExhaustedRetries(message);

		// Detect transient upstream failures that a fresh CLI worker is likely to clear:
		// 5xx broker errors, 403/permission_denied identity-handshake errors, and 429
		// rate limits. The dying CLI's cached auth/connection state is the usual culprit;
		// cold restart re-authenticates from scratch.
		var transientUpstream = LooksLikeTransientUpstreamFailure(message, err.Data.StatusCode);

		// Capture every field the SDK gave us in SessionErrorData. Historically only
		// Message was retained which collapsed the upstream ErrorType / StatusCode /
		// ProviderCallId / Url / Stack into nothing — making real failures (e.g. a
		// 56-minute query that died with "Unknown error") essentially un-triable.
		var details = new AgentSessionErrorDetails
		{
			ErrorType = err.Data.ErrorType,
			StatusCode = err.Data.StatusCode,
			ProviderCallId = err.Data.ProviderCallId,
			Url = err.Data.Url,
			Stack = err.Data.Stack,
			ExhaustedCliRetries = exhaustedCliRetries,
			TransientUpstreamFailure = transientUpstream,
		};

		// Loud ERROR log: a fatal session-level error from the CLI MUST be visible.
		// All five SDK fields are emitted as structured properties on the log event so
		// log shippers can filter/aggregate on category, request id, HTTP status, etc.
		LogSessionError(
			_requestedModel,
			message,
			details.ErrorType ?? "(none)",
			details.StatusCode,
			details.ProviderCallId ?? "(none)",
			details.Url ?? "(none)",
			details.Stack ?? "(none)");

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
			$"Copilot session failed: {message}",
			details: details));
	}

	private void HandleShutdown(SessionShutdownEvent shutdown)
	{
		var errorReason = shutdown.Data.ErrorReason;
		var shutdownType = shutdown.Data.ShutdownType.ToString();

		// SDK 1.0.0 surfaces a structured billing roll-up on every shutdown event
		// (the per-usage QuotaSnapshots / TotalNanoAiu fields disappeared in 1.0.0).
		// Capture the summary BEFORE we decide whether the shutdown was clean or
		// abnormal — even error shutdowns carry token / nano-AIU counters so the
		// engine can include them in failure telemetry.
		_shutdownSummary = ProjectShutdownSummary(shutdown.Data);

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

	/// <summary>
	/// Projects the SDK 1.0.0 <c>SessionShutdownData</c> into Orchestra's
	/// <see cref="AgentSessionShutdownSummary"/>. Per-model breakdown and code-change
	/// counters are passed through verbatim when present; absent fields stay null so
	/// downstream consumers can distinguish "no data" from "zero".
	/// </summary>
	/// <remarks>
	/// SDK 1.0.0 marks several billing-related fields with the GHCP001 "evaluation-only"
	/// diagnostic (<c>SessionShutdownData.TotalNanoAiu</c>,
	/// <c>ShutdownModelMetric.TotalNanoAiu</c>, <c>ShutdownModelMetricRequests.Count</c>,
	/// <c>ShutdownModelMetricRequests.Cost</c>). They are wire-compatible with 0.3.0's
	/// shape and our consumers already treat them as best-effort, so we suppress the
	/// diagnostic locally rather than removing the fields from our shutdown summary.
	/// </remarks>
	private static AgentSessionShutdownSummary ProjectShutdownSummary(SessionShutdownData data)
	{
		AgentShutdownCodeChanges? codeChanges = null;
		if (data.CodeChanges is { } cc)
		{
			codeChanges = new AgentShutdownCodeChanges(
				FilesModified: cc.FilesModified is { Length: > 0 } files ? files : [],
				LinesAdded: cc.LinesAdded,
				LinesRemoved: cc.LinesRemoved);
		}

		IReadOnlyDictionary<string, AgentShutdownModelMetric>? modelMetrics = null;
		if (data.ModelMetrics is { Count: > 0 } metrics)
		{
			var projected = new Dictionary<string, AgentShutdownModelMetric>(metrics.Count, StringComparer.Ordinal);
			foreach (var (model, metric) in metrics)
			{
				if (metric is null) continue;
				#pragma warning disable GHCP001 // ShutdownModelMetric.TotalNanoAiu / Requests.* are evaluation-only
				projected[model] = new AgentShutdownModelMetric
				{
					TotalNanoAiu = metric.TotalNanoAiu,
					Requests = metric.Requests is { } r
						? new AgentShutdownModelMetricRequests(r.Count, r.Cost)
						: null,
					Usage = metric.Usage is { } u
						? new AgentShutdownModelMetricUsage(
							InputTokens: u.InputTokens,
							OutputTokens: u.OutputTokens,
							CacheReadTokens: u.CacheReadTokens,
							CacheWriteTokens: u.CacheWriteTokens,
							ReasoningTokens: u.ReasoningTokens)
						: null,
				};
				#pragma warning restore GHCP001
			}
			modelMetrics = projected;
		}

		#pragma warning disable GHCP001 // SessionShutdownData.TotalNanoAiu is evaluation-only
		var totalNanoAiu = data.TotalNanoAiu;
		#pragma warning restore GHCP001

		return new AgentSessionShutdownSummary
		{
			TotalNanoAiu = totalNanoAiu,
			ConversationTokens = data.ConversationTokens,
			ToolDefinitionsTokens = data.ToolDefinitionsTokens,
			SystemTokens = data.SystemTokens,
			CurrentTokens = data.CurrentTokens,
			TotalApiDuration = data.TotalApiDuration,
			CodeChanges = codeChanges,
			ModelMetrics = modelMetrics,
		};
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

	/// <summary>
	/// Recognises the bundled CLI's "I exhausted my internal retries" error message so
	/// the agent can route this error class to the swap loop instead of failing the step.
	/// The CLI emits messages of the form:
	///   "Failed to get response from the AI model; retried N times (total retry wait time: ...)"
	/// We match the substring "retried .* times" case-insensitively and also accept the
	/// shorter form "Failed to get response from the AI model" for forward compatibility
	/// since the surrounding text has shifted between CLI versions.
	/// </summary>
	internal static bool LooksLikeCliExhaustedRetries(string? message)
	{
		if (string.IsNullOrEmpty(message))
			return false;

		// "retried N times" is the strong signal — CLI's own retry loop emitted it.
		if (System.Text.RegularExpressions.Regex.IsMatch(
				message,
				@"retried\s+\d+\s+times",
				System.Text.RegularExpressions.RegexOptions.IgnoreCase))
		{
			return true;
		}

		// Fallback: the surrounding "Failed to get response from the AI model" phrase
		// has been stable across CLI versions even when the retry-count format shifted.
		return message.Contains("Failed to get response from the AI model", StringComparison.OrdinalIgnoreCase);
	}

	/// <summary>
	/// Recognises transient upstream failures that a fresh CLI worker is likely to
	/// clear: HTTP 5xx broker errors, 403 / permission_denied identity-handshake
	/// errors, and 429 rate limits. The dying CLI's cached auth/connection state is
	/// the typical culprit — a cold restart re-authenticates from scratch and
	/// resets the upstream connection pool.
	/// </summary>
	/// <remarks>
	/// Detection is layered:
	/// <list type="number">
	///   <item>The SDK-supplied <paramref name="statusCode"/> is consulted first. Any
	///   5xx response, 429, or 403 counts as transient.</item>
	///   <item>The free-form <paramref name="message"/> is scanned for the well-known
	///   strings the upstream broker emits even when the SDK does not surface a
	///   structured status code:
	///   <c>"Error: 5xx"</c>, <c>"HTTP status code 5xx"</c>, <c>"HTTP status code 403"</c>,
	///   <c>"permission_denied"</c>, <c>"can't get copilot user by id"</c>,
	///   <c>"rate limit"</c>.</item>
	/// </list>
	/// Matching is intentionally lenient so the same swap path catches every shape
	/// the broker has shipped to date without needing per-release tuning.
	/// </remarks>
	internal static bool LooksLikeTransientUpstreamFailure(string? message, long? statusCode)
	{
		// Structured status-code path first — most reliable when the SDK supplies one.
		if (statusCode is { } code)
		{
			if (code >= 500 && code <= 599) return true;
			if (code == 429) return true;
			if (code == 403) return true;
		}

		if (string.IsNullOrEmpty(message))
			return false;

		// Message-pattern fallback. The SDK frequently surfaces the HTTP status only
		// inside the free-form message (the structured StatusCode field is null in
		// practice for many error shapes the broker emits today).
		if (System.Text.RegularExpressions.Regex.IsMatch(
				message,
				@"\b(?:Error:|HTTP\s+status\s+code)\s*5\d{2}\b",
				System.Text.RegularExpressions.RegexOptions.IgnoreCase))
		{
			return true;
		}

		if (System.Text.RegularExpressions.Regex.IsMatch(
				message,
				@"\b(?:Error:|HTTP\s+status\s+code)\s*(?:403|429)\b",
				System.Text.RegularExpressions.RegexOptions.IgnoreCase))
		{
			return true;
		}

		// Common broker / identity-handshake strings the CLI surfaces verbatim.
		if (message.Contains("permission_denied", StringComparison.OrdinalIgnoreCase))
			return true;
		if (message.Contains("can't get copilot user by id", StringComparison.OrdinalIgnoreCase))
			return true;
		if (message.Contains("rate limit", StringComparison.OrdinalIgnoreCase))
			return true;
		if (message.Contains("Forbidden", StringComparison.OrdinalIgnoreCase)
			&& message.Contains("intermediary", StringComparison.OrdinalIgnoreCase))
			return true;
		// SDK session-create failures where the bundled CLI lost its auth handle.
		// A fresh CLI worker creates a new session with valid auth from scratch,
		// which clears the failure.
		if (message.Contains("Session was not created with authentication info", StringComparison.OrdinalIgnoreCase))
			return true;
		if (message.Contains("custom provider", StringComparison.OrdinalIgnoreCase)
			&& message.Contains("authentication", StringComparison.OrdinalIgnoreCase))
			return true;

		return false;
	}

	#region Source-Generated Logging

	[LoggerMessage(
		EventId = 1,
		Level = LogLevel.Error,
		Message = "Copilot session failed (model={Model}): {Message} [errorType={ErrorType}, statusCode={StatusCode}, providerCallId={ProviderCallId}, url={Url}, stack={Stack}]")]
	private partial void LogSessionError(
		string model,
		string message,
		string errorType,
		long? statusCode,
		string providerCallId,
		string url,
		string stack);

	[LoggerMessage(
		EventId = 2,
		Level = LogLevel.Error,
		Message = "Copilot session shutdown abnormally (model={Model}, type={ShutdownType}): {Reason}")]
	private partial void LogAbnormalShutdown(string model, string shutdownType, string reason);

	[LoggerMessage(
		EventId = 3,
		Level = LogLevel.Debug,
		Message = "Sub-agent frame activated: toolCallId={ToolCallId} agent={AgentName} depth={Depth}")]
	private partial void LogSubagentFrameActivated(string toolCallId, string agentName, int depth);

	[LoggerMessage(
		EventId = 4,
		Level = LogLevel.Debug,
		Message = "Sub-agent frame removed: toolCallId={ToolCallId} agent={AgentName} remainingDepth={RemainingDepth}")]
	private partial void LogSubagentFrameRemoved(string toolCallId, string agentName, int remainingDepth);

	[LoggerMessage(
		EventId = 5,
		Level = LogLevel.Warning,
		Message = "Sub-agent start reused active toolCallId={ToolCallId}; replacing previous frame for agent={AgentName}.")]
	private partial void LogSubagentFrameReplaced(string toolCallId, string agentName);

	[LoggerMessage(
		EventId = 6,
		Level = LogLevel.Warning,
		Message = "Sub-agent completion for unknown toolCallId={ToolCallId}. Active frames left intact (event predates a SubagentStarted, or was already removed).")]
	private partial void LogSubagentFrameMissing(string toolCallId);

	[LoggerMessage(
		EventId = 7,
		Level = LogLevel.Warning,
		Message = "SDK ParentToolCallId={ParentToolCallId} not found in active sub-agent frames; falling back to current actor.")]
	private partial void LogParentToolCallIdNotFound(string parentToolCallId);

	// EventId 8: per-turn accumulator (Phase 3.1). Information-level so a long-running
	// turn is visible in default-verbosity host logs without enabling Debug.
	[LoggerMessage(
		EventId = 8,
		Level = LogLevel.Information,
		Message = "Turn ended (model={Model}, turnId={TurnId}): elapsed={ElapsedMs}ms, contentGrowth={ContentGrowthChars} chars, messageDeltas={MessageDeltaCount}, reasoningDeltas={ReasoningDeltaCount}")]
	private partial void LogTurnEnded(
		string model,
		string turnId,
		long elapsedMs,
		int contentGrowthChars,
		int messageDeltaCount,
		int reasoningDeltaCount);

	// EventId 9: per-heartbeat session-usage info (Phase 3.2). Debug-level so the noise
	// stays out of default logs but is one switch away when triaging long sessions.
	// SDK 1.0.0 narrowed TokenLimit and CurrentTokens to long (int64). We accept long
	// here because the structured-log shipper preserves the type fidelity better than
	// the legacy double signature did.
	[LoggerMessage(
		EventId = 9,
		Level = LogLevel.Debug,
		Message = "Session usage heartbeat #{HeartbeatNumber} (model={Model}): tokenLimit={TokenLimit}, currentTokens={CurrentTokens}, sessionElapsed={SessionElapsedMs}ms, turnElapsed={TurnElapsedMs}ms")]
	private partial void LogSessionUsageInfo(
		string model,
		int heartbeatNumber,
		long tokenLimit,
		long currentTokens,
		long sessionElapsedMs,
		long turnElapsedMs);

	// EventId 10: session resumed on a fresh CLI worker. Information-level — this is a
	// notable recovery event that operators want visible in default-verbosity logs.
	// SDK 1.0.0: SessionResumeData.EventCount is long (int64), not double.
	[LoggerMessage(
		EventId = 10,
		Level = LogLevel.Information,
		Message = "Session resumed (model={Model}, alreadyInUse={AlreadyInUse}, eventCount={EventCount}, selectedModel={SelectedModel}, resumeTime={ResumeTime})")]
	private partial void LogSessionResumed(
		string model,
		bool alreadyInUse,
		long eventCount,
		string selectedModel,
		string resumeTime);

	// EventId 11: SDK 1.0.0 introduced ModelCallFailureEvent — a single failing model API
	// call (HTTP error, timeout, rate-limit) that does NOT yet end the session. The CLI
	// retries internally; if it gives up, our existing SessionErrorEvent / swap-loop
	// machinery kicks in. We log at Warning so the operator log shows upstream flakiness
	// without alarming on every transient retry.
	[LoggerMessage(
		EventId = 11,
		Level = LogLevel.Warning,
		Message = "Model call failure (source={Source}, model={Model}, statusCode={StatusCode}, message={Message}) — CLI retry loop will attempt recovery")]
	private partial void LogModelCallFailure(
		string source,
		string model,
		int? statusCode,
		string message);

	#endregion
}
