namespace Orchestra.Engine;

public class AgentEvent
{
	public required AgentEventType Type { get; init; }
	public string? Content { get; init; }
	public string? ErrorMessage { get; init; }

	/// <summary>
	/// The model involved in this event (used by SessionStart, ModelChange, Usage events).
	/// </summary>
	public string? Model { get; init; }

	/// <summary>
	/// The previous model (used by ModelChange event when the server changes models).
	/// </summary>
	public string? PreviousModel { get; init; }

	/// <summary>
	/// Token usage data (used by Usage event).
	/// </summary>
	public AgentUsage? Usage { get; init; }

	// ── Tool execution data (used by ToolExecutionStart / ToolExecutionComplete) ──

	/// <summary>
	/// Unique identifier for this tool call, used to correlate start/complete events.
	/// </summary>
	public string? ToolCallId { get; init; }

	/// <summary>
	/// The name of the tool being executed.
	/// </summary>
	public string? ToolName { get; init; }

	/// <summary>
	/// Serialized arguments passed to the tool.
	/// </summary>
	public string? ToolArguments { get; init; }

	/// <summary>
	/// The MCP server that owns this tool (if any).
	/// </summary>
	public string? McpServerName { get; init; }

	/// <summary>
	/// Whether the tool execution succeeded (used by ToolExecutionComplete).
	/// </summary>
	public bool? ToolSuccess { get; init; }

	/// <summary>
	/// The result content returned by the tool (used by ToolExecutionComplete).
	/// </summary>
	public string? ToolResult { get; init; }

	/// <summary>
	/// The error message if the tool failed (used by ToolExecutionComplete).
	/// </summary>
	public string? ToolError { get; init; }

	// ── Session diagnostics (used by Warning, Info events) ──

	/// <summary>
	/// The warning/info category type from the SDK (e.g., "mcp_server_error", "tool_discovery_failed").
	/// </summary>
	public string? DiagnosticType { get; init; }

	// ── MCP server lifecycle data (used by McpServersLoaded, McpServerStatusChanged) ──

	/// <summary>
	/// List of MCP server statuses (used by McpServersLoaded event).
	/// </summary>
	public IReadOnlyList<McpServerStatusInfo>? McpServerStatuses { get; init; }

	/// <summary>
	/// The new status of an MCP server (used by McpServerStatusChanged event).
	/// </summary>
	public string? McpServerStatus { get; init; }

	// ── Subagent data (used by SubagentSelected, SubagentStarted, SubagentCompleted, SubagentFailed) ──

	/// <summary>
	/// The unique name/identifier of the subagent.
	/// </summary>
	public string? SubagentName { get; init; }

	/// <summary>
	/// The human-readable display name of the subagent.
	/// </summary>
	public string? SubagentDisplayName { get; init; }

	/// <summary>
	/// The description of the subagent (used by SubagentStarted).
	/// </summary>
	public string? SubagentDescription { get; init; }

	/// <summary>
	/// The list of tools available to the subagent (used by SubagentSelected).
	/// </summary>
	public string[]? SubagentTools { get; init; }

	// ── Context compaction data (used by CompactionStart, CompactionComplete) ──

	/// <summary>
	/// Token count before compaction (used by CompactionComplete).
	/// </summary>
	public int? CompactionTokensBefore { get; init; }

	/// <summary>
	/// Token count after compaction (used by CompactionComplete).
	/// </summary>
	public int? CompactionTokensAfter { get; init; }

	// ── Hook lifecycle data (used by HookStart, HookEnd) ──

	/// <summary>
	/// Unique identifier for a hook invocation, used to correlate HookStart/HookEnd events.
	/// </summary>
	public string? HookInvocationId { get; init; }

	/// <summary>
	/// The type of hook being executed (e.g., "preToolUse", "postToolUse", "sessionStart").
	/// </summary>
	public string? HookType { get; init; }

	/// <summary>
	/// Whether the hook completed successfully (used by HookEnd).
	/// </summary>
	public bool? HookSuccess { get; init; }

	// ── Turn tracking data (used by TurnStart) ──

	/// <summary>
	/// Identifier for the current assistant turn in multi-turn conversations.
	/// </summary>
	public string? TurnId { get; init; }

