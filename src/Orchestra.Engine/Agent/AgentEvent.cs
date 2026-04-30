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
}

/// <summary>
/// Represents the status of an individual MCP server, as reported by the SDK.
/// </summary>
public record McpServerStatusInfo(
	string Name,
	string Status,
	string? Source = null,
	string? Error = null);
