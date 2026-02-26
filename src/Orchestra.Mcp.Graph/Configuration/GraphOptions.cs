namespace Orchestra.Mcp.Graph.Configuration;

/// <summary>
/// Configuration options for Microsoft Graph API authentication.
/// Values are read from environment variables.
/// </summary>
public class GraphOptions
{
    /// <summary>
    /// Environment variable name for the client ID.
    /// </summary>
    public const string ClientIdEnvVar = "GRAPH_CLIENT_ID";

    /// <summary>
    /// Environment variable name for the tenant ID.
    /// </summary>
    public const string TenantIdEnvVar = "GRAPH_TENANT_ID";

    /// <summary>
    /// Environment variable name for the redirect URI.
    /// </summary>
    public const string RedirectUriEnvVar = "GRAPH_REDIRECT_URI";

    /// <summary>
    /// Environment variable name for the token cache path.
    /// </summary>
    public const string TokenCachePathEnvVar = "GRAPH_TOKEN_CACHE_PATH";

    /// <summary>
    /// Default values matching the Python implementation.
    /// </summary>
    public const string DefaultClientId = "ba081686-5d24-4bc6-a0d6-d034ecffed87";
    public const string DefaultTenantId = "72f988bf-86f1-41af-91ab-2d7cd011db47";
    public const string DefaultRedirectUri = "http://localhost:8400";
    public const int DefaultOAuthPort = 8400;

    /// <summary>
    /// Azure AD application (client) ID.
    /// </summary>
    public string ClientId { get; set; } = Environment.GetEnvironmentVariable(ClientIdEnvVar) ?? DefaultClientId;

    /// <summary>
    /// Azure AD tenant ID.
    /// </summary>
    public string TenantId { get; set; } = Environment.GetEnvironmentVariable(TenantIdEnvVar) ?? DefaultTenantId;

    /// <summary>
    /// OAuth redirect URI for interactive authentication.
    /// </summary>
    public string RedirectUri { get; set; } = Environment.GetEnvironmentVariable(RedirectUriEnvVar) ?? DefaultRedirectUri;

    /// <summary>
    /// Port for the OAuth callback server.
    /// </summary>
    public int OAuthPort => int.TryParse(new Uri(RedirectUri).Port.ToString(), out var port) ? port : DefaultOAuthPort;

    /// <summary>
    /// Path to the token cache file.
    /// </summary>
    public string TokenCachePath { get; set; } = Environment.GetEnvironmentVariable(TokenCachePathEnvVar)
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".graph_token.json");

    /// <summary>
    /// Microsoft Graph API base URL.
    /// </summary>
    public string GraphBaseUrl { get; set; } = "https://graph.microsoft.com/v1.0";

    /// <summary>
    /// Microsoft Graph API beta URL (for Copilot).
    /// </summary>
    public string GraphBetaUrl { get; set; } = "https://graph.microsoft.com/beta";

    /// <summary>
    /// Azure AD authorization endpoint.
    /// </summary>
    public string AuthorizeEndpoint => $"https://login.microsoftonline.com/{TenantId}/oauth2/v2.0/authorize";

    /// <summary>
    /// Azure AD token endpoint.
    /// </summary>
    public string TokenEndpoint => $"https://login.microsoftonline.com/{TenantId}/oauth2/v2.0/token";

    /// <summary>
    /// Timeout for OAuth callback server (in seconds).
    /// </summary>
    public int OAuthTimeoutSeconds { get; set; } = 120;
}
