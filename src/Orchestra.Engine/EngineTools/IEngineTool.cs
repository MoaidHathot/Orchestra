namespace Orchestra.Engine;

/// <summary>
/// Defines a tool that the orchestration engine injects into prompt steps.
/// Engine tools allow the LLM to interact with the engine during execution
/// (e.g., signaling failure, setting metadata, emitting structured logs).
///
/// Unlike MCP tools which are external services, engine tools are built-in
/// and execute in-process, producing side effects on the <see cref="EngineToolContext"/>.
/// </summary>
public interface IEngineTool
{
	/// <summary>
	/// Unique name of this tool, used by the LLM to invoke it.
	/// Should follow a naming convention like "orchestra_set_status".
	/// </summary>
	string Name { get; }

	/// <summary>
	/// Human-readable description of what this tool does.
	/// This is included in the tool definition sent to the LLM.
	/// </summary>
	string Description { get; }

	/// <summary>
	/// JSON Schema describing the tool's input parameters.
	/// This is included in the tool definition sent to the LLM.
	/// </summary>
	string ParametersSchema { get; }

	/// <summary>
	/// Executes the tool with the given arguments, producing side effects
	/// on the <see cref="EngineToolContext"/>.
	/// </summary>
	/// <param name="arguments">Serialized JSON arguments from the LLM.</param>
	/// <param name="context">Shared context for recording side effects.</param>
	/// <returns>A result string to return to the LLM as the tool response.</returns>
	string Execute(string arguments, EngineToolContext context);
}
