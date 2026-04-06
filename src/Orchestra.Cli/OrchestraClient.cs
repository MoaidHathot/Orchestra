using System.Net.Http.Json;
using System.Text.Json;

namespace Orchestra.Cli;

/// <summary>
/// HTTP client wrapper for communicating with the Orchestra server REST API.
/// </summary>
public class OrchestraClient : IDisposable
{
	private readonly HttpClient _http;
	private static readonly JsonSerializerOptions s_jsonOptions = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		PropertyNameCaseInsensitive = true,
		WriteIndented = true,
		DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
	};

	public OrchestraClient(string serverUrl)
	{
		_http = new HttpClient { BaseAddress = new Uri(serverUrl.TrimEnd('/') + "/") };
	}

	// ── Orchestrations ──

	public async Task<JsonElement> ListOrchestrationsAsync()
		=> await GetAsync("api/orchestrations");

	public async Task<JsonElement> GetOrchestrationAsync(string id)
		=> await GetAsync($"api/orchestrations/{Uri.EscapeDataString(id)}");

	public async Task<JsonElement> RegisterOrchestrationAsync(string path)
		=> await PostAsync("api/orchestrations", new { paths = new[] { path } });

	public async Task<JsonElement> RemoveOrchestrationAsync(string id)
		=> await DeleteAsync($"api/orchestrations/{Uri.EscapeDataString(id)}");

	public async Task<JsonElement> ScanDirectoryAsync(string directory)
		=> await PostAsync("api/orchestrations/scan", new { directory });

	public async Task<JsonElement> EnableOrchestrationAsync(string id)
		=> await PostAsync($"api/orchestrations/{Uri.EscapeDataString(id)}/enable", new { });

	public async Task<JsonElement> DisableOrchestrationAsync(string id)
		=> await PostAsync($"api/orchestrations/{Uri.EscapeDataString(id)}/disable", new { });

	// ── Execution ──

	public async Task<JsonElement> RunOrchestrationAsync(string id, Dictionary<string, string>? parameters = null, bool async_ = true, int timeoutSeconds = 300)
	{
		var paramJson = parameters is { Count: > 0 }
			? Uri.EscapeDataString(JsonSerializer.Serialize(parameters, s_jsonOptions))
			: null;
		var url = $"api/orchestrations/{Uri.EscapeDataString(id)}/run";
		if (paramJson is not null)
			url += $"?params={paramJson}";

		// For async execution via the API, use the SSE endpoint but just get initial response
		return await GetAsync(url);
	}

	// ── Active Executions ──

	public async Task<JsonElement> GetActiveExecutionsAsync()
		=> await GetAsync("api/active");

	public async Task<JsonElement> CancelExecutionAsync(string executionId)
		=> await PostAsync($"api/active/{Uri.EscapeDataString(executionId)}/cancel", new { });

	// ── Run History ──

	public async Task<JsonElement> ListRunsAsync(int? limit = null)
	{
		var url = "api/history";
		if (limit.HasValue) url += $"?limit={limit}";
		return await GetAsync(url);
	}

	public async Task<JsonElement> GetRunAsync(string orchestrationName, string runId)
		=> await GetAsync($"api/history/{Uri.EscapeDataString(orchestrationName)}/{Uri.EscapeDataString(runId)}");

	public async Task<JsonElement> DeleteRunAsync(string orchestrationName, string runId)
		=> await DeleteAsync($"api/history/{Uri.EscapeDataString(orchestrationName)}/{Uri.EscapeDataString(runId)}");

	// ── Triggers ──

	public async Task<JsonElement> ListTriggersAsync()
		=> await GetAsync("api/triggers");

	public async Task<JsonElement> EnableTriggerAsync(string id)
		=> await PostAsync($"api/triggers/{Uri.EscapeDataString(id)}/enable", new { });

	public async Task<JsonElement> DisableTriggerAsync(string id)
		=> await PostAsync($"api/triggers/{Uri.EscapeDataString(id)}/disable", new { });

	public async Task<JsonElement> FireTriggerAsync(string id, Dictionary<string, string>? parameters = null)
		=> await PostAsync($"api/triggers/{Uri.EscapeDataString(id)}/fire", new { parameters });

	// ── Profiles ──

	public async Task<JsonElement> ListProfilesAsync()
		=> await GetAsync("api/profiles");

	public async Task<JsonElement> GetProfileAsync(string id)
		=> await GetAsync($"api/profiles/{Uri.EscapeDataString(id)}");

	public async Task<JsonElement> ActivateProfileAsync(string id)
		=> await PostAsync($"api/profiles/{Uri.EscapeDataString(id)}/activate", new { });

	public async Task<JsonElement> DeactivateProfileAsync(string id)
		=> await PostAsync($"api/profiles/{Uri.EscapeDataString(id)}/deactivate", new { });

	public async Task<JsonElement> DeleteProfileAsync(string id)
		=> await DeleteAsync($"api/profiles/{Uri.EscapeDataString(id)}");

	// ── Tags ──

	public async Task<JsonElement> ListTagsAsync()
		=> await GetAsync("api/tags");

	public async Task<JsonElement> GetOrchestrationTagsAsync(string id)
		=> await GetAsync($"api/orchestrations/{Uri.EscapeDataString(id)}/tags");

	public async Task<JsonElement> AddTagsAsync(string id, string[] tags)
		=> await PostAsync($"api/orchestrations/{Uri.EscapeDataString(id)}/tags", new { tags });

	public async Task<JsonElement> RemoveTagAsync(string id, string tag)
		=> await DeleteAsync($"api/orchestrations/{Uri.EscapeDataString(id)}/tags/{Uri.EscapeDataString(tag)}");

	// ── Status ──

	public async Task<JsonElement> GetStatusAsync()
		=> await GetAsync("api/status");

	// ── HTTP helpers ──

	private async Task<JsonElement> GetAsync(string url)
	{
		var response = await _http.GetAsync(url);
		return await ReadResponseAsync(response);
	}

	private async Task<JsonElement> PostAsync(string url, object body)
	{
		var response = await _http.PostAsJsonAsync(url, body, s_jsonOptions);
		return await ReadResponseAsync(response);
	}

	private async Task<JsonElement> DeleteAsync(string url)
	{
		var response = await _http.DeleteAsync(url);
		return await ReadResponseAsync(response);
	}

	private static async Task<JsonElement> ReadResponseAsync(HttpResponseMessage response)
	{
		var content = await response.Content.ReadAsStringAsync();
		if (string.IsNullOrWhiteSpace(content))
		{
			return JsonSerializer.SerializeToElement(new
			{
				statusCode = (int)response.StatusCode,
				success = response.IsSuccessStatusCode,
			}, s_jsonOptions);
		}

		try
		{
			return JsonSerializer.Deserialize<JsonElement>(content, s_jsonOptions);
		}
		catch
		{
			return JsonSerializer.SerializeToElement(new
			{
				statusCode = (int)response.StatusCode,
				body = content,
			}, s_jsonOptions);
		}
	}

	public void Dispose() => _http.Dispose();
}