	// ── Session usage info data (used by SessionUsageInfo) ──

	/// <summary>
	/// Maximum context window token limit for the session.
	/// </summary>
	public double? TokenLimit { get; init; }

	/// <summary>
	/// Current token count used in the session.
	/// </summary>
	public double? CurrentTokens { get; init; }

	// ── Auto mode switching (SDK 0.3.0) ──

	/// <summary>
	/// SDK request id correlating <see cref="AgentEventType.AutoModeSwitchRequested"/>
	/// with its corresponding <see cref="AgentEventType.AutoModeSwitchCompleted"/>.
	/// </summary>
	public string? AutoModeRequestId { get; init; }

	/// <summary>
	/// SDK error code that triggered an auto-mode model switch (e.g. rate-limit code).
	/// Null on the completed event.
	/// </summary>
	public string? AutoModeErrorCode { get; init; }

	/// <summary>
	/// SDK response on completion (typically the new model name or status string).
	/// Null on the requested event.
	/// </summary>
	public string? AutoModeResponse { get; init; }

	// ── System notifications (SDK 0.3.0) ──

	/// <summary>
	/// Discriminator for <see cref="AgentEventType.SystemNotification"/>: e.g. "agent_completed",
	/// "agent_idle", "shell_completed", "shell_detached_completed", "new_inbox_message".
	/// </summary>
	public string? NotificationKind { get; init; }

	/// <summary>
	/// Human-readable notification text from the SDK (the <c>Content</c> field on
	/// <c>SystemNotificationData</c>).
	/// </summary>
	public string? NotificationMessage { get; init; }

	// ── Quota snapshots (SDK 0.3.0 — emitted alongside AssistantUsageEvent) ──

	/// <summary>
	/// Quota snapshots as reported by the SDK, keyed by quota name.
	/// </summary>
	public IReadOnlyDictionary<string, AgentQuotaSnapshot>? QuotaSnapshots { get; init; }

	// ── CLI swap / session resume recovery (used by CliInstanceSwapped, SessionResumed) ──

	/// <summary>
	/// Zero-based index of the current swap attempt for the step. <c>0</c> = original attempt,
	/// <c>1</c> = first swap, etc. Stamped on <see cref="AgentEventType.CliInstanceSwapped"/>.
	/// </summary>
	public int? SwapAttempt { get; init; }

	/// <summary>
	/// Total number of swaps allowed for this step (the per-step swap budget). Stamped on
	/// <see cref="AgentEventType.CliInstanceSwapped"/> so observers can render "swap 1 of 3".
	/// </summary>
	public int? SwapBudget { get; init; }

	/// <summary>
	/// Short machine-friendly reason for the swap, e.g. <c>"transport_lost"</c>,
	/// <c>"cli_exhausted_retries"</c>, <c>"abnormal_shutdown"</c>, <c>"resume_locked"</c>.
	/// </summary>
	public string? SwapReason { get; init; }

	/// <summary>
	/// Recovery mode for the swap: <c>"resume"</c> when the new CLI will pick up the prior
	/// session id, <c>"cold_restart"</c> when the prompt is re-sent on a fresh session.
	/// </summary>
	public string? SwapMode { get; init; }

	/// <summary>
	/// Session id of the session that failed and triggered the swap. Null on the original
	/// attempt's failure if no session id was ever issued (CreateSessionAsync threw).
	/// </summary>
	public string? PriorSessionId { get; init; }

	/// <summary>
	/// Number of persisted events that already exist in the resumed session, as reported
	/// by the SDK's <c>SessionResumeData.EventCount</c>. Stamped on
	/// <see cref="AgentEventType.SessionResumed"/>.
	/// </summary>
	public int? ResumedEventCount { get; init; }

	/// <summary>
	/// True when the SDK reports that another client already had the session open at resume
	/// time (<c>SessionResumeData.AlreadyInUse</c>). Indicates the previous CLI hasn't fully
	/// released the session lock yet; Orchestra polls briefly and falls back to cold restart
	/// if the lock isn't released within the grace window.
	/// </summary>
	public bool? ResumeAlreadyInUse { get; init; }

	// ── Actor attribution (sub-agent vs main agent) ──

