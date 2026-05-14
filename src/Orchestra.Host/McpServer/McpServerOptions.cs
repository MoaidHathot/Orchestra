namespace Orchestra.Host.McpServer;

/// <summary>
/// Configuration options for Orchestra's MCP server endpoints.
/// </summary>
public class McpServerOptions
{
	/// <summary>
	/// Whether the data-plane MCP server is enabled.
	/// The data plane provides tools for listing and invoking orchestrations.
	/// Default: true (enabled).
	/// </summary>
	public bool DataPlaneEnabled { get; set; } = true;

	/// <summary>
	/// Route path for the data-plane MCP endpoint.
	/// Default: "/mcp/data".
	/// </summary>
	public string DataPlaneRoute { get; set; } = "/mcp/data";

	/// <summary>
	/// Whether the control-plane MCP server is enabled.
	/// The control plane provides tools for managing orchestrations, profiles, tags, and triggers.
	/// Default: false (disabled, opt-in).
	/// </summary>
	public bool ControlPlaneEnabled { get; set; } = false;

	/// <summary>
	/// Route path for the control-plane MCP endpoint.
	/// Default: "/mcp/control".
	/// </summary>
	public string ControlPlaneRoute { get; set; } = "/mcp/control";

	/// <summary>
	/// Maximum nesting depth for orchestration-to-orchestration invocations.
	/// 0 = top-level only (no nesting), 5 = up to 5 levels of nesting.
	/// Default: 5.
	/// </summary>
	public int MaxNestingDepth { get; set; } = 5;

	/// <summary>
	/// Default timeout (seconds) applied to MCP tool calls that target Orchestra's
	/// own data-plane MCP endpoint when the orchestration YAML/JSON does not specify
	/// a <c>timeoutSeconds</c> on the matching <c>mcps[]</c> entry.
	/// <para>
	/// Set to <c>0</c> (the default) to disable any client-side transport timeout for the
	/// Orchestra data plane. The server-side engine already enforces its own deadlines
	/// (orchestration <c>timeoutSeconds</c>, step <c>timeoutSeconds</c>, and the sync-invoke
	/// timeout passed to <c>invoke_orchestration</c>); a separate transport timeout adds
	/// nothing but creates the well-known failure mode where a long-running sync invoke is
	/// aborted by the MCP transport with a generic <c>"cancelled by caller"</c> reason
	/// before the engine's own timeout fires.
	/// </para>
	/// <para>
	/// Authors who want belt-and-suspenders client-side limits can still set
	/// <c>mcps[].timeoutSeconds</c> per orchestration; that value always wins. When this
	/// option is non-zero and a sync <c>invoke_orchestration</c> call requests a
	/// <c>timeoutSeconds</c> larger than the configured transport limit,
	/// <c>DataPlaneTools.InvokeOrchestration</c> returns a structured error explaining the
	/// mismatch instead of letting the transport abort silently.
	/// </para>
	/// <para>Default: 0 (no client-side transport timeout — server-side timeouts are
	/// authoritative).</para>
	/// </summary>
	public int DefaultOrchestraInvokeTimeoutSeconds { get; set; } = 0;
}
