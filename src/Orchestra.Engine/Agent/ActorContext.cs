namespace Orchestra.Engine;

/// <summary>
/// Identifies which actor (main agent or a specific sub-agent invocation) produced an event.
/// Stamped on every <see cref="AgentEvent"/> so live consumers (Portal, CLI) can visually
/// distinguish, group and color sub-agent activity rather than blending it into a single
/// flat stream with the main agent.
///
/// <see cref="Depth"/> 0 with <see cref="AgentName"/> = null means "the main agent for the step".
/// <see cref="Depth"/> &gt;= 1 identifies a sub-agent invocation, scoped by <see cref="ToolCallId"/>
/// (assigned by the SDK on the corresponding <c>SubagentStarted</c> event).
/// </summary>
public readonly record struct ActorContext(
	string? AgentName,
	string? AgentDisplayName,
	string? ToolCallId,
	int Depth)
{
	/// <summary>
	/// The well-known main-agent context (depth 0, no sub-agent identity).
	/// </summary>
	public static ActorContext Main { get; } = new(null, null, null, 0);

	/// <summary>
	/// True when this context refers to the main agent (no enclosing sub-agent).
	/// </summary>
	public bool IsMain => Depth == 0 && AgentName is null;
}
