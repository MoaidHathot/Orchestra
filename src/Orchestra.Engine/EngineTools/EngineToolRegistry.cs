namespace Orchestra.Engine;

/// <summary>
/// Registry that manages the collection of engine tools available to prompt steps.
/// Engine tools are automatically injected into every prompt step's agent.
/// </summary>
public sealed class EngineToolRegistry
{
	private readonly Dictionary<string, IEngineTool> _tools = new(StringComparer.OrdinalIgnoreCase);

	/// <summary>
	/// Registers an engine tool. Replaces any existing tool with the same name.
	/// </summary>
	public EngineToolRegistry Register(IEngineTool tool)
	{
		_tools[tool.Name] = tool;
		return this;
	}

	/// <summary>
	/// Gets all registered engine tools.
	/// </summary>
	public IReadOnlyCollection<IEngineTool> GetAll() => _tools.Values;

	/// <summary>
	/// Tries to get an engine tool by name.
	/// </summary>
	public bool TryGet(string name, out IEngineTool tool)
	{
		return _tools.TryGetValue(name, out tool!);
	}

	/// <summary>
	/// Gets the number of registered tools.
	/// </summary>
	public int Count => _tools.Count;

	/// <summary>
	/// Creates a default registry with all built-in engine tools.
	/// </summary>
	public static EngineToolRegistry CreateDefault()
	{
		return new EngineToolRegistry()
			.Register(new SetStatusTool())
			.Register(new CompleteTool());
	}
}
