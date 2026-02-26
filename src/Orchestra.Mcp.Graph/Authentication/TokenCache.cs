using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Orchestra.Mcp.Graph.Configuration;

namespace Orchestra.Mcp.Graph.Authentication;

/// <summary>
/// Cached token data structure.
/// </summary>
public class CachedTokenData
{
    [JsonPropertyName("access_token")]
    public string? AccessToken { get; set; }

    [JsonPropertyName("refresh_token")]
    public string? RefreshToken { get; set; }
}

/// <summary>
/// Handles token caching to disk.
/// </summary>
public class TokenCache
{
    private readonly string _cachePath;
    private readonly ILogger<TokenCache> _logger;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true
    };

    public TokenCache(GraphOptions options, ILogger<TokenCache> logger)
    {
        _cachePath = options.TokenCachePath;
        _logger = logger;
    }

    /// <summary>
    /// Loads cached token data from disk.
    /// </summary>
    public CachedTokenData? Load()
    {
        try
        {
            if (!File.Exists(_cachePath))
            {
                _logger.LogDebug("Token cache file does not exist: {Path}", _cachePath);
                return null;
            }

            var json = File.ReadAllText(_cachePath);
            var data = JsonSerializer.Deserialize<CachedTokenData>(json);
            _logger.LogDebug("Loaded token cache from {Path}", _cachePath);
            return data;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load token cache from {Path}", _cachePath);
            return null;
        }
    }

    /// <summary>
    /// Saves token data to disk.
    /// </summary>
    public void Save(CachedTokenData data)
    {
        try
        {
            var directory = Path.GetDirectoryName(_cachePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(data, _jsonOptions);
            File.WriteAllText(_cachePath, json);
            _logger.LogDebug("Saved token cache to {Path}", _cachePath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save token cache to {Path}", _cachePath);
        }
    }

    /// <summary>
    /// Clears the token cache.
    /// </summary>
    public void Clear()
    {
        try
        {
            if (File.Exists(_cachePath))
            {
                File.Delete(_cachePath);
                _logger.LogDebug("Cleared token cache at {Path}", _cachePath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to clear token cache at {Path}", _cachePath);
        }
    }
}
