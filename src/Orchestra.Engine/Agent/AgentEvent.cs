namespace Orchestra.Engine;

public class AgentEvent
{
	public required AgentEventType Type { get; init; }
	public string? Content { get; init; }
	public string? ErrorMessage { get; init; }
}
