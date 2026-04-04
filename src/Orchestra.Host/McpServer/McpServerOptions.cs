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
}
