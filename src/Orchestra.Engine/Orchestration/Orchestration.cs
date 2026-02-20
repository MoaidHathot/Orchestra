namespace Orchestra.Engine;

public class Orchestration
{
	public required string Name { get; init; }
	public required string Description { get; init; }
	public required OrchestrationStep[] Steps { get; init; }

	/// <summary>
	/// Version of the orchestration. Defaults to "1.0.0".
	/// Used for tracking execution history and orchestration changes.
	/// </summary>
	public string Version { get; init; } = "1.0.0";

	/// <summary>
	/// Optional trigger configuration defined in the orchestration JSON.
	/// Can be overridden by user-defined triggers set via the UI.
	/// </summary>
	public TriggerConfig? Trigger { get; init; }

	/// <summary>
	/// Optional inline MCP server definitions in the orchestration JSON.
	/// At runtime, these are merged with any external mcp.json definitions
	/// (external definitions take priority on name conflicts).
	/// </summary>
	public Mcp[] Mcps { get; init; } = [];
}
