using Microsoft.Extensions.Logging;

namespace Orchestra.Mcp.Graph.Authentication;

/// <summary>
/// Dual-token provider that manages both Azure CLI and OAuth tokens.
/// Provides the appropriate token based on the operation type.
/// </summary>
public class DualTokenProvider : ITokenProvider
{
    private readonly AzureCliTokenProvider _azureCliProvider;
    private readonly OAuthTokenProvider _oauthProvider;
    private readonly ILogger<DualTokenProvider> _logger;

    public DualTokenProvider(
        AzureCliTokenProvider azureCliProvider,
        OAuthTokenProvider oauthProvider,
        ILogger<DualTokenProvider> logger)
    {
        _azureCliProvider = azureCliProvider;
        _oauthProvider = oauthProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<string?> GetTokenAsync(TokenType tokenType, CancellationToken cancellationToken = default)
    {
        return tokenType switch
        {
            TokenType.AzureCli => await _azureCliProvider.GetTokenAsync(cancellationToken),
            TokenType.OAuth => await _oauthProvider.GetTokenAsync(cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(tokenType))
        };
    }

    /// <inheritdoc />
    public async Task<bool> AuthenticateAsync(bool force = false, CancellationToken cancellationToken = default)
    {
        var oauthSuccess = await _oauthProvider.AuthenticateAsync(force, cancellationToken);

        if (!oauthSuccess)
        {
            _logger.LogWarning("OAuth authentication failed");
            return false;
        }

        // Also try to get Azure CLI token (non-blocking, optional)
        var azToken = await _azureCliProvider.GetTokenAsync(cancellationToken);
        if (azToken == null)
        {
            _logger.LogWarning("Azure CLI token not available. Some operations (listing teams/channels) may fail.");
        }
        else
        {
            _logger.LogDebug("Azure CLI token available");
        }

        return true;
    }

    /// <inheritdoc />
    public bool HasToken(TokenType tokenType)
    {
        return tokenType switch
        {
            TokenType.AzureCli => _azureCliProvider.HasToken,
            TokenType.OAuth => _oauthProvider.HasToken,
            _ => false
        };
    }

    /// <summary>
    /// Gets the Azure CLI token provider directly.
    /// </summary>
    public AzureCliTokenProvider AzureCliProvider => _azureCliProvider;

    /// <summary>
    /// Gets the OAuth token provider directly.
    /// </summary>
    public OAuthTokenProvider OAuthProvider => _oauthProvider;
}
