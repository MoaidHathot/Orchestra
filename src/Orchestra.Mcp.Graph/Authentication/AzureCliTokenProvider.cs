using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace Orchestra.Mcp.Graph.Authentication;

/// <summary>
/// Provides access tokens from Azure CLI.
/// Used for operations requiring broader permissions (Group.ReadWrite.All).
/// </summary>
public partial class AzureCliTokenProvider
{
    private readonly ILogger<AzureCliTokenProvider> _logger;
    private string? _cachedToken;
    private DateTime _tokenExpiry = DateTime.MinValue;

    public AzureCliTokenProvider(ILogger<AzureCliTokenProvider> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Gets an access token from Azure CLI.
    /// </summary>
    public async Task<string?> GetTokenAsync(CancellationToken cancellationToken = default)
    {
        // Return cached token if still valid (with 5 minute buffer)
        if (_cachedToken != null && DateTime.UtcNow < _tokenExpiry.AddMinutes(-5))
        {
            return _cachedToken;
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "cmd.exe" : "az",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                startInfo.Arguments = "/c az account get-access-token --resource https://graph.microsoft.com --query accessToken -o tsv";
            }
            else
            {
                startInfo.Arguments = "account get-access-token --resource https://graph.microsoft.com --query accessToken -o tsv";
            }

            using var process = new Process { StartInfo = startInfo };
            process.Start();

            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);

            await process.WaitForExitAsync(cancellationToken);

            if (process.ExitCode == 0)
            {
                var token = (await outputTask).Trim();
                if (!string.IsNullOrEmpty(token))
                {
                    _cachedToken = token;
                    // Azure CLI tokens typically last 1 hour
				_tokenExpiry = DateTime.UtcNow.AddHours(1);
					LogSuccessfullyObtainedAzureCliToken();
					return token;
                }
            }

		var error = await errorTask;
			LogAzureCliTokenRequestFailed(error);
			return null;
		}
		catch (Exception ex)
		{
			LogFailedToGetAzureCliToken(ex);
			return null;
		}
    }

    /// <summary>
    /// Checks if a token is available.
    /// </summary>
    public bool HasToken => _cachedToken != null && DateTime.UtcNow < _tokenExpiry.AddMinutes(-5);

	/// <summary>
	/// Clears the cached token.
	/// </summary>
	public void ClearCache()
	{
		_cachedToken = null;
		_tokenExpiry = DateTime.MinValue;
	}

	#region Source-Generated Logging

	[LoggerMessage(Level = LogLevel.Debug, Message = "Successfully obtained Azure CLI token")]
	private partial void LogSuccessfullyObtainedAzureCliToken();

	[LoggerMessage(Level = LogLevel.Warning, Message = "Azure CLI token request failed: {Error}")]
	private partial void LogAzureCliTokenRequestFailed(string error);

	[LoggerMessage(Level = LogLevel.Warning, Message = "Failed to get Azure CLI token")]
	private partial void LogFailedToGetAzureCliToken(Exception ex);

	#endregion
}
