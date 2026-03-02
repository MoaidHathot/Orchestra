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
public partial class TokenCache
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
				LogTokenCacheFileDoesNotExist(_cachePath);
				return null;
			}

			var json = File.ReadAllText(_cachePath);
			var data = JsonSerializer.Deserialize<CachedTokenData>(json);
			LogLoadedTokenCache(_cachePath);
			return data;
		}
		catch (Exception ex)
		{
			LogFailedToLoadTokenCache(ex, _cachePath);
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
			LogSavedTokenCache(_cachePath);
		}
		catch (Exception ex)
		{
			LogFailedToSaveTokenCache(ex, _cachePath);
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
				LogClearedTokenCache(_cachePath);
			}
		}
		catch (Exception ex)
		{
			LogFailedToClearTokenCache(ex, _cachePath);
		}
	}

	#region Source-Generated Logging

	[LoggerMessage(Level = LogLevel.Debug, Message = "Token cache file does not exist: {Path}")]
	private partial void LogTokenCacheFileDoesNotExist(string path);

	[LoggerMessage(Level = LogLevel.Debug, Message = "Loaded token cache from {Path}")]
	private partial void LogLoadedTokenCache(string path);

	[LoggerMessage(Level = LogLevel.Warning, Message = "Failed to load token cache from {Path}")]
	private partial void LogFailedToLoadTokenCache(Exception ex, string path);

	[LoggerMessage(Level = LogLevel.Debug, Message = "Saved token cache to {Path}")]
	private partial void LogSavedTokenCache(string path);

	[LoggerMessage(Level = LogLevel.Warning, Message = "Failed to save token cache to {Path}")]
	private partial void LogFailedToSaveTokenCache(Exception ex, string path);

	[LoggerMessage(Level = LogLevel.Debug, Message = "Cleared token cache at {Path}")]
	private partial void LogClearedTokenCache(string path);

	[LoggerMessage(Level = LogLevel.Warning, Message = "Failed to clear token cache at {Path}")]
	private partial void LogFailedToClearTokenCache(Exception ex, string path);

	#endregion
}
