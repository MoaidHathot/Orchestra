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
}

/// <summary>
/// Represents the status of an individual MCP server, as reported by the SDK.
/// </summary>
public record McpServerStatusInfo(
	string Name,
	string Status,
	string? Source = null,
	string? Error = null);
