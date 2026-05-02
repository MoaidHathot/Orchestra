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
	/// Long-running tools such as <c>invoke_orchestration</c> in sync mode commonly
	/// exceed the Copilot SDK's ~3-minute default MCP request timeout, so this default
	/// is intentionally generous. Authors can still override per call by setting
	/// <c>timeoutSeconds</c> on the <c>mcps[]</c> entry (any non-null value wins),
	/// and per-invocation deadlines on the data-plane <c>invoke_orchestration</c> tool
	/// itself (<c>request.timeoutSeconds</c>) continue to apply on the server side.
	/// </para>
	/// <para>Default: 1800 (30 minutes). Set to 0 or a negative value to disable the
	/// default and fall back to the Copilot SDK's built-in default.</para>
	/// </summary>
	public int DefaultOrchestraInvokeTimeoutSeconds { get; set; } = 1800;
}
