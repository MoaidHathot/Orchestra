using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using Orchestra.Mcp.Graph.Services;

namespace Orchestra.Mcp.Graph.Tools;

/// <summary>
/// MCP tools for user-related Graph API operations.
/// </summary>
[McpServerToolType]
public class UserTools
{
    private readonly GraphApiClient _graphClient;

    public UserTools(GraphApiClient graphClient)
    {
        _graphClient = graphClient;
    }

    [McpServerTool(Name = "get_me")]
    [Description("Get current user information including display name, email, and job title")]
    public async Task<string> GetMe(CancellationToken cancellationToken = default)
    {
        var result = await _graphClient.GetAsync(
            "/me",
            useAzureCli: true,
            cancellationToken: cancellationToken);

        return result?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) ?? "{}";
    }

    [McpServerTool(Name = "authenticate")]
    [Description("Authenticate with Microsoft Graph API using interactive browser flow. Required before using other tools.")]
    public async Task<string> Authenticate(
        [Description("Force re-authentication even if token exists")] bool force = false,
        CancellationToken cancellationToken = default)
    {
        var success = await _graphClient.AuthenticateAsync(force, cancellationToken);

        return success
            ? "Authentication successful. You can now use other Graph API tools."
            : "Authentication failed. Please check your credentials and try again.";
    }
}