	/// <summary>
	/// The unique name/identifier of the sub-agent that emitted this event,
	/// or null if the event was emitted by the main agent for the step.
	/// Stamped on every event by <see cref="CopilotSessionHandler"/> using the SDK's
	/// <c>ParentToolCallId</c> when available, or the active sub-agent stack otherwise.
	/// </summary>
	public string? ActorAgentName { get; init; }

	/// <summary>
	/// Human-readable display name of the actor sub-agent, for UI rendering.
	/// </summary>
	public string? ActorAgentDisplayName { get; init; }

	/// <summary>
	/// The <c>ToolCallId</c> of the <c>SubagentStarted</c> event that opened the
	/// current actor's scope. Stable per sub-agent invocation; lets clients group
	/// all events from a single invocation together (e.g. one card per invocation).
	/// </summary>
	public string? ActorToolCallId { get; init; }

	/// <summary>
	/// Nesting depth: 0 = main agent for the step, 1 = first-level sub-agent,
	/// 2+ = nested sub-agent invocations. Future-proofs nested sub-agent rendering.
	/// </summary>
	public int ActorDepth { get; init; }

	/// <summary>
	/// Convenience accessor that materialises the actor fields as an <see cref="ActorContext"/>.
	/// </summary>
	public ActorContext Actor =>
		new(ActorAgentName, ActorAgentDisplayName, ActorToolCallId, ActorDepth);

	// ── Model call failure (SDK 1.0.0 — observational only) ──

	/// <summary>
	/// SDK 1.0.0 <c>ModelCallFailureData.Source.Value</c>: where the failing call
	/// originated (<c>"top_level"</c>, <c>"subagent"</c>, or <c>"mcp_sampling"</c>).
	/// Stamped on <see cref="AgentEventType.ModelCallFailure"/>.
	/// </summary>
	public string? ModelCallFailureSource { get; init; }

	/// <summary>
	/// SDK 1.0.0 <c>ModelCallFailureData.ErrorMessage</c>: the upstream error message
	/// surfaced by the model API (or null if the SDK couldn't extract one).
	/// </summary>
	public string? ModelCallFailureMessage { get; init; }

	/// <summary>
	/// SDK 1.0.0 <c>ModelCallFailureData.Model</c>: the model identifier the failing
	/// call was targeting. Often the same as the session model but can differ for
	/// sub-agent or MCP-sampling-driven calls.
	/// </summary>
	public string? ModelCallFailureModel { get; init; }

	/// <summary>
	/// SDK 1.0.0 <c>ModelCallFailureData.StatusCode</c>: HTTP status (or null if the
	/// failure was a transport-level issue with no HTTP response). Useful for the
	/// Portal to bucket failures by class (5xx vs 4xx vs network).
	/// </summary>
	public int? ModelCallFailureStatusCode { get; init; }

	// ── Richer SDK 1.0.0 diagnostic fields ───────────────────────────────────
	// These mirror previously-dropped fields the SDK surfaces on per-event payloads.
	// They are additive: leaving them null on AgentEvent emitters that don't supply
	// them costs nothing for downstream consumers (existing fields keep working).

	/// <summary>
	/// SDK 1.0.0 <c>AssistantUsageData.InterTokenLatency</c> projected to milliseconds.
	/// Streaming-perf metric: time gap between tokens during a response. Useful for
	/// detecting upstream slowness without waiting for the full call to complete.
	/// Stamped on <see cref="AgentEventType.Usage"/>.
	/// </summary>
	public double? InterTokenLatencyMs { get; init; }

	/// <summary>
	/// SDK 1.0.0 <c>SessionInfoData.Tip</c>: optional CLI-side hint string the runtime
	/// surfaces alongside info messages (e.g. "Try running …"). Stamped on
	/// <see cref="AgentEventType.Info"/>.
	/// </summary>
	public string? InfoTip { get; init; }

	/// <summary>
	/// SDK 1.0.0 <c>SessionInfoData.Url</c>: optional CLI-side hyperlink the runtime
	/// associates with an info message (typically a docs URL). Stamped on
	/// <see cref="AgentEventType.Info"/>.
	/// </summary>
	public string? InfoUrl { get; init; }

