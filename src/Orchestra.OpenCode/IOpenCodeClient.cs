using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace Orchestra.OpenCode;

/// <summary>
/// Thin transport over an <c>opencode serve</c> HTTP server. Wraps only the endpoints the
/// adapter needs: session lifecycle, prompting, abort, permission replies, dynamic MCP
/// registration, health, and the <c>GET /event</c> SSE bus. Mirrors the role
/// <c>ICopilotClient</c> plays for the Copilot SDK.
/// </summary>
internal interface IOpenCodeClient : IAsyncDisposable
{
	string BaseUrl { get; }

	Task<bool> HealthAsync(CancellationToken cancellationToken);

	Task<string> CreateSessionAsync(string? title, CancellationToken cancellationToken);

	Task DeleteSessionAsync(string sessionId, CancellationToken cancellationToken);

	/// <summary>Sends a prompt without blocking on the turn (<c>POST /session/:id/prompt_async</c>, 204).</summary>
	Task PromptAsync(string sessionId, OpenCodePromptRequest request, CancellationToken cancellationToken);

	Task AbortSessionAsync(string sessionId, CancellationToken cancellationToken);

	Task RespondPermissionAsync(string sessionId, string permissionId, string response, CancellationToken cancellationToken);

	/// <summary>Registers an MCP server on the instance (<c>POST /mcp</c>).</summary>
	Task AddMcpAsync(string name, object config, CancellationToken cancellationToken);

	/// <summary>Streams the instance-global event bus (<c>GET /event</c>).</summary>
	IAsyncEnumerable<OpenCodeServerEvent> SubscribeAsync(CancellationToken cancellationToken);
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

	public async Task<bool> HealthAsync(CancellationToken cancellationToken)
	{
		try
		{
			using var resp = await _http.GetAsync("global/health", cancellationToken).ConfigureAwait(false);
			return resp.IsSuccessStatusCode;
		}
		catch
		{
			return false;
		}
	}

	public async Task<string> CreateSessionAsync(string? title, CancellationToken cancellationToken)
	{
		using var resp = await _http.PostAsJsonAsync(
			"session", new OpenCodeCreateSessionRequest { Title = title }, OpenCodeJson.Options, cancellationToken).ConfigureAwait(false);
		resp.EnsureSuccessStatusCode();
		using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
		if (doc.RootElement.TryGetProperty("id", out var id) && id.GetString() is { } sessionId)
			return sessionId;
		throw new OpenCodeSessionFailedException("OpenCode POST /session returned no session id.");
	}

	public async Task DeleteSessionAsync(string sessionId, CancellationToken cancellationToken)
	{
		try
		{
			using var resp = await _http.DeleteAsync($"session/{Uri.EscapeDataString(sessionId)}", cancellationToken).ConfigureAwait(false);
		}
		catch
		{
			// Best-effort cleanup.
		}
	}

	public async Task PromptAsync(string sessionId, OpenCodePromptRequest request, CancellationToken cancellationToken)
	{
		using var resp = await _http.PostAsJsonAsync(
			$"session/{Uri.EscapeDataString(sessionId)}/prompt_async", request, OpenCodeJson.Options, cancellationToken).ConfigureAwait(false);
		resp.EnsureSuccessStatusCode();
	}

	public async Task AbortSessionAsync(string sessionId, CancellationToken cancellationToken)
	{
		try
		{
			using var resp = await _http.PostAsync($"session/{Uri.EscapeDataString(sessionId)}/abort", content: null, cancellationToken).ConfigureAwait(false);
		}
		catch
		{
			// Abort is best-effort; the per-step token cancellation already unwinds the turn.
		}
	}

	public async Task RespondPermissionAsync(string sessionId, string permissionId, string response, CancellationToken cancellationToken)
	{
		using var resp = await _http.PostAsJsonAsync(
			$"session/{Uri.EscapeDataString(sessionId)}/permissions/{Uri.EscapeDataString(permissionId)}",
			new OpenCodePermissionResponse { Response = response },
			OpenCodeJson.Options,
			cancellationToken).ConfigureAwait(false);
		resp.EnsureSuccessStatusCode();
	}

	public async Task AddMcpAsync(string name, object config, CancellationToken cancellationToken)
	{
		using var resp = await _http.PostAsJsonAsync(
			"mcp", new { name, config }, OpenCodeJson.Options, cancellationToken).ConfigureAwait(false);
		resp.EnsureSuccessStatusCode();
	}

	public async IAsyncEnumerable<OpenCodeServerEvent> SubscribeAsync([EnumeratorCancellation] CancellationToken cancellationToken)
	{
		using var req = new HttpRequestMessage(HttpMethod.Get, "event");
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
						yield return evt;
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

			var properties = root.TryGetProperty("properties", out var props)
				? props.Clone()
				: default;

			return new OpenCodeServerEvent { Type = type, Properties = properties };
		}
		catch (JsonException)
		{
			return null;
		}
	}

	public ValueTask DisposeAsync()
	{
		if (_ownsHttp)
			_http.Dispose();
		return ValueTask.CompletedTask;
	}
}
