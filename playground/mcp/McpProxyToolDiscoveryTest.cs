#:package ModelContextProtocol
#:property PublishAot=false

// ─────────────────────────────────────────────────────────────────────────────
// MCP Proxy Tool Discovery Test
//
// PURPOSE:
//   Verifies that tools/list returns the expected tools when connecting to
//   an mcpproxy instance's per-server endpoints. This reproduces a scenario
//   where MCP servers show status "Connected" but expose zero tools.
//
// CONTEXT:
//   We have an mcpproxy instance running on port 5113, configured with
//   per-server routing and deferConnection: true. Four M365 backends are
//   proxied: calendar, mail, me, m365-copilot.
//
//   When a client connects to http://localhost:5113/mcp/{server}:
//     Expected: tools/list returns the backend's tools
//     Actual:   tools/list returns 0 tools
//
//   The MCP connection itself succeeds (status: Connected), but tool
//   discovery returns empty. This means the proxy transport layer works,
//   but either:
//     (a) The backend hasn't initialized yet (deferConnection: true delays
//         backend connection until first request, but tools/list IS a request)
//     (b) tools/list completes before the backend finishes connecting
//     (c) The proxy caches an empty tool list from initialization
//
// PROXY CONFIG (m365.proxy.json):
//   {
//     "proxy": {
//       "routing": { "mode": "perServer", "basePath": "/mcp" }
//     },
//     "mcp": {
//       "calendar": {
//         "type": "http",
//         "url": "https://agent365.svc.cloud.microsoft/.../mcp_CalendarTools",
//         "auth": {
//           "type": "InteractiveBrowser",
//           "deferConnection": true,
//           ...
//         }
//       },
//       ... (mail, me, m365-copilot similar)
//     }
//   }
//
// USAGE:
//   dotnet run --file McpProxyToolDiscoveryTest.cs
//   dotnet run --file McpProxyToolDiscoveryTest.cs -- --port 5113
//   dotnet run --file McpProxyToolDiscoveryTest.cs -- --port 5113 --server calendar
// ─────────────────────────────────────────────────────────────────────────────

using ModelContextProtocol.Client;
using System.Net.Http.Json;
using System.Text.Json;

var port = 5113;
var servers = new[] { "calendar", "mail", "me", "m365-copilot" };
string? singleServer = null;

// Parse arguments
for (int i = 0; i < args.Length; i++)
{
	if (args[i] == "--port" && i + 1 < args.Length)
		port = int.Parse(args[++i]);
	else if (args[i] == "--server" && i + 1 < args.Length)
		singleServer = args[++i];
}

if (singleServer is not null)
	servers = [singleServer];

var baseUrl = $"http://localhost:{port}/mcp";
Console.WriteLine($"Testing MCP proxy at {baseUrl}");
Console.WriteLine($"Servers to test: {string.Join(", ", servers)}");
Console.WriteLine(new string('─', 60));

// ── Test 1: HTTP connectivity check ──────────────────────────────────────
Console.WriteLine("\n[1] HTTP connectivity check");
using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
try
{
	var response = await http.GetAsync(baseUrl);
	Console.WriteLine($"    GET {baseUrl} → {(int)response.StatusCode} {response.StatusCode}");
}
catch (Exception ex)
{
	Console.WriteLine($"    GET {baseUrl} → FAILED: {ex.Message}");
	Console.WriteLine("\n    The proxy is not reachable. Is the m365-remote-mcps service running?");
	return;
}

// ── Test 2: REST tools/list on per-server endpoints ──────────────────────
Console.WriteLine("\n[2] REST tools/list (POST {endpoint}/tools/list)");
foreach (var server in servers)
{
	var endpoint = $"{baseUrl}/{server}";
	try
	{
		var response = await http.PostAsync(
			$"{endpoint}/tools/list",
			new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));

		var status = $"{(int)response.StatusCode} {response.StatusCode}";
		if (response.IsSuccessStatusCode)
		{
			var body = await response.Content.ReadAsStringAsync();
			var json = JsonDocument.Parse(body);
			if (json.RootElement.TryGetProperty("tools", out var tools))
			{
				var toolNames = tools.EnumerateArray()
					.Select(t => t.GetProperty("name").GetString())
					.ToArray();
				Console.WriteLine($"    {server}: {status} → {toolNames.Length} tool(s)");
				foreach (var name in toolNames)
					Console.WriteLine($"      - {name}");
			}
			else
			{
				Console.WriteLine($"    {server}: {status} → No 'tools' property in response");
				Console.WriteLine($"      Response: {body[..Math.Min(body.Length, 200)]}");
			}
		}
		else
		{
			var body = await response.Content.ReadAsStringAsync();
			Console.WriteLine($"    {server}: {status}");
			Console.WriteLine($"      Response: {body[..Math.Min(body.Length, 200)]}");
		}
	}
	catch (Exception ex)
	{
		Console.WriteLine($"    {server}: FAILED → {ex.Message}");
	}
}

// ── Test 3: MCP Streamable HTTP protocol (full handshake) ────────────────
Console.WriteLine("\n[3] MCP Streamable HTTP (initialize + tools/list)");
foreach (var server in servers)
{
	var endpoint = $"{baseUrl}/{server}";
	try
	{
		await using var client = await McpClient.CreateAsync(
			new HttpClientTransport(new HttpClientTransportOptions
			{
				Endpoint = new Uri(endpoint),
			}));

		var tools = await client.ListToolsAsync();
		Console.WriteLine($"    {server}: Connected → {tools.Count} tool(s)");
		foreach (var tool in tools)
			Console.WriteLine($"      - {tool.Name}: {tool.Description?[..Math.Min(tool.Description.Length, 60)]}");
	}
	catch (Exception ex)
	{
		Console.WriteLine($"    {server}: FAILED → {ex.GetType().Name}: {ex.Message}");
	}
}

// ── Test 4: Unified endpoint (all tools aggregated) ──────────────────────
Console.WriteLine("\n[4] Unified endpoint tools/list (POST {baseUrl}/tools/list)");
try
{
	var response = await http.PostAsync(
		$"{baseUrl}/tools/list",
		new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));

	if (response.IsSuccessStatusCode)
	{
		var body = await response.Content.ReadAsStringAsync();
		var json = JsonDocument.Parse(body);
		if (json.RootElement.TryGetProperty("tools", out var tools))
		{
			var toolNames = tools.EnumerateArray()
				.Select(t => t.GetProperty("name").GetString())
				.ToArray();
			Console.WriteLine($"    Unified: {toolNames.Length} total tool(s)");
			foreach (var name in toolNames.Take(10))
				Console.WriteLine($"      - {name}");
			if (toolNames.Length > 10)
				Console.WriteLine($"      ... and {toolNames.Length - 10} more");
		}
		else
		{
			Console.WriteLine($"    No 'tools' property in response");
		}
	}
	else
	{
		Console.WriteLine($"    {(int)response.StatusCode} {response.StatusCode}");
	}
}
catch (Exception ex)
{
	Console.WriteLine($"    FAILED → {ex.Message}");
}

Console.WriteLine("\n" + new string('─', 60));
Console.WriteLine("Done.");
