using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Orchestra.OpenCode;

/// <summary>A private, disposable V2 server for provider discovery and native integration authentication.</summary>
public sealed class OpenCodeSetupSession : IAsyncDisposable
{
	private readonly OpenCodeServerProcess _server;
	private readonly HttpClient _http;
	private OpenCodeSetupSession(OpenCodeServerProcess server, string password)
	{
		_server = server;
		_http = new() { Timeout = TimeSpan.FromSeconds(60) };
		_http.DefaultRequestHeaders.Authorization = new("Basic", Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("opencode:" + password)));
	}
	public static async Task<OpenCodeSetupSession> StartAsync(string? cliPath, string directory, ILogger logger, CancellationToken token)
	{
		var password = Guid.NewGuid().ToString("N");
		var options = new OpenCodeAgentPoolOptions { CliPath = cliPath, ServerPassword = password };
		Directory.CreateDirectory(directory);
		var server = new OpenCodeServerProcess(OpenCodeServerBootstrap.Resolve(options), options, new OpenCodeHttpClientFactory(), logger, workingDirectory: directory);
		var session = new OpenCodeSetupSession(server, password);
		try { await server.StartAsync(token); session._http.BaseAddress = new Uri(server.BaseUrl + "/"); return session; }
		catch { await session.DisposeAsync(); throw; }
	}
	public async Task<JsonElement> RequestAsync(HttpMethod method, string path, object? body, CancellationToken token)
	{
		using var request = new HttpRequestMessage(method, path);
		if (body is not null) request.Content = JsonContent.Create(body);
		using var response = await _http.SendAsync(request, token);
		if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"OpenCode setup request failed ({(int)response.StatusCode}): {await response.Content.ReadAsStringAsync(token)}");
		if (response.StatusCode == System.Net.HttpStatusCode.NoContent) return default;
		using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
		return json.RootElement.TryGetProperty("data", out var data) ? data.Clone() : json.RootElement.Clone();
	}
	public async ValueTask DisposeAsync() { _http.Dispose(); await _server.DisposeAsync(); }
}
