using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace Orchestra.OpenCode;

/// <summary>
/// Thin transport over an <c>opencode serve</c> HTTP server. Wraps only the endpoints the
/// adapter needs: session lifecycle, prompting, abort, permission replies, dynamic MCP
/// registration, health, and the V2 <c>GET /api/event</c> SSE bus. Mirrors the role
/// <c>ICopilotClient</c> plays for the Copilot SDK.
/// </summary>
internal interface IOpenCodeClient : IAsyncDisposable
{
	string BaseUrl { get; }

	Task<bool> HealthAsync(CancellationToken cancellationToken);
	Task EnsureReadyAsync(OpenCodeModelDto model, string? agent, TimeSpan timeout, CancellationToken cancellationToken);
	Task<bool> IsSessionWithinAsync(string candidate, string root, CancellationToken cancellationToken);
	Task<JsonElement> GetFormAsync(string sessionId, string formId, CancellationToken cancellationToken);
	Task ReplyFormAsync(string sessionId, string formId, object? answer, CancellationToken cancellationToken);

	Task<string> CreateSessionAsync(OpenCodeCreateSessionRequest request, CancellationToken cancellationToken);

	/// <summary>Wait for the V2 agent loop and read its durable message projection.</summary>
	Task<IReadOnlyList<JsonElement>> WaitForMessagesAsync(string sessionId, CancellationToken cancellationToken);

	Task DeleteSessionAsync(string sessionId, CancellationToken cancellationToken);

	/// <summary>Admits a V2 inbox prompt without waiting for execution to finish.</summary>
	Task<string> PromptAsync(string sessionId, OpenCodePromptRequest request, CancellationToken cancellationToken);

	Task AbortSessionAsync(string sessionId, CancellationToken cancellationToken);

	Task RespondPermissionAsync(string sessionId, string permissionId, string response, CancellationToken cancellationToken);

	/// <summary>Registers an MCP server with V2's runtime PUT endpoint.</summary>
	Task AddMcpAsync(string name, object config, CancellationToken cancellationToken);

	/// <summary>Streams the instance-global V2 event bus.</summary>
	IAsyncEnumerable<OpenCodeServerEvent> SubscribeAsync(CancellationToken cancellationToken);

	/// <summary>Lists currently loaded V2 agent IDs.</summary>
	Task<IReadOnlyList<string>> ListAgentNamesAsync(CancellationToken cancellationToken);

	/// <summary>Lists MCP server names whose V2 status is connected.</summary>
	Task<IReadOnlyList<string>> ListMcpNamesAsync(CancellationToken cancellationToken);
	Task<IReadOnlyList<string>> WaitForMcpNamesAsync(IReadOnlyCollection<string> required, TimeSpan timeout, CancellationToken cancellationToken);
}

internal interface IOpenCodeClientFactory
{
	IOpenCodeClient Create(string baseUrl, string? username, string? password);
}

internal sealed class OpenCodeHttpClientFactory : IOpenCodeClientFactory
{
	public IOpenCodeClient Create(string baseUrl, string? username, string? password)
		=> new OpenCodeHttpClient(baseUrl, username, password);
}

internal sealed class OpenCodeHttpClient : IOpenCodeClient
{
	private readonly HttpClient _http;
	private readonly bool _ownsHttp;

