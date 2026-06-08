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

	/// <summary>
	/// Probes the network reachability of every requested global MCP (by name) and
	/// returns a map of <c>mcpName → reachability</c>. Implementations must probe the
	/// <em>upstream</em> backend endpoint (as originally configured), not Orchestra's
	/// in-process proxy wrapper — otherwise the probe would always report "reachable"
	/// because Orchestra's own process is up by definition.
	/// <para>
	/// This is a complementary signal to <see cref="GetGlobalMcpToolCountsAsync"/>:
	/// when the tool-count probe returns <c>0</c>, the caller can use the reachability
	/// result to distinguish the two very different causes of "0 tools" —
	/// </para>
	/// <list type="bullet">
	///   <item><see cref="McpEndpointReachabilityStatus.Unreachable"/>: the backend MCP
	///   process is not running / the endpoint refuses connections. Action: start the
	///   backend.</item>
	///   <item><see cref="McpEndpointReachabilityStatus.Reachable"/>: the backend is up
	///   but returned an empty tool list. Action: check authentication / deferred-connection
	///   state.</item>
	/// </list>
	/// <para>
	/// Local stdio MCPs map to <see cref="McpEndpointReachabilityStatus.LocalStdio"/>
	/// because TCP probing isn't applicable. Names that don't match a globally managed
	/// MCP map to <see cref="McpEndpointReachabilityStatus.Unknown"/>. The default
	/// implementation returns an empty dictionary so resolvers without an MCP registry
	/// (test doubles, no-op resolvers) opt out without overriding.
	/// </para>
	/// </summary>
	/// <param name="mcpNames">The names of MCPs to probe (typically those that just
	/// returned 0 tools from <see cref="GetGlobalMcpToolCountsAsync"/>).</param>
	/// <param name="cancellationToken">Cancellation token. Implementations should apply
	/// a short internal timeout per probe so a single unreachable backend cannot stall
	/// the diagnostic.</param>
	Task<IReadOnlyDictionary<string, McpEndpointReachability>> ProbeEndpointReachabilityAsync(
		IEnumerable<string> mcpNames,
		CancellationToken cancellationToken = default)
		=> Task.FromResult<IReadOnlyDictionary<string, McpEndpointReachability>>(
			new Dictionary<string, McpEndpointReachability>(StringComparer.OrdinalIgnoreCase));
}

/// <summary>
/// Result of an <see cref="IMcpResolver.ProbeEndpointReachabilityAsync"/> call for a
/// single MCP name. Carries the status discriminator plus optional context for the
/// diagnostic message (the endpoint that was probed and, on failure, a brief reason).
/// </summary>
/// <param name="Status">Probe outcome category.</param>
/// <param name="Endpoint">For <see cref="McpEndpointReachabilityStatus.Reachable"/>
/// and <see cref="McpEndpointReachabilityStatus.Unreachable"/>, the upstream URL that
/// was probed (e.g. <c>http://localhost:5113/mcp/m365-copilot</c>). Null for local
/// stdio or unknown MCPs.</param>
/// <param name="FailureReason">For <see cref="McpEndpointReachabilityStatus.Unreachable"/>,
/// a short human-readable description of why the connect attempt failed
/// (e.g. <c>"connection refused"</c>, <c>"timed out after 1s"</c>). Null otherwise.</param>
public sealed record McpEndpointReachability(
	McpEndpointReachabilityStatus Status,
	string? Endpoint = null,
	string? FailureReason = null);

/// <summary>
/// Categorical outcome of an MCP endpoint reachability probe.
/// </summary>
public enum McpEndpointReachabilityStatus
{
	/// <summary>The MCP name is not registered as a globally managed MCP, so the
	/// resolver could not look up an endpoint to probe.</summary>
	Unknown,

	/// <summary>The MCP is a local stdio backend. TCP probing is not applicable
	/// because the process is launched on demand rather than listening on a port.</summary>
	LocalStdio,

	/// <summary>The upstream remote endpoint accepted a TCP connection within the
	/// probe timeout.</summary>
	Reachable,

	/// <summary>The upstream remote endpoint refused the TCP connection, timed out,
	/// or threw an error. The endpoint host:port is most likely not listening.</summary>
	Unreachable,
}
