using System.Diagnostics;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;
using System.Web;
using Microsoft.Extensions.Logging;
using Orchestra.Mcp.Graph.Configuration;

namespace Orchestra.Mcp.Graph.Authentication;

/// <summary>
/// OAuth token response from Azure AD.
/// </summary>
public class OAuthTokenResponse
{
    [JsonPropertyName("access_token")]
    public string? AccessToken { get; set; }

    [JsonPropertyName("refresh_token")]
    public string? RefreshToken { get; set; }

    [JsonPropertyName("expires_in")]
    public int? ExpiresIn { get; set; }

    [JsonPropertyName("token_type")]
    public string? TokenType { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("error_description")]
    public string? ErrorDescription { get; set; }
}

/// <summary>
/// Provides OAuth tokens via interactive browser authentication.
/// Used for reading messages, mail, etc.
/// </summary>
public class OAuthTokenProvider
{
    private readonly GraphOptions _options;
    private readonly TokenCache _tokenCache;
    private readonly HttpClient _httpClient;
    private readonly ILogger<OAuthTokenProvider> _logger;

    private string? _accessToken;
    private string? _refreshToken;
    private DateTime _tokenExpiry = DateTime.MinValue;

    public OAuthTokenProvider(
        GraphOptions options,
        TokenCache tokenCache,
        HttpClient httpClient,
        ILogger<OAuthTokenProvider> logger)
    {
        _options = options;
        _tokenCache = tokenCache;
        _httpClient = httpClient;
        _logger = logger;

        // Load cached tokens
        LoadCachedTokens();
    }

    private void LoadCachedTokens()
    {
        var cached = _tokenCache.Load();
        if (cached != null)
        {
            _accessToken = cached.AccessToken;
            _refreshToken = cached.RefreshToken;
            _logger.LogDebug("Loaded cached OAuth tokens");
        }
    }

    private void SaveTokens()
    {
        _tokenCache.Save(new CachedTokenData
        {
            AccessToken = _accessToken,
            RefreshToken = _refreshToken
        });
    }

    /// <summary>
    /// Gets the current access token, refreshing if necessary.
    /// </summary>
    public async Task<string?> GetTokenAsync(CancellationToken cancellationToken = default)
    {
        // Return cached token if still valid
        if (_accessToken != null && DateTime.UtcNow < _tokenExpiry.AddMinutes(-5))
        {
            return _accessToken;
        }

        // Try to refresh if we have a refresh token
        if (_refreshToken != null)
        {
            if (await RefreshTokenAsync(cancellationToken))
            {
                return _accessToken;
            }
        }

        return _accessToken;
    }

    /// <summary>
    /// Checks if a token is available (may need refresh).
    /// </summary>
    public bool HasToken => _accessToken != null || _refreshToken != null;

    /// <summary>
    /// Validates the current token by making a test API call.
    /// </summary>
    public async Task<bool> ValidateTokenAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(_accessToken))
        {
            return false;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{_options.GraphBaseUrl}/me");
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _accessToken);

            var response = await _httpClient.SendAsync(request, cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Refreshes the access token using the refresh token.
    /// </summary>
    public async Task<bool> RefreshTokenAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(_refreshToken))
        {
            return false;
        }

        try
        {
            var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = _options.ClientId,
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = _refreshToken,
                ["scope"] = GraphScopes.OAuthScopesString
            });

            var response = await _httpClient.PostAsync(_options.TokenEndpoint, content, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                var tokenResponse = await response.Content.ReadFromJsonAsync<OAuthTokenResponse>(cancellationToken);
                if (tokenResponse?.AccessToken != null)
                {
                    _accessToken = tokenResponse.AccessToken;
                    _refreshToken = tokenResponse.RefreshToken ?? _refreshToken;
                    _tokenExpiry = DateTime.UtcNow.AddSeconds(tokenResponse.ExpiresIn ?? 3600);
                    SaveTokens();
                    _logger.LogDebug("Successfully refreshed OAuth token");
                    return true;
                }
            }

            var error = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogWarning("Token refresh failed: {Error}", error);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to refresh OAuth token");
            return false;
        }
    }

    /// <summary>
    /// Authenticates interactively via browser.
    /// </summary>
    public async Task<bool> AuthenticateAsync(bool force = false, CancellationToken cancellationToken = default)
    {
        // Check if existing token is valid
        if (!force && _accessToken != null)
        {
            if (await ValidateTokenAsync(cancellationToken))
            {
                _logger.LogDebug("Existing OAuth token is valid");
                return true;
            }
        }

        // Try refresh first
        if (!force && await RefreshTokenAsync(cancellationToken))
        {
            return true;
        }

        // Interactive browser auth
        _logger.LogInformation("Starting interactive browser authentication...");

        var authUrl = BuildAuthorizationUrl();

        // Start callback handler
        using var callbackHandler = new OAuthCallbackHandler(_options.OAuthPort);

        // Open browser
        OpenBrowser(authUrl);

        // Wait for callback
        var callbackResult = await callbackHandler.WaitForCallbackAsync(
            TimeSpan.FromSeconds(_options.OAuthTimeoutSeconds),
            cancellationToken);

        if (!callbackResult.Success)
        {
            _logger.LogError("OAuth callback failed: {Error}", callbackResult.Error);
            return false;
        }

        // Exchange code for token
        return await ExchangeCodeForTokenAsync(callbackResult.AuthCode!, cancellationToken);
    }

    private string BuildAuthorizationUrl()
    {
        var queryParams = HttpUtility.ParseQueryString(string.Empty);
        queryParams["client_id"] = _options.ClientId;
        queryParams["response_type"] = "code";
        queryParams["redirect_uri"] = _options.RedirectUri;
        queryParams["scope"] = GraphScopes.OAuthScopesString;
        queryParams["response_mode"] = "query";

        return $"{_options.AuthorizeEndpoint}?{queryParams}";
    }

    private static void OpenBrowser(string url)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            Process.Start("open", url);
        }
        else
        {
            Process.Start("xdg-open", url);
        }
    }

    private async Task<bool> ExchangeCodeForTokenAsync(string authCode, CancellationToken cancellationToken)
    {
        try
        {
            var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = _options.ClientId,
                ["grant_type"] = "authorization_code",
                ["code"] = authCode,
                ["redirect_uri"] = _options.RedirectUri,
                ["scope"] = GraphScopes.OAuthScopesString
            });

            var response = await _httpClient.PostAsync(_options.TokenEndpoint, content, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                var tokenResponse = await response.Content.ReadFromJsonAsync<OAuthTokenResponse>(cancellationToken);
                if (tokenResponse?.AccessToken != null)
                {
                    _accessToken = tokenResponse.AccessToken;
                    _refreshToken = tokenResponse.RefreshToken;
                    _tokenExpiry = DateTime.UtcNow.AddSeconds(tokenResponse.ExpiresIn ?? 3600);
                    SaveTokens();
                    _logger.LogInformation("OAuth authentication successful");
                    return true;
                }
            }

            var error = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError("Token exchange failed: {Error}", error);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to exchange authorization code for token");
            return false;
        }
    }

    /// <summary>
    /// Clears all cached tokens.
    /// </summary>
    public void ClearCache()
    {
        _accessToken = null;
        _refreshToken = null;
        _tokenExpiry = DateTime.MinValue;
        _tokenCache.Clear();
    }
}