	public OpenCodeHttpClient(string baseUrl, string? username, string? password, HttpClient? http = null)
	{
		BaseUrl = baseUrl.TrimEnd('/');
		_ownsHttp = http is null;
		_http = http ?? new HttpClient();
		_http.BaseAddress = new Uri(BaseUrl + "/");
		// No overall timeout: prompt turns and the SSE stream are long-lived; cancellation is
		// driven by the per-step CancellationToken instead.
		_http.Timeout = Timeout.InfiniteTimeSpan;

		if (!string.IsNullOrEmpty(password))
		{
			var creds = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username ?? "opencode"}:{password}"));
			_http.DefaultRequestHeaders.Authorization =
				new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", creds);
		}
	}

	public string BaseUrl { get; }

	public async Task<bool> IsSessionWithinAsync(string candidate, string root, CancellationToken cancellationToken)
	{
		var seen = new HashSet<string>(StringComparer.Ordinal);
		while (seen.Add(candidate))
		{
			if (candidate == root)
				return true;
			using var response = await _http.GetAsync($"api/session/{Uri.EscapeDataString(candidate)}", cancellationToken).ConfigureAwait(false);
			if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
				return false;
			response.EnsureSuccessStatusCode();
			using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
			if (!doc.RootElement.GetProperty("data").TryGetProperty("parentID", out var parent) || parent.ValueKind != JsonValueKind.String)
				return false;
			candidate = parent.GetString()!;
		}
		return false;
	}

	public async Task EnsureReadyAsync(OpenCodeModelDto model, string? agent, TimeSpan timeout, CancellationToken cancellationToken)
	{
		using var deadlineCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		deadlineCts.CancelAfter(timeout);
		cancellationToken = deadlineCts.Token;
		var deadline = DateTimeOffset.UtcNow + timeout;
		do
		{
			var agents = await ListAgentNamesAsync(cancellationToken).ConfigureAwait(false);
			using var response = await _http.GetAsync("api/model", cancellationToken).ConfigureAwait(false);
			response.EnsureSuccessStatusCode();
			using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
			var selected = doc.RootElement.GetProperty("data").EnumerateArray().FirstOrDefault(m =>
				m.GetProperty("id").GetString() == model.ModelId && m.GetProperty("providerID").GetString() == model.ProviderId
				&& (!m.TryGetProperty("enabled", out var enabled) || enabled.GetBoolean()));
			if (selected.ValueKind == JsonValueKind.Object && (agent is null || agents.Contains(agent)))
			{
				if (model.Variant is not null and not "default"
					&& (!selected.TryGetProperty("variants", out var variants) || !variants.EnumerateArray().Any(v => v.GetProperty("id").GetString() == model.Variant)))
					throw new OpenCodeSessionFailedException($"OpenCode model '{model.ProviderId}/{model.ModelId}' does not advertise variant '{model.Variant}'.");
				return;
			}
			await Task.Delay(200, cancellationToken).ConfigureAwait(false);
		} while (DateTimeOffset.UtcNow < deadline);
		throw new OpenCodeSessionFailedException($"OpenCode V2 did not load model '{model.ProviderId}/{model.ModelId}' and agent '{agent ?? "build"}' within {timeout.TotalSeconds:0}s.");
	}

	public async Task<JsonElement> GetFormAsync(string sessionId, string formId, CancellationToken cancellationToken)
	{
		using var response = await _http.GetAsync($"api/session/{Uri.EscapeDataString(sessionId)}/form/{Uri.EscapeDataString(formId)}", cancellationToken).ConfigureAwait(false);
		response.EnsureSuccessStatusCode();
		using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
		return doc.RootElement.GetProperty("data").Clone();
	}

	public async Task ReplyFormAsync(string sessionId, string formId, object? answer, CancellationToken cancellationToken)
	{
		var path = $"api/session/{Uri.EscapeDataString(sessionId)}/form/{Uri.EscapeDataString(formId)}";
		using var response = answer is null
			? await _http.DeleteAsync(path, cancellationToken).ConfigureAwait(false)
			: await _http.PostAsJsonAsync(path + "/reply", new { answer }, OpenCodeJson.Options, cancellationToken).ConfigureAwait(false);
		response.EnsureSuccessStatusCode();
	}

	public async Task<bool> HealthAsync(CancellationToken cancellationToken)
	{
		try
		{
			using var resp = await _http.GetAsync("api/info", cancellationToken).ConfigureAwait(false);
			if (!resp.IsSuccessStatusCode)
				return false;
			using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
			return doc.RootElement.TryGetProperty("version", out var version) && version.GetString()?.StartsWith("2.", StringComparison.Ordinal) == true;
		}
		catch
		{
			return false;
		}
	}

	public async Task<string> CreateSessionAsync(OpenCodeCreateSessionRequest request, CancellationToken cancellationToken)
	{
		using var resp = await _http.PostAsJsonAsync(
			"api/session", request, OpenCodeJson.Options, cancellationToken).ConfigureAwait(false);
		resp.EnsureSuccessStatusCode();
		using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
		if (doc.RootElement.GetProperty("data").TryGetProperty("id", out var id) && id.GetString() is { } sessionId)
			return sessionId;
		throw new OpenCodeSessionFailedException("OpenCode POST /api/session returned no session id.");
	}

	public async Task DeleteSessionAsync(string sessionId, CancellationToken cancellationToken)
	{
		using var resp = await _http.DeleteAsync($"api/session/{Uri.EscapeDataString(sessionId)}", cancellationToken).ConfigureAwait(false);
		if (resp.StatusCode != System.Net.HttpStatusCode.NotFound)
			resp.EnsureSuccessStatusCode();
	}

	public async Task<string> PromptAsync(string sessionId, OpenCodePromptRequest request, CancellationToken cancellationToken)
	{
		using var resp = await _http.PostAsJsonAsync(
			$"api/session/{Uri.EscapeDataString(sessionId)}/prompt", new
			{
				text = string.Join("\n", request.Parts.Where(p => p.Type == "text").Select(p => p.Text)),
				files = request.Parts.Where(p => p.Type == "file").Select(p => new { uri = p.Url, name = p.Filename }).ToArray(),
			}, OpenCodeJson.Options, cancellationToken).ConfigureAwait(false);
		resp.EnsureSuccessStatusCode();
		using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
		return doc.RootElement.GetProperty("data").GetProperty("id").GetString()
			?? throw new OpenCodeSessionFailedException("V2 prompt admission returned no message ID.");
	}

	public async Task AbortSessionAsync(string sessionId, CancellationToken cancellationToken)
	{
		using var resp = await _http.PostAsync($"api/session/{Uri.EscapeDataString(sessionId)}/interrupt", content: null, cancellationToken).ConfigureAwait(false);
		if (resp.StatusCode != System.Net.HttpStatusCode.NotFound)
			resp.EnsureSuccessStatusCode();
	}

	public async Task RespondPermissionAsync(string sessionId, string permissionId, string response, CancellationToken cancellationToken)
	{
		using var resp = await _http.PostAsJsonAsync(
			$"api/session/{Uri.EscapeDataString(sessionId)}/permission/{Uri.EscapeDataString(permissionId)}/reply",
			new { decision = response },
			OpenCodeJson.Options,
			cancellationToken).ConfigureAwait(false);
		resp.EnsureSuccessStatusCode();
	}

	public async Task AddMcpAsync(string name, object config, CancellationToken cancellationToken)
	{
		using var resp = await _http.PutAsJsonAsync(
			$"api/experimental/mcp/{Uri.EscapeDataString(name)}", new { config }, OpenCodeJson.Options, cancellationToken).ConfigureAwait(false);
		resp.EnsureSuccessStatusCode();
	}

	public async IAsyncEnumerable<OpenCodeServerEvent> SubscribeAsync([EnumeratorCancellation] CancellationToken cancellationToken)
	{
		using var req = new HttpRequestMessage(HttpMethod.Get, "api/event");
		using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
		resp.EnsureSuccessStatusCode();

		await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
		using var reader = new StreamReader(stream, Encoding.UTF8);

		var data = new StringBuilder();
		while (!cancellationToken.IsCancellationRequested)
		{
			var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
			if (line is null)
				break; // server closed the stream

			if (line.Length == 0)
			{
				// Blank line terminates an SSE event.
				if (data.Length > 0)
				{
					var evt = TryParseEvent(data.ToString());
					data.Clear();
					if (evt is not null)
					{
						yield return evt;
					}
				}
				continue;
			}

			if (line.StartsWith("data:", StringComparison.Ordinal))
			{
				// Per SSE, multiple data: lines concatenate with newlines.
				if (data.Length > 0)
					data.Append('\n');
				data.Append(line.AsSpan(5).Trim());
			}
			// "event:" / ":" comment lines are ignored — OpenCode carries the type in the JSON.
		}
	}

	private static OpenCodeServerEvent? TryParseEvent(string json)
	{
		if (string.IsNullOrWhiteSpace(json))
			return null;
		try
		{
			using var doc = JsonDocument.Parse(json);
			var root = doc.RootElement;
			if (!root.TryGetProperty("type", out var typeEl) || typeEl.GetString() is not { } type)
				return null;

			var properties = root.TryGetProperty("data", out var props)
				? props.Clone()
				: default;

			return new OpenCodeServerEvent { Type = type, Properties = properties };
		}
		catch (JsonException)
		{
			return null;
		}
	}

	public async Task<IReadOnlyList<string>> ListAgentNamesAsync(CancellationToken cancellationToken)
	{
		using var resp = await _http.GetAsync("api/agent", cancellationToken).ConfigureAwait(false);
		resp.EnsureSuccessStatusCode();
		using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
		var names = new List<string>();
		if (doc.RootElement.GetProperty("data").ValueKind == JsonValueKind.Array)
		{
			foreach (var el in doc.RootElement.GetProperty("data").EnumerateArray())
			{
				if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty("id", out var n) && n.GetString() is { } name)
					names.Add(name);
			}
		}
		return names;
	}

	public async Task<IReadOnlyList<string>> ListMcpNamesAsync(CancellationToken cancellationToken)
	{
		using var resp = await _http.GetAsync("api/mcp", cancellationToken).ConfigureAwait(false);
		resp.EnsureSuccessStatusCode();
		using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
		var names = new List<string>();
		// V2 returns data[] with explicit per-server connection status.
		if (doc.RootElement.GetProperty("data").ValueKind == JsonValueKind.Array)
		{
			foreach (var server in doc.RootElement.GetProperty("data").EnumerateArray())
				if (server.GetProperty("status").GetProperty("status").GetString() == "connected")
					names.Add(server.GetProperty("name").GetString()!);
		}
		return names;
	}

	public ValueTask DisposeAsync()
	{
		if (_ownsHttp)
			_http.Dispose();
		return ValueTask.CompletedTask;
	}

	public async Task<IReadOnlyList<string>> WaitForMcpNamesAsync(IReadOnlyCollection<string> required, TimeSpan timeout, CancellationToken cancellationToken)
	{
		using var deadlineCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		deadlineCts.CancelAfter(timeout);
		cancellationToken = deadlineCts.Token;
		var deadline = DateTimeOffset.UtcNow + timeout;
		while (true)
		{
			using var response = await _http.GetAsync("api/mcp", cancellationToken).ConfigureAwait(false);
			response.EnsureSuccessStatusCode();
			using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
			var statuses = doc.RootElement.GetProperty("data").EnumerateArray().ToDictionary(
				s => s.GetProperty("name").GetString()!, s => s.GetProperty("status").GetProperty("status").GetString());
			if (required.All(name => statuses.TryGetValue(name, out var status) && status != "pending") || DateTimeOffset.UtcNow >= deadline)
				return statuses.Where(s => s.Value == "connected").Select(s => s.Key).ToArray();
			await Task.Delay(200, cancellationToken).ConfigureAwait(false);
		}
	}

	public async Task<IReadOnlyList<JsonElement>> WaitForMessagesAsync(string sessionId, CancellationToken cancellationToken)
	{
		using var wait = await _http.PostAsync($"api/experimental/session/{Uri.EscapeDataString(sessionId)}/wait", null, cancellationToken).ConfigureAwait(false);
		wait.EnsureSuccessStatusCode();
		var messages = new List<JsonElement>();
		string? cursor = null;
		var seenCursors = new HashSet<string>(StringComparer.Ordinal);
		while (true)
		{
			var query = cursor is null ? "order=asc" : $"cursor={Uri.EscapeDataString(cursor)}";
			using var resp = await _http.GetAsync($"api/session/{Uri.EscapeDataString(sessionId)}/message?limit=100&{query}", cancellationToken).ConfigureAwait(false);
			resp.EnsureSuccessStatusCode();
			using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
			var page = doc.RootElement.GetProperty("data");
			if (page.GetArrayLength() == 0)
				break;
			messages.AddRange(page.EnumerateArray().Select(m => m.Clone()));
			cursor = doc.RootElement.GetProperty("cursor").TryGetProperty("next", out var next) && next.ValueKind == JsonValueKind.String ? next.GetString() : null;
			if (cursor is null)
				break;
			if (!seenCursors.Add(cursor))
				throw new OpenCodeSessionFailedException("OpenCode repeated a message pagination cursor.");
		}
		return messages;
	}
}
