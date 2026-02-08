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
}
