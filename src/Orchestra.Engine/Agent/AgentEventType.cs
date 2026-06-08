namespace Orchestra.Engine;

public enum AgentEventType
{
	MessageDelta,
	Message,
	ReasoningDelta,
	Reasoning,
	ToolExecutionStart,
	ToolExecutionComplete,
	SessionIdle,
	Error,
	SessionStart,
	ModelChange,
	Usage,

	// Subagent events
	SubagentSelected,
	SubagentStarted,
	SubagentCompleted,
	SubagentFailed,
	SubagentDeselected,

	// Session diagnostics
	Warning,
	Info,

	// MCP server lifecycle
	McpServersLoaded,
	McpServerStatusChanged,

	// Context compaction (infinite sessions)
	CompactionStart,
	CompactionComplete,

	// Hook lifecycle (SDK pre/post hooks)
	HookStart,
	HookEnd,

	// Turn tracking (multi-turn conversations)
	TurnStart,
	TurnEnd,

	// Session-level token usage info
	SessionUsageInfo,

	// Auto mode switching (model fallback when rate-limited; SDK 0.3.0)
	AutoModeSwitchRequested,
	AutoModeSwitchCompleted,

	// System notifications (SDK 0.3.0 typed discriminator: agent_completed,
	// agent_idle, shell_completed, shell_detached_completed, new_inbox_message)
	SystemNotification,

	// Quota / entitlement snapshot (emitted alongside usage events)
	QuotaSnapshot,

	// ── CLI swap / session resume recovery (Orchestra.Copilot) ──

	/// <summary>
	/// Emitted when the agent abandons the current CLI worker mid-step and acquires a
	/// fresh one to recover from a transport-level failure (e.g. JSON-RPC connection lost,
	/// CLI process died, CLI exhausted its internal model-API retries). Informational —
	/// the step continues on the new worker, either by resuming the prior session (if
	/// session id was captured and resume is enabled) or by re-sending the original prompt.
	/// Carries <see cref="AgentEvent.SwapAttempt"/>, <see cref="AgentEvent.SwapBudget"/>,
	/// <see cref="AgentEvent.SwapReason"/>, <see cref="AgentEvent.SwapMode"/>,
	/// <see cref="AgentEvent.PriorSessionId"/>.
	/// </summary>
	CliInstanceSwapped,

	/// <summary>
	/// Emitted when the agent successfully resumes an existing Copilot session on a fresh
	/// CLI worker. Surfaces the SDK's <c>SessionResumeEvent</c> payload so operators and
	/// UIs can show "resumed at event N, model X". Carries
	/// <see cref="AgentEvent.ResumedEventCount"/>, <see cref="AgentEvent.ResumeAlreadyInUse"/>.
	/// </summary>
	SessionResumed,

	/// <summary>
	/// SDK 1.0.0 introduced <c>ModelCallFailureEvent</c> — fires when an individual model
	/// API call faults (HTTP error, timeout, rate-limit), distinct from a fatal session
	/// error. The CLI's own retry loop normally recovers without us doing anything; we
	/// emit this as a pure observability signal so the Portal and operator logs can
	/// surface upstream flakiness ahead of an eventual <see cref="Error"/>. Carries
	/// <see cref="AgentEvent.ModelCallFailureSource"/> ("top_level" / "subagent" / "mcp_sampling"),
	/// <see cref="AgentEvent.ModelCallFailureMessage"/>, <see cref="AgentEvent.ModelCallFailureModel"/>,
	/// <see cref="AgentEvent.ModelCallFailureStatusCode"/>.
	/// </summary>
	ModelCallFailure,

	// ── Per-call permission lifecycle (SDK 1.0.0) ──

	/// <summary>
	/// SDK 1.0.0 <c>PermissionRequestedEvent</c>: emitted before a side-effectful action
	/// (file read/write, shell command, URL fetch, MCP tool, memory access, etc.) so the
	/// host can approve or deny the request. Orchestra uses
	/// <c>PermissionHandler.ApproveAll</c> at session creation, so every request resolves
	/// to "approved" — but we still want a per-call audit breadcrumb so a run trace can
	/// reconstruct exactly what the agent was allowed to do. Carries
	/// <see cref="AgentEvent.PermissionRequestId"/>, <see cref="AgentEvent.PermissionKind"/>,
	/// <see cref="AgentEvent.PermissionTarget"/>, <see cref="AgentEvent.PermissionToolCallId"/>.
	/// </summary>
	PermissionRequested,

	/// <summary>
	/// SDK 1.0.0 <c>PermissionCompletedEvent</c>: paired completion of a prior
	/// <see cref="PermissionRequested"/>, carrying the decision (approved / denied / cancelled
	/// + the specific result kind) and any contextual reason (denial message, location key,
	/// feedback, rule names). Carries <see cref="AgentEvent.PermissionRequestId"/>,
	/// <see cref="AgentEvent.PermissionDecision"/>, <see cref="AgentEvent.PermissionDecisionReason"/>,
	/// <see cref="AgentEvent.PermissionToolCallId"/>.
	/// </summary>
	PermissionCompleted,
}
