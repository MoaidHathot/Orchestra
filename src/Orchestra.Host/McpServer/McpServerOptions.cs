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

	/// <summary>
	/// Catch-all default transport timeout (seconds) for MCP tool calls to servers that
	/// are NOT the Orchestra data plane (see <see cref="DefaultOrchestraInvokeTimeoutSeconds"/>
	/// for the data-plane-specific knob). Applied by <c>McpManager.Resolve</c> to any MCP
	/// whose <c>Timeout</c> is still <c>null</c> after orchestration-level and global-MCP-level
	/// resolution.
	/// <para>
	/// <c>null</c> (the default) means: do not stamp any default — the Copilot MCP SDK's
	/// built-in ~3-minute timeout remains in effect. This preserves backward-compatible
	/// behavior for existing orchestrations.<br/>
	/// <c>0</c> means: stamp an effectively-infinite transport timeout
	/// (<c>TimeSpan.FromMilliseconds(int.MaxValue)</c>) so the SDK's 180-second cliff is
	/// bypassed entirely. Use this when server-side and orchestration-side deadlines should
	/// be the only authority. Mirrors the semantics of
	/// <see cref="DefaultOrchestraInvokeTimeoutSeconds"/>.<br/>
	/// A positive number means: stamp that many seconds onto every non-data-plane MCP that
	/// doesn't already carry an override from the orchestration's <c>mcps[].timeoutSeconds</c>.
	/// </para>
	/// <para>
	/// A per-orchestration <c>mcps[].timeoutSeconds</c> always wins over this catch-all default,
	/// just like for the data-plane knob.
	/// </para>
	/// </summary>
	public int? DefaultMcpToolCallTimeoutSeconds { get; set; } = null;

	/// <summary>
	/// Default value used by <c>invoke_orchestration</c>'s sync-mode <c>timeoutSeconds</c>
	/// argument when the LLM caller doesn't supply one. Has no effect on async invocations
	/// (which don't wait for completion) or on calls that explicitly pass <c>timeoutSeconds</c>.
	/// <para>
	/// Default: <c>300</c> seconds (5 minutes) — matches the prior hardcoded value so
	/// existing orchestrations keep working without configuration changes.
	/// </para>
	/// </summary>
	public int DefaultInvokeOrchestrationSyncTimeoutSeconds { get; set; } = 300;

	/// <summary>
	/// Per-server timeout (seconds) applied by <c>McpManager.GetGlobalMcpToolCountsAsync</c>
	/// when probing a global MCP's <c>tools/list</c> at step start to detect the
	/// "Connected but zero tools" failure mode that the Copilot SDK's
	/// <c>SessionMcpServersLoadedEvent</c> cannot surface.
	/// <para>
	/// The probe is intentionally short: a single slow backend must not stall the
	/// step start. On a healthy local proxy the probe completes in &lt; 50ms; on a
	/// backend stuck in interactive-browser auth (the typical reproducer) the probe
	/// times out and the step proceeds with "tool count unknown" so the SDK-status
	/// fast-fail remains the safety net. Set to a small positive value; values
	/// &lt; 1 are clamped to 1.
	/// </para>
	/// <para>Default: 5 seconds.</para>
	/// </summary>
	public int ToolDiscoveryProbeTimeoutSeconds { get; set; } = 5;

	/// <summary>
	/// Per-MCP timeout (seconds) applied by
	/// <c>McpManager.ProbeEndpointReachabilityAsync</c> when TCP-probing a remote
	/// MCP endpoint to distinguish "backend offline" from "backend reachable but
	/// returned zero tools" in pre-flight error messages.
	/// <para>
	/// Smaller than <see cref="ToolDiscoveryProbeTimeoutSeconds"/> on purpose: the
	/// reachability probe only runs on the error path (after the tool-count probe
	/// already returned 0), and stacking another long timeout on top of an
	/// already-failed step start would penalise the user with extra latency before
	/// the error reaches them. Values &lt; 1 are clamped to 1.
	/// </para>
	/// <para>Default: 2 seconds.</para>
	/// </summary>
	public int EndpointReachabilityProbeTimeoutSeconds { get; set; } = 2;
}