	/// <summary>
	/// SDK 1.0.0 <c>SessionWarningData.Url</c>: optional CLI-side hyperlink the runtime
	/// associates with a warning message (typically a docs / status-page URL).
	/// Stamped on <see cref="AgentEventType.Warning"/>.
	/// </summary>
	public string? WarningUrl { get; init; }

	/// <summary>
	/// SDK 1.0.0 <c>ToolExecutionStartData.Model</c> / <c>ToolExecutionCompleteData.Model</c>:
	/// the model that initiated the tool call. Useful when multiple models are active
	/// in a session (sub-agents, auto-mode switches). Stamped on
	/// <see cref="AgentEventType.ToolExecutionStart"/> and
	/// <see cref="AgentEventType.ToolExecutionComplete"/>.
	/// </summary>
	public string? ToolExecutionModel { get; init; }

	/// <summary>
	/// SDK 1.0.0 <c>ToolExecutionStartData.TurnId</c> / <c>ToolExecutionCompleteData.TurnId</c>:
	/// correlates a tool call back to the assistant turn that triggered it.
	/// </summary>
	public string? ToolExecutionTurnId { get; init; }

	/// <summary>
	/// SDK 1.0.0 <c>ToolExecutionStartData.DisplayVerbatim</c>: hint from the runtime
	/// that the tool's output should be shown verbatim (vs. pretty-printed) by UIs.
	/// Stamped on <see cref="AgentEventType.ToolExecutionStart"/>.
	/// </summary>
	public bool? ToolDisplayVerbatim { get; init; }

	/// <summary>
	/// SDK 1.0.0 <c>ToolExecutionCompleteData.Sandboxed</c>: indicates the tool ran
	/// inside the runtime's sandbox. Useful for audit / Portal display.
	/// </summary>
	public bool? ToolSandboxed { get; init; }

	/// <summary>
	/// SDK 1.0.0 <c>ToolExecutionCompleteData.ToolDescription</c>: human-readable
	/// description the runtime attaches to the tool definition. Lets Portals render
	/// per-tool cards without maintaining a hardcoded catalog. We surface the
	/// description's display text only (the structured meta is consumed internally).
	/// </summary>
	public string? ToolDescription { get; init; }

	/// <summary>
	/// SDK 1.0.0 <c>SessionResumeData.SessionWasActive</c>: true if the resumed session
	/// was active when the snapshot was taken (i.e. the dying CLI still had work in
	/// flight). Helps the swap loop decide whether ContinuePendingWork should fire.
	/// </summary>
	public bool? ResumeSessionWasActive { get; init; }

	/// <summary>
	/// SDK 1.0.0 <c>SessionResumeData.ContinuePendingWork</c>: signals whether the
	/// runtime resumed the prior session's pending message. Stamped on
	/// <see cref="AgentEventType.SessionResumed"/> for observability of swap behaviour.
	/// </summary>
	public bool? ResumeContinuePendingWork { get; init; }

	/// <summary>
	/// SDK 1.0.0 <c>AssistantMessageData.Model</c>: the model that produced this
	/// message (may differ from the session model for sub-agents / auto-mode).
	/// </summary>
	public string? MessageModel { get; init; }

	/// <summary>
	/// SDK 1.0.0 <c>AssistantMessageData.OutputTokens</c>: per-message output token
	/// count. Sums up to <see cref="AgentUsage.OutputTokens"/> on the matching turn.
	/// </summary>
	public long? MessageOutputTokens { get; init; }

	/// <summary>
	/// SDK 1.0.0 <c>AssistantMessageData.RequestId</c>: the upstream request id the
	/// SDK used to generate the message. Surfaces in upstream provider logs for
	/// triage of model-side issues.
	/// </summary>
	public string? MessageRequestId { get; init; }

	/// <summary>
	/// SDK 1.0.0 <c>AssistantMessageData.TurnId</c>: correlates this message back to
	/// the assistant turn that produced it.
	/// </summary>
	public string? MessageTurnId { get; init; }

	// ── Per-call permission lifecycle (SDK 1.0.0 — PermissionRequested / PermissionCompleted) ──

