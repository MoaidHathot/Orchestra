namespace Orchestra.Engine;

/// <summary>
/// Defines a subagent that the main step orchestrator can delegate to.
/// Subagents have their own system prompt, tool restrictions, and optional MCP servers.
/// The runtime can automatically delegate to these agents when a user's request matches
/// the subagent's expertise based on its name and description.
/// </summary>
public class Subagent
{
	/// <summary>
	/// Unique name/identifier for the subagent. Required.
	/// </summary>
	public required string Name { get; init; }

	/// <summary>
	/// Human-readable display name shown in events and UI.
	/// </summary>
	public string? DisplayName { get; init; }

	/// <summary>
	/// Description of what the subagent does.
	/// Helps the runtime select the right subagent based on user intent.
	/// </summary>
	public string? Description { get; init; }

	/// <summary>
	/// The system prompt for this subagent. Required.
	/// Defines the subagent's behavior and capabilities.
	/// </summary>
	public required string Prompt { get; init; }

	/// <summary>
	/// List of tool names the subagent can use.
	/// When null or empty, all available tools are accessible.
	/// Use this to restrict subagent capabilities (e.g., read-only tools for a researcher).
	/// </summary>
	public string[]? Tools { get; init; }

	/// <summary>
	/// MCP servers specific to this subagent.
	/// Resolved at runtime from the available MCP configurations.
	/// </summary>
	public Mcp[] Mcps { get; internal set; } = [];

	/// <summary>
	/// Raw MCP names from JSON, used internally during parsing to resolve to <see cref="Mcps"/>.
	/// </summary>
	internal string[] McpNames { get; init; } = [];

	/// <summary>
	/// Whether the subagent should be available for automatic selection based on model inference.
	/// When true (default), the runtime can auto-select this subagent based on user intent.
	/// When false, the subagent is only invoked when explicitly requested.
	/// </summary>
	public bool Infer { get; init; } = true;
}
