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
}
