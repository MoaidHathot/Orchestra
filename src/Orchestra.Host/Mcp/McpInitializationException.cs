namespace Orchestra.Host.Mcp;

/// <summary>
/// A global MCP backend could not be validated before the host started.
/// Retains the backend identity and original transport, authentication, or process failure.
/// </summary>
public sealed class McpInitializationException(string serverName, string target, Exception innerException)
	: Exception($"MCP server '{serverName}' failed startup validation ({target}): {innerException.Message}", innerException)
{
	public string ServerName { get; } = serverName;
	public string Target { get; } = target;
}
