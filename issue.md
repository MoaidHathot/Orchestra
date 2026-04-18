# Per-Server MCP Streamable HTTP: Tools Not Discovered by Downstream Consumers

## Summary

When an MCP client connects to a per-server endpoint (e.g., `/mcp/calendar`) on an
mcpproxy instance via MCP Streamable HTTP, tool discovery fails: the connection
succeeds (status: "Connected") but `tools/list` returns **zero tools**. The same
endpoint returns the correct tools via the REST sub-route
(`POST /mcp/calendar/tools/list`).

This was tested with **mcpproxy CLI v1.15.0** and **McpProxy.Sdk v1.15.0**.

## Environment

- **mcpproxy CLI**: `1.15.0+2c4d850d052f9819a58229c17b7f3a67f0167ad4`
- **McpProxy.Sdk NuGet**: `1.15.0`
- **ModelContextProtocol (C# SDK)**: `1.2.0`
- **Platform**: Windows 11, .NET 10
- **Backends**: Microsoft 365 MCP servers via `agent365.svc.cloud.microsoft`
  (Calendar, Mail, Me, M365-Copilot)

## Proxy Configuration

The mcpproxy CLI is started with per-server HTTP routing:

```
dnx mcpproxy --yes -- -t http -c m365.proxy.json -p 5113
```

**m365.proxy.json** (simplified):

```json
{
  "proxy": {
    "serverInfo": {
      "name": "m365 MCP Proxies",
      "version": "1.0.0"
    },
    "routing": {
      "mode": "perServer",
      "basePath": "/mcp"
    }
  },
  "mcp": {
    "calendar": {
      "type": "http",
      "title": "Microsoft 365 Calendar",
      "url": "https://agent365.svc.cloud.microsoft/agents/tenants/<tenant-id>/servers/mcp_CalendarTools",
      "enabled": true,
      "auth": {
        "type": "InteractiveBrowser",
        "credentialGroup": "m365",
        "deferConnection": true,
        "azureAd": {
          "clientId": "<client-id>",
          "tenantId": "<tenant-id>",
          "scopes": ["<audience>/.default"]
        }
      }
    },
    "mail": {
      "type": "http",
      "title": "Microsoft 365 Mail",
      "url": "https://agent365.svc.cloud.microsoft/agents/tenants/<tenant-id>/servers/mcp_MailTools",
      "enabled": true,
      "auth": { "...same as calendar..." }
    },
    "me": {
      "type": "http",
      "title": "Microsoft 365 Me",
      "url": "https://agent365.svc.cloud.microsoft/agents/tenants/<tenant-id>/servers/mcp_MeServer",
      "enabled": true,
      "auth": { "...same as calendar..." }
    },
    "m365-copilot": {
      "type": "http",
      "title": "Microsoft 365 Copilot",
      "url": "https://agent365.svc.cloud.microsoft/agents/tenants/<tenant-id>/servers/mcp_M365Copilot",
      "enabled": true,
      "auth": { "...same as calendar..." }
    }
  }
}
```

## Consumer Architecture

A downstream application (Orchestra) consumes these per-server endpoints by
creating a second mcpproxy layer using **McpProxy.Sdk** (the "McpManager proxy"):

```
Copilot SDK  →  McpManager proxy (random port, McpProxy.Sdk)
                  →  mcpproxy CLI (port 5113)
                       →  M365 cloud backends
```

The McpManager proxy is built using the SDK:

```csharp
builder.Services.AddMcpProxy(proxy =>
{
    proxy.WithServerInfo("orchestra-mcp-proxy", "1.0.0");
    proxy.WithRouting(RoutingMode.PerServer, "/mcp");

    // Add each remote MCP as an HTTP backend
    proxy.AddHttpServer("calendar", "http://localhost:5113/mcp/calendar")
         .WithTitle("calendar")
         .Build();
    proxy.AddHttpServer("mail", "http://localhost:5113/mcp/mail")
         .WithTitle("mail")
         .Build();
    // ... etc for me, m365-copilot
});

// During startup:
await app.InitializeMcpProxyAsync(ct);
app.MapPerServerMcpEndpoints();
```

When the Copilot SDK connects to `http://localhost:{random_port}/mcp/calendar`,
the McpManager proxy delegates to the mcpproxy CLI at
`http://localhost:5113/mcp/calendar`.

## Observed Behavior

### Test 1: REST sub-routes — CORRECT (per-server isolation works)

```
POST http://localhost:5113/mcp/calendar/tools/list → 13 tools (calendar only)
POST http://localhost:5113/mcp/mail/tools/list     → 22 tools (mail only)
POST http://localhost:5113/mcp/me/tools/list       →  5 tools (me only)
POST http://localhost:5113/mcp/m365-copilot/tools/list → 1 tool (copilot only)
```

### Test 2: MCP Streamable HTTP — BROKEN on v1.14.0

On v1.14.0, every per-server MCP endpoint returned ALL 41 tools from ALL
backends instead of just that server's tools:

```
MCP connect to /mcp/calendar     → 41 tools (all backends leaked)
MCP connect to /mcp/mail         → 41 tools (all backends leaked)
MCP connect to /mcp/me           → 41 tools (all backends leaked)
MCP connect to /mcp/m365-copilot → 41 tools (all backends leaked)
```

This was confirmed fixed in the v1.15.0 changelog:
> Fixed: Per-server MCP endpoints now support full MCP Streamable HTTP protocol

### Test 3: After upgrading to v1.15.0 — STILL BROKEN for downstream consumers

After upgrading both the mcpproxy CLI and McpProxy.Sdk to v1.15.0, the
downstream consumer (Orchestra) still cannot discover MCP tools through the
proxy chain. Across **all executions** (6 runs tested), the behavior is:

| MCP Server   | Transport Status | Tools Discovered |
|-------------|-----------------|-----------------|
| calendar     | Connected        | **0**           |
| mail         | Connected        | **0**           |
| me           | Connected        | **0**           |
| m365-copilot | Connected        | **0**           |

The MCP connection succeeds at the transport level, but the Copilot SDK sees
**zero MCP tools** — only its built-in tools (powershell, view, edit, etc.).
The model correctly reports: "No Calendar MCP is connected."

The token budget at session start is ~14,200 tokens (system prompt + built-in
tool definitions only). If MCP tools were loaded, this would be significantly
higher.

### Test 4: Direct client connection to v1.15.0 proxy — NOT YET CONFIRMED

We were unable to re-run the direct MCP Streamable HTTP test against the
v1.15.0 proxy because the service was down at the time of testing. The v1.14.0
test results showed all 41 tools leaking to every endpoint.

## Expected Behavior

1. `MCP connect to /mcp/calendar` should return only calendar's 13 tools
2. `MCP connect to /mcp/mail` should return only mail's 22 tools
3. A downstream McpProxy.Sdk instance connecting to these per-server endpoints
   as HTTP backends should discover and re-expose those tools correctly
4. The Copilot SDK connecting through the double-proxy chain should see the
   correct per-server tools

## Possible Root Causes

1. **`deferConnection: true` + `tools/list` race**: The proxy may accept the
   MCP connection and respond to `initialize` before the backend has connected.
   If `tools/list` completes before the backend finishes authenticating
   (InteractiveBrowser auth with deferred connection), the proxy returns an
   empty tool list that gets cached by the downstream consumer.

2. **Double-proxy initialization**: When `InitializeMcpProxyAsync()` runs on the
   McpManager proxy, it connects to all four per-server endpoints on port 5113.
   If the backends aren't ready yet (deferred), the initialization may cache
   empty tool lists. Subsequent `tools/list` requests from the Copilot SDK may
   receive the cached empty list rather than re-querying the backend.

3. **MCP Streamable HTTP per-server endpoint behavior**: Even after the v1.15.0
   fix, the per-server MCP endpoints might not correctly delegate `tools/list`
   to the specific backend when the endpoint is consumed by another proxy (not
   a direct client).

## Reproduction Steps

### Quick test (direct client to proxy)

A test script is provided that connects directly to the proxy and tests both
REST and MCP Streamable HTTP tool discovery:

```
dotnet run --file McpProxyToolDiscoveryTest.cs -- --port 5113
```

**Expected output** (per-server isolation working):
```
[2] REST tools/list
    calendar: 200 OK → 13 tool(s)
    mail:     200 OK → 22 tool(s)

[3] MCP Streamable HTTP
    calendar: Connected → 13 tool(s)   ← should match REST count
    mail:     Connected → 22 tool(s)   ← should match REST count
```

**Actual output on v1.14.0** (tool isolation broken):
```
[2] REST tools/list
    calendar: 200 OK → 13 tool(s)     ← correct
    mail:     200 OK → 22 tool(s)     ← correct

[3] MCP Streamable HTTP
    calendar: Connected → 41 tool(s)  ← WRONG: all backends leaked
    mail:     Connected → 41 tool(s)  ← WRONG: all backends leaked
```

### Double-proxy test (simulates the consumer architecture)

To reproduce the full scenario where a downstream McpProxy.Sdk consumer
connects to the mcpproxy CLI's per-server endpoints:

1. Start the mcpproxy CLI with the config above on port 5113
2. Create an in-process McpProxy.Sdk proxy that adds the per-server endpoints
   as HTTP backends:
   ```csharp
   proxy.AddHttpServer("calendar", "http://localhost:5113/mcp/calendar");
   proxy.AddHttpServer("mail", "http://localhost:5113/mcp/mail");
   ```
3. Call `InitializeMcpProxyAsync()` on the SDK proxy
4. Connect an MCP client to the SDK proxy's per-server endpoint and call
   `ListToolsAsync()`
5. Observe that zero tools are returned

## Test Script

```csharp
#:package ModelContextProtocol
#:property PublishAot=false

using ModelContextProtocol.Client;
using System.Text.Json;

var port = 5113;
var servers = new[] { "calendar", "mail", "me", "m365-copilot" };
string? singleServer = null;

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
Console.WriteLine(new string('-', 60));

// Test 1: HTTP connectivity
using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
try
{
    var response = await http.GetAsync(baseUrl);
    Console.WriteLine($"[1] GET {baseUrl} -> {(int)response.StatusCode}");
}
catch (Exception ex)
{
    Console.WriteLine($"[1] GET {baseUrl} -> FAILED: {ex.Message}");
    return;
}

// Test 2: REST tools/list per-server
Console.WriteLine("\n[2] REST tools/list (POST {endpoint}/tools/list)");
foreach (var server in servers)
{
    var endpoint = $"{baseUrl}/{server}";
    try
    {
        var response = await http.PostAsync(
            $"{endpoint}/tools/list",
            new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));

        if (response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            var json = JsonDocument.Parse(body);
            if (json.RootElement.TryGetProperty("tools", out var tools))
            {
                var count = tools.GetArrayLength();
                Console.WriteLine($"    {server}: {count} tool(s)");
            }
        }
        else
        {
            Console.WriteLine($"    {server}: {(int)response.StatusCode}");
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"    {server}: FAILED -> {ex.Message}");
    }
}

// Test 3: MCP Streamable HTTP (full handshake)
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
        Console.WriteLine($"    {server}: Connected -> {tools.Count} tool(s)");
        foreach (var tool in tools)
            Console.WriteLine($"      - {tool.Name}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"    {server}: FAILED -> {ex.GetType().Name}: {ex.Message}");
    }
}

Console.WriteLine("\nDone.");
```

## Questions for the McpProxy Team

1. When `deferConnection: true` is set and a client connects via MCP Streamable
   HTTP, does `tools/list` wait for the backend to finish connecting and
   authenticating? Or does it return immediately with whatever tools are
   currently known (possibly empty)?

2. When one McpProxy.Sdk instance connects to another mcpproxy CLI's per-server
   endpoint as an HTTP backend (`AddHttpServer("calendar",
   "http://localhost:5113/mcp/calendar")`), does `InitializeMcpProxyAsync()`
   correctly discover tools from that endpoint? Is this double-proxy scenario
   supported/tested?

3. Is there a `tools/list_changed` notification mechanism that would allow a
   downstream proxy to update its tool cache when the backend finishes
   connecting (relevant for `deferConnection: true`)?