	/// <summary>
	/// SDK 1.0.0 <c>PermissionRequestedData.RequestId</c> / <c>PermissionCompletedData.RequestId</c>:
	/// correlation id linking a <see cref="AgentEventType.PermissionRequested"/> entry to
	/// its matching <see cref="AgentEventType.PermissionCompleted"/> entry.
	/// </summary>
	public string? PermissionRequestId { get; init; }

	/// <summary>
	/// Kind discriminator for the permission request: <c>"read"</c>, <c>"write"</c>,
	/// <c>"shell"</c>, <c>"url"</c>, <c>"mcp"</c>, <c>"memory"</c>, <c>"customTool"</c>,
	/// <c>"hook"</c>, <c>"extensionManagement"</c>, <c>"extensionPermissionAccess"</c>.
	/// Stamped on <see cref="AgentEventType.PermissionRequested"/>.
	/// </summary>
	public string? PermissionKind { get; init; }

	/// <summary>
	/// Human-readable summary of the resource the permission applies to. Depends on Kind:
	/// path for read/write, full command text for shell, URL for url, <c>"server::tool"</c>
	/// for mcp, subject for memory, tool name for customTool/hook, extension name for the
	/// extension kinds. Stamped on <see cref="AgentEventType.PermissionRequested"/>.
	/// </summary>
	public string? PermissionTarget { get; init; }

	/// <summary>
	/// The tool call id that triggered the permission request, as reported by the SDK on
	/// the request subclass. Distinct from <see cref="ToolCallId"/> (left null on permission
	/// events because permissions are not themselves tool calls). Stamped on both
	/// <see cref="AgentEventType.PermissionRequested"/> and <see cref="AgentEventType.PermissionCompleted"/>
	/// when the SDK supplies it.
	/// </summary>
	public string? PermissionToolCallId { get; init; }

	/// <summary>
	/// Result-kind discriminator for <see cref="AgentEventType.PermissionCompleted"/>:
	/// <c>"approved"</c>, <c>"approvedForLocation"</c>, <c>"approvedForSession"</c>,
	/// <c>"cancelled"</c>, <c>"deniedByContentExclusionPolicy"</c>,
	/// <c>"deniedByPermissionRequestHook"</c>, <c>"deniedByRules"</c>,
	/// <c>"deniedInteractivelyByUser"</c>, <c>"deniedNoApprovalRule"</c>, or <c>"unknown"</c>
	/// for a future SDK result kind we don't yet recognise.
	/// </summary>
	public string? PermissionDecision { get; init; }

	/// <summary>
	/// Optional human-readable context for a permission decision: location key for
	/// <c>approvedForLocation</c>, denial message / feedback / rule list for the denied
	/// results, cancellation reason for <c>cancelled</c>. Null when the SDK supplies no
	/// additional context (e.g. plain <c>approved</c>).
	/// </summary>
	public string? PermissionDecisionReason { get; init; }
}

/// <summary>
/// Represents the status of an individual MCP server.
/// <para>
/// <see cref="Status"/>, <see cref="Source"/> and <see cref="Error"/> come from the
/// Copilot SDK's <c>SessionMcpServersLoadedEvent</c> — they describe the
/// <em>transport-level</em> connection (e.g. <c>"Connected"</c> means the SDK opened the
/// MCP channel, NOT that <c>tools/list</c> succeeded or returned anything).
/// </para>
/// <para>
/// <see cref="ToolCount"/> is supplied by Orchestra itself (via
/// <see cref="IMcpResolver.GetGlobalMcpToolCountsAsync"/>) — it is the number of tools
/// the upstream backend exposed when Orchestra probed it directly. A value of <c>0</c>
/// on a server whose <see cref="Status"/> is <c>"Connected"</c> is the
/// "MCP connected but no tools" failure mode (e.g. an upstream proxy with
/// <c>deferConnection: true</c> whose backend has not finished authenticating yet);
/// <see langword="null"/> means Orchestra did not / could not probe that server
/// (for example because it is an inline MCP, not a global one routed through
/// <c>McpManager</c>'s in-process proxy).
/// </para>
/// </summary>
public record McpServerStatusInfo(
	string Name,
	string Status,
	string? Source = null,
	string? Error = null,
	int? ToolCount = null);
