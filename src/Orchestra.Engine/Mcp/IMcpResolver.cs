namespace Orchestra.Engine;

/// <summary>
/// Resolves MCP configurations for a step, allowing shared/global MCPs
/// to be replaced with proxy endpoints while passing through inline MCPs unchanged.
/// </summary>
public interface IMcpResolver
{
	/// <summary>
	/// Resolves the given MCPs, replacing any globally managed MCPs with their
	/// remote proxy endpoints. Non-global MCPs are returned unchanged.
	/// </summary>
	Mcp[] Resolve(Mcp[] mcps);
}
