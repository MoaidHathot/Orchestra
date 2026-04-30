namespace Orchestra.Engine;

public class Mcp
{
	public required string Name { get; init; }
	public required McpType Type { get; init; }

	/// <summary>
	/// Optional per-server timeout for tool calls. When set, the MCP client (e.g., the
	/// Copilot SDK's <c>McpServerConfig.Timeout</c>) is configured to use this value
	/// instead of its default. Use this for MCP servers that host long-running tools
	/// (e.g., the orchestra MCP server's <c>invoke_orchestration</c> tool in sync mode)
	/// to avoid premature transport-level timeouts.
	/// </summary>
	public TimeSpan? Timeout { get; init; }
}
