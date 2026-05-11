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
		_ownsHttp = true;
	}

	/// <summary>
	/// Test/integration constructor: use a caller-provided <see cref="HttpClient"/> (for example,
	/// the one returned by <c>WebApplicationFactory.CreateClient()</c>). The supplied client's
	/// lifetime is the caller's responsibility.
	/// </summary>
	public OrchestraClient(HttpClient httpClient)
	{
		_http = httpClient;
		_ownsHttp = false;
	}

	private readonly bool _ownsHttp;

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

	/// <summary>
	/// Opens a streaming SSE connection that starts a new run of <paramref name="orchestrationId"/>
	/// and emits every event for that run's lifetime. Caller owns the returned response and
	/// must dispose it when done; the body stream stays open until the run terminates or the
	/// caller cancels via <paramref name="cancellationToken"/>.
	/// </summary>
	/// <remarks>
	/// Uses <see cref="HttpCompletionOption.ResponseHeadersRead"/> so the caller can begin reading
	/// frames immediately rather than waiting for the entire response body to buffer.
	/// </remarks>
	public async Task<HttpResponseMessage> OpenRunStreamAsync(
		string orchestrationId,
		Dictionary<string, string>? parameters,
		CancellationToken cancellationToken)
	{
		var paramJson = parameters is { Count: > 0 }
			? Uri.EscapeDataString(JsonSerializer.Serialize(parameters, s_jsonOptions))
			: null;
		var url = $"api/orchestrations/{Uri.EscapeDataString(orchestrationId)}/run";
		if (paramJson is not null)
		{
			url += $"?params={paramJson}";
		}

		using var request = new HttpRequestMessage(HttpMethod.Get, url);
		request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("text/event-stream"));

		var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
			.ConfigureAwait(false);
		return response;
	}

	/// <summary>
	/// Opens a streaming SSE connection attached to an existing run identified by
	/// <paramref name="orchestrationName"/> + <paramref name="runId"/>. Caller owns the
	/// response.
	/// </summary>
	public async Task<HttpResponseMessage> OpenAttachStreamAsync(
		string orchestrationName,
		string runId,
		CancellationToken cancellationToken)
	{
		var url = $"api/orchestrations/{Uri.EscapeDataString(orchestrationName)}/runs/{Uri.EscapeDataString(runId)}/attach";

		using var request = new HttpRequestMessage(HttpMethod.Get, url);
		request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("text/event-stream"));

		var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
			.ConfigureAwait(false);
		return response;
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

	// ── Human-in-the-loop ──

	public async Task<JsonElement> ListPendingAsync(string? orchestration = null)
	{
		var url = "api/runs/pending";
		if (!string.IsNullOrEmpty(orchestration))
			url += $"?orchestration={Uri.EscapeDataString(orchestration)}";
		return await GetAsync(url);
	}

	public async Task<JsonElement> GetPendingAsync(string orchestrationName, string runId, string stepName)
		=> await GetAsync($"api/orchestrations/{Uri.EscapeDataString(orchestrationName)}/runs/{Uri.EscapeDataString(runId)}/pending/{Uri.EscapeDataString(stepName)}");

	public async Task<JsonElement> RespondAsync(string orchestrationName, string runId, string stepName, string? choice, string? reply, string? respondedBy = null)
	{
		var url = $"api/orchestrations/{Uri.EscapeDataString(orchestrationName)}/runs/{Uri.EscapeDataString(runId)}/respond?step={Uri.EscapeDataString(stepName)}";
		return await PostAsync(url, new { choice, reply, respondedBy });
	}

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
			if (!response.IsSuccessStatusCode)
			{
				throw new HttpRequestException(
					$"Server returned {(int)response.StatusCode} {response.ReasonPhrase} with no body.",
					null,
					response.StatusCode);
			}
			return JsonSerializer.SerializeToElement(new
			{
				statusCode = (int)response.StatusCode,
				success = true,
			}, s_jsonOptions);
		}

		try
		{
			var result = JsonSerializer.Deserialize<JsonElement>(content, s_jsonOptions);

			// Check for error responses that contain problem details
			if (!response.IsSuccessStatusCode)
			{
				var detail = result.TryGetProperty("detail", out var detailProp)
					? detailProp.GetString()
					: content;
				throw new HttpRequestException(
					$"Server returned {(int)response.StatusCode}: {detail}",
					null,
					response.StatusCode);
			}

			return result;
		}
		catch (HttpRequestException)
		{
			throw; // Re-throw HTTP errors from above
		}
		catch (JsonException)
		{
			if (!response.IsSuccessStatusCode)
			{
				throw new HttpRequestException(
					$"Server returned {(int)response.StatusCode}: {content}",
					null,
					response.StatusCode);
			}
			return JsonSerializer.SerializeToElement(new
			{
				statusCode = (int)response.StatusCode,
				body = content,
			}, s_jsonOptions);
		}
	}

	public void Dispose()
	{
		if (_ownsHttp)
		{
			_http.Dispose();
		}
	}
}
