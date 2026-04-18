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

	// Session-level token usage info
	SessionUsageInfo,
}
