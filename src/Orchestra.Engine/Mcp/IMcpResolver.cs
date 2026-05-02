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
	/// <param name="mcps">Step MCPs to resolve.</param>
	/// <param name="parent">
	/// Optional parent-execution metadata. When supplied, implementations should stamp HTTP
	/// headers listed on <see cref="OrchestraHeaders"/> onto any resolved <see cref="RemoteMcp"/>
	/// entries that target Orchestra's own server endpoints. The headers let server-side MCP
	/// tool handlers (e.g. <c>DataPlaneTools.InvokeOrchestration</c>) auto-populate
	/// <c>parentExecutionId</c> for nested invocations, restoring run lineage that was
	/// previously lost when an LLM agent recursively invoked orchestrations through MCP.
	/// When <c>null</c>, MCPs are returned without parent-execution headers.
	/// </param>
	Mcp[] Resolve(Mcp[] mcps, ParentExecutionAnnotation? parent = null);
}
