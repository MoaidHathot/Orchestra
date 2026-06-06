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

	/// <summary>
	/// Probes the tool list of every requested global MCP (by name) and returns a
	/// map of <c>mcpName → toolCount</c>. The probe queries Orchestra's own in-process
	/// MCP proxy — exactly the same surface the Copilot SDK / LLM will see — so the
	/// returned counts match what the model would actually find via <c>tools/list</c>.
	/// <para>
	/// Used by the executor to detect the "MCP connected but no tools" race that the
	/// SDK's <c>SessionMcpServersLoadedEvent</c> alone cannot surface (the SDK only
	/// reports transport-level status: <c>"Connected"</c> says nothing about whether
	/// <c>tools/list</c> returned tools).
	/// </para>
	/// <para>
	/// Behavior:
	/// </para>
	/// <list type="bullet">
	///   <item>Returns a count (≥ 0) only for names that match a globally registered MCP
	///   AND that the implementation was able to probe within the timeout.</item>
	///   <item>Omits (or maps to <see langword="null"/>) names that are not global MCPs,
	///   are inline MCPs the resolver doesn't manage, or whose probe failed for any
	///   reason. Callers must treat a missing/null entry as "unknown" rather than "zero".</item>
	///   <item>A returned count of <c>0</c> for a Connected MCP is the failure mode the
	///   executor should fail-fast on when the step explicitly requires that MCP.</item>
	/// </list>
	/// <para>
	/// The default implementation returns an empty dictionary — resolvers that do not
	/// own an in-process proxy (test doubles, no-op resolvers) opt out simply by not
	/// overriding this method.
	/// </para>
	/// </summary>
	/// <param name="mcpNames">The names of MCPs the step depends on (typically <c>step.Mcps.Select(m => m.Name)</c>).</param>
	/// <param name="cancellationToken">Cancellation token. Implementations should also apply their own
	/// short internal timeout per probe so a single slow backend cannot stall the step.</param>
	Task<IReadOnlyDictionary<string, int?>> GetGlobalMcpToolCountsAsync(
		IEnumerable<string> mcpNames,
		CancellationToken cancellationToken = default)
		=> Task.FromResult<IReadOnlyDictionary<string, int?>>(
			new Dictionary<string, int?>(StringComparer.OrdinalIgnoreCase));
}
