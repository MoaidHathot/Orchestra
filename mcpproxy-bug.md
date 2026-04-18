# McpProxy SDK 1.12.0: `InitializeMcpProxyAsync` hangs with PerServer routing

## Environment

- McpProxy.Sdk 1.12.0
- ModelContextProtocol 1.2.0 / ModelContextProtocol.AspNetCore 1.2.0
- .NET 10.0, Windows

## Summary

`InitializeMcpProxyAsync` hangs indefinitely when `RoutingMode.PerServer` is configured. All backends connect or defer successfully, but the method never returns. This happens with both the builder API (`AddMcpProxy(proxy => { ... })`) and the config API (`AddMcpProxy(McpProxySdkConfiguration)`).

The v1.12.0 fixes for `CancellationToken` observance and `ServerTransportType.Http` transport mode did not resolve the underlying hang. The hang occurs in the post-connection phase (likely the `SingleServerProxy` hook pipeline configuration step), not during backend connection itself.

## What was fixed in 1.12.0 (confirmed working)

- `WithRouting(RoutingMode, string)` now exists on `IMcpProxyBuilder` -- confirmed, builds and compiles.
- `CancellationToken` propagation -- not testable since the hang is in a step that doesn't yield to cancellation checkpoints.
- `ServerTransportType.Http` backends now use `StreamableHttp` mode -- the logs show `(Http)` instead of `(Sse)`.

## What is still broken

`InitializeMcpProxyAsync` hangs after all backends are processed when `RoutingMode.PerServer` is configured. The method never returns regardless of whether the builder API or config API is used.

## Reproduction

### Builder API (recommended path per README)

```csharp
using McpProxy.Sdk.Configuration;
using McpProxy.Sdk.Sdk;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Server;

// 1. Start a simple Streamable HTTP backend
var backendBuilder = WebApplication.CreateSlimBuilder();
backendBuilder.WebHost.UseUrls("http://127.0.0.1:5200");
backendBuilder.Services.AddMcpServer(o =>
    o.ServerInfo = new() { Name = "test-backend", Version = "1.0" })
    .WithHttpTransport()
    .WithTools([McpServerTool.Create(
        (CancellationToken _) => "hello",
        new McpServerToolCreateOptions { Name = "test_tool" })]);
var backend = backendBuilder.Build();
backend.MapMcp("/mcp");
await backend.StartAsync();

// 2. Configure proxy with PerServer routing via builder API
var proxyBuilder = WebApplication.CreateSlimBuilder();
proxyBuilder.WebHost.UseUrls("http://127.0.0.1:5300");

proxyBuilder.Services.AddMcpProxy(proxy =>
{
    proxy.WithServerInfo("Test Proxy", "1.0.0");
    proxy.WithRouting(RoutingMode.PerServer, "/mcp");

    proxy.AddHttpServer("my-backend", "http://localhost:5200/mcp")
        .WithTitle("My Backend")
        .Build();
});

proxyBuilder.Services.AddMcpServer().WithHttpTransport().WithSdkProxyHandlers();
var proxy = proxyBuilder.Build();

// 3. This hangs indefinitely
Console.WriteLine("Calling InitializeMcpProxyAsync...");
await proxy.InitializeMcpProxyAsync();
Console.WriteLine("This line is never reached");
```

### Config API (alternative path)

```csharp
var sdkConfig = new McpProxySdkConfiguration
{
    Configuration = new ProxyConfiguration
    {
        Proxy = new ProxySettings
        {
            ServerInfo = new ServerInfo { Name = "Test", Version = "1.0.0" },
            Routing = new RoutingConfiguration
            {
                Mode = RoutingMode.PerServer,
                BasePath = "/mcp",
            },
        },
        Mcp = new Dictionary<string, ServerConfiguration>
        {
            ["my-backend"] = new ServerConfiguration
            {
                Type = ServerTransportType.Http,
                Title = "My Backend",
                Url = "http://localhost:5200/mcp",
                Route = "/my-backend",
            },
        },
    },
    GlobalPreInvokeHooks = [],
    GlobalPostInvokeHooks = [],
    VirtualTools = [],
    ToolInterceptors = [],
    ToolCallInterceptors = [],
    ServerStates = new Dictionary<string, ServerState>(),
};

var proxyBuilder = WebApplication.CreateSlimBuilder();
proxyBuilder.Services.AddMcpProxy(sdkConfig);
proxyBuilder.Services.AddMcpServer().WithHttpTransport().WithSdkProxyHandlers();
var proxy = proxyBuilder.Build();

await proxy.InitializeMcpProxyAsync(); // <-- hangs
```

### Both hang identically. Removing `RoutingMode.PerServer` (using default unified routing) resolves the hang.

## Observed logs (production environment, 10 backends)

```
Connecting to backend server 'calendar' (Http)
Failed to connect to backend server 'calendar'
  System.Net.Http.HttpRequestException: 404 (Not Found)
Backend server 'calendar' connection deferred until first request

Connecting to backend server 'mail' (Http)
  ...
Backend server 'mail' connection deferred until first request

Connecting to backend server 'm365-copilot' (Http)
  ...
Backend server 'm365-copilot' connection deferred until first request

Connecting to backend server 'me' (Http)
  ...
Backend server 'me' connection deferred until first request

Connecting to backend server 'icm-full' (Stdio)
Connected to backend server 'icm-full'

Connecting to backend server 'icm-readonly' (Stdio)
Connected to backend server 'icm-readonly'

Connecting to backend server 'azdo' (Stdio)
Connected to backend server 'azdo'

Connecting to backend server 'azure' (Stdio)
Connected to backend server 'azure'

Connecting to backend server 'powerreview' (Stdio)
Connected to backend server 'powerreview'

Connecting to backend server 'debug-mcp' (Stdio)
Connected to backend server 'debug-mcp'

<--- HANGS HERE. All 10 backends processed. Method never returns. --->
```

## Diagnostic results

| Test | Result | Conclusion |
|---|---|---|
| `McpClient.CreateAsync` with `HttpClientTransport` to backend directly | Passed (147ms) | Backends work fine |
| GET to backend `/mcp` | Returned in 18ms | No SSE hang |
| Thread pool state after starting backends | Normal | No starvation |
| `InitializeMcpProxyAsync` with `RoutingMode.PerServer` | Hangs indefinitely | Bug is specific to PerServer routing |
| `InitializeMcpProxyAsync` without PerServer routing (1.3.0) | Completes normally | Regression from PerServer feature |

## Root cause hypothesis

The v1.10.0 changelog states:
> `InitializeMcpProxyAsync` updated to configure hook pipelines on `SingleServerProxy` instances

This post-connection hook pipeline configuration step hangs. All backend connections complete (visible in logs), but the subsequent `SingleServerProxy` setup never finishes. The hang is deterministic and occurs every time `PerServer` routing is configured.

## Current workaround

```csharp
var initTask = app.InitializeMcpProxyAsync(cancellationToken);
var completed = await Task.WhenAny(initTask, Task.Delay(TimeSpan.FromSeconds(30), cancellationToken));
if (completed != initTask)
{
    // Log warning — per-server tool isolation may be degraded
}

app.MapMcp("/mcp");
app.MapPerServerMcpEndpoints();
await app.StartAsync();
```

This unblocks the host, but `MapPerServerMcpEndpoints()` may not register `SingleServerProxy` instances since initialization didn't complete. Per-server tool isolation is not guaranteed.
