using Orchestra.Engine;

namespace Orchestra.OpenCode;

/// <summary>
/// Per-instance holder for the engine tools + <see cref="EngineToolContext"/> of the prompt
/// step currently leasing an OpenCode worker. The loopback MCP bridge reads this to dispatch
/// <c>orchestra_*</c> tool calls to the right step. Because the default pool config gives each
/// step its own instance (<c>MaxSessionsPerInstance = 1</c>), there is at most one active
/// binding per worker at a time.
/// </summary>
public sealed class EngineToolContextHolder
{
	private volatile EngineToolBinding? _current;

	/// <summary>The active binding, or null when the worker is idle / between leases.</summary>
	public EngineToolBinding? Current => _current;

	public void Set(IReadOnlyCollection<IEngineTool> tools, EngineToolContext context)
		=> _current = new EngineToolBinding(tools, context);

	public void Clear() => _current = null;
}

/// <summary>The engine tools and shared context for one in-flight prompt step.</summary>
public sealed record EngineToolBinding(IReadOnlyCollection<IEngineTool> Tools, EngineToolContext Context);
