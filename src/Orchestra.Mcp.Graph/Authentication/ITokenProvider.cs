namespace Orchestra.Mcp.Graph.Authentication;

/// <summary>
/// Represents the type of token to use for Graph API calls.
/// </summary>
public enum TokenType
{
    /// <summary>
    /// OAuth token from interactive browser authentication.
    /// Used for reading messages, mail, etc.
    /// </summary>
    OAuth,

    /// <summary>
    /// Token from Azure CLI.
    /// Used for listing teams, channels, and other operations requiring broader permissions.
    /// </summary>
    AzureCli
}

/// <summary>
/// Provides access tokens for Microsoft Graph API.
/// </summary>
public interface ITokenProvider
{
    /// <summary>
    /// Gets an access token for the specified token type.
    /// </summary>
    /// <param name="tokenType">The type of token to retrieve.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The access token, or null if not available.</returns>
    Task<string?> GetTokenAsync(TokenType tokenType, CancellationToken cancellationToken = default);

    /// <summary>
    /// Authenticates interactively using the browser.
    /// </summary>
    /// <param name="force">If true, forces re-authentication even if a valid token exists.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if authentication was successful.</returns>
    Task<bool> AuthenticateAsync(bool force = false, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks if a token of the specified type is available.
    /// </summary>
    /// <param name="tokenType">The type of token to check.</param>
    /// <returns>True if a token is available.</returns>
    bool HasToken(TokenType tokenType);
}
