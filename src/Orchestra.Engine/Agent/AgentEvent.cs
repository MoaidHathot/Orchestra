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
}
