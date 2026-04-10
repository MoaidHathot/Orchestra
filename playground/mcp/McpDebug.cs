#:package ModelContextProtocol
#:package Microsoft.Extensions.Hosting
#:property PublishAot=false

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using System.Diagnostics;
using System.Text.Json;

// ── Instance identity ──────────────────────────────────────────────────
// A fresh GUID is generated every time this process starts.
// This lets MCP clients detect whether a new process was spawned.
var instanceId = Guid.NewGuid().ToString("D");
var startTime = DateTimeOffset.UtcNow;
var processId = Environment.ProcessId;
var invocationCount = 0;

// ── Build the MCP server ───────────────────────────────────────────────
var builder = Host.CreateApplicationBuilder(args);

builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

var toolName = $"Debug_tool_{instanceId}";

var tool = McpServerTool.Create(
    () =>
    {
        var currentInvocation = Interlocked.Increment(ref invocationCount);

        return JsonSerializer.Serialize(new
        {
            instanceId,
            toolName,
            invocation = currentInvocation,
            process = new
            {
                pid = processId,
                startTime = startTime.ToString("o"),
                uptime = (DateTimeOffset.UtcNow - startTime).ToString(),
                machineName = Environment.MachineName,
                osDescription = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
                dotnetVersion = Environment.Version.ToString(),
                frameworkDescription = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            },
            workingDirectory = Environment.CurrentDirectory,
            arguments = Environment.GetCommandLineArgs(),
            environmentVariables = Environment.GetEnvironmentVariables()
                .Cast<System.Collections.DictionaryEntry>()
                .Where(e => e.Key.ToString()!.StartsWith("MCP_", StringComparison.OrdinalIgnoreCase))
                .ToDictionary(e => e.Key.ToString()!, e => e.Value?.ToString()),
        }, new JsonSerializerOptions { WriteIndented = true });
    },
    new McpServerToolCreateOptions
    {
        Name = toolName,
        Title = "MCP Debug / Lifecycle Test Tool",
        Description =
            "Returns diagnostic information about this MCP server instance. " +
            "Use it to verify process identity, detect duplicate instances, " +
            "and confirm environment propagation from the MCP client.",
        ReadOnly = true,
        Destructive = false,
        Idempotent = false,  // invocation counter increments each call
        OpenWorld = false,
    });

builder.Services.AddSingleton(tool);

builder.Services.AddMcpServer(o =>
{
    o.ServerInfo = new()
    {
        Name = "mcp-debug",
        Version = "1.0.0",
    };
}).WithStdioServerTransport();

await builder.Build().RunAsync();
