using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Orchestra.Mcp.Graph.Authentication;
using Orchestra.Mcp.Graph.Configuration;

namespace Orchestra.Mcp.Graph.Services;

/// <summary>
/// HTTP client wrapper for Microsoft Graph API.
/// </summary>
public partial class GraphApiClient
{
    private readonly HttpClient _httpClient;
    private readonly ITokenProvider _tokenProvider;
    private readonly GraphOptions _options;
    private readonly ILogger<GraphApiClient> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public GraphApiClient(
        HttpClient httpClient,
        ITokenProvider tokenProvider,
        GraphOptions options,
        ILogger<GraphApiClient> logger)
    {
        _httpClient = httpClient;
        _tokenProvider = tokenProvider;
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// Authenticates the client. Required before making API calls.
    /// </summary>
    public Task<bool> AuthenticateAsync(bool force = false, CancellationToken cancellationToken = default)
    {
        return _tokenProvider.AuthenticateAsync(force, cancellationToken);
    }

    /// <summary>
    /// Makes a GET request to the Graph API.
    /// </summary>
    /// <param name="endpoint">The API endpoint (e.g., "/me").</param>
    /// <param name="parameters">Optional query parameters.</param>
    /// <param name="useAzureCli">If true, uses Azure CLI token instead of OAuth.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The JSON response as a JsonNode.</returns>
    public async Task<JsonNode?> GetAsync(
        string endpoint,
        Dictionary<string, string>? parameters = null,
        bool useAzureCli = false,
        CancellationToken cancellationToken = default)
    {
        var tokenType = useAzureCli ? TokenType.AzureCli : TokenType.OAuth;
        var token = await _tokenProvider.GetTokenAsync(tokenType, cancellationToken);

        if (string.IsNullOrEmpty(token))
        {
            var errorMessage = tokenType == TokenType.AzureCli
                ? "No Azure CLI token available. Please run 'az login' in your terminal first, then try again."
                : "No OAuth token available. Please call the 'authenticate' tool first to log in.";
            throw new InvalidOperationException(errorMessage);
        }

        var url = BuildUrl(_options.GraphBaseUrl, endpoint, parameters);

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

		LogGetRequest(url);

        var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<JsonNode>(cancellationToken);
    }

    /// <summary>
    /// Makes a POST request to the Graph API.
    /// </summary>
    public async Task<JsonNode?> PostAsync(
        string endpoint,
        object? body = null,
        bool useBeta = false,
        CancellationToken cancellationToken = default)
    {
        var token = await _tokenProvider.GetTokenAsync(TokenType.OAuth, cancellationToken);

        if (string.IsNullOrEmpty(token))
        {
            throw new InvalidOperationException("No OAuth token available. Please call the 'authenticate' tool first to log in.");
        }

        var baseUrl = useBeta ? _options.GraphBetaUrl : _options.GraphBaseUrl;
        var url = $"{baseUrl}{endpoint}";

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        if (body != null)
        {
            request.Content = JsonContent.Create(body, options: JsonOptions);
        }

		LogPostRequest(url);

        var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<JsonNode>(cancellationToken);
    }

    /// <summary>
    /// Makes paginated GET requests and returns all results.
    /// </summary>
    public async Task<List<JsonNode>> GetAllPagesAsync(
        string endpoint,
        Dictionary<string, string>? parameters = null,
        bool useAzureCli = false,
        int maxResults = int.MaxValue,
        CancellationToken cancellationToken = default)
    {
        var tokenType = useAzureCli ? TokenType.AzureCli : TokenType.OAuth;
        var token = await _tokenProvider.GetTokenAsync(tokenType, cancellationToken);

        if (string.IsNullOrEmpty(token))
        {
            var errorMessage = tokenType == TokenType.AzureCli
                ? "No Azure CLI token available. Please run 'az login' in your terminal first, then try again."
                : "No OAuth token available. Please call the 'authenticate' tool first to log in.";
            throw new InvalidOperationException(errorMessage);
        }

        var results = new List<JsonNode>();
        var url = BuildUrl(_options.GraphBaseUrl, endpoint, parameters);

        while (!string.IsNullOrEmpty(url) && results.Count < maxResults)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

			LogGetPagedRequest(url);

            var response = await _httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();

            var data = await response.Content.ReadFromJsonAsync<JsonNode>(cancellationToken);

            if (data?["value"] is JsonArray valueArray)
            {
                foreach (var item in valueArray)
                {
                    if (item != null && results.Count < maxResults)
                    {
                        results.Add(item.DeepClone());
                    }
                }
            }

            // Check for next page
            var nextLink = data?["@odata.nextLink"]?.GetValue<string>();
            if (string.IsNullOrEmpty(nextLink))
            {
                break;
            }

            url = nextLink;
            // nextLink includes params, so we use it directly
        }

        return results;
    }

    /// <summary>
    /// Gets values from a paged response.
    /// </summary>
    public static List<JsonNode> GetValues(JsonNode? response)
    {
        if (response?["value"] is JsonArray array)
        {
            return [.. array.Where(x => x != null).Select(x => x!.DeepClone())];
        }
        return [];
    }

	private static string BuildUrl(string baseUrl, string endpoint, Dictionary<string, string>? parameters)
	{
		var url = $"{baseUrl}{endpoint}";

		if (parameters != null && parameters.Count > 0)
		{
			var queryString = string.Join("&", parameters.Select(p =>
				$"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}"));
			url += $"?{queryString}";
		}

		return url;
	}

	#region Source-Generated Logging

	[LoggerMessage(Level = LogLevel.Debug, Message = "GET {Url}")]
	private partial void LogGetRequest(string url);

	[LoggerMessage(Level = LogLevel.Debug, Message = "POST {Url}")]
	private partial void LogPostRequest(string url);

	[LoggerMessage(Level = LogLevel.Debug, Message = "GET (paged) {Url}")]
	private partial void LogGetPagedRequest(string url);

	#endregion
}
