using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using Orchestra.Engine;

namespace Orchestra.OpenCode;

/// <summary>
/// A tiny loopback MCP server (one per OpenCode worker) that exposes Orchestra's engine tools
/// (<c>orchestra_set_status</c>, <c>orchestra_complete</c>, <c>orchestra_save_file</c>,
/// <c>orchestra_read_file</c>, <c>orchestra_request_user_input</c>) so the OpenCode server can
/// call back into the host. Bound to a single <see cref="EngineToolContextHolder"/>, so the
/// tools always act on whichever step currently leases the worker — no token routing needed
/// because each worker hosts at most one in-flight step (<c>MaxSessionsPerInstance = 1</c>).
/// </summary>
internal sealed class OpenCodeEngineToolBridge : IAsyncDisposable
{
	private readonly WebApplication _app;

	private OpenCodeEngineToolBridge(WebApplication app, string mcpUrl)
	{
		_app = app;
		McpUrl = mcpUrl;
	}

	/// <summary>The MCP endpoint URL OpenCode connects to (e.g. <c>http://127.0.0.1:49xxx/mcp</c>).</summary>
	public string McpUrl { get; }

	public static async Task<OpenCodeEngineToolBridge> StartAsync(
		EngineToolContextHolder holder,
		string hostname,
		ILoggerFactory loggerFactory,
		CancellationToken cancellationToken)
	{
		// The fixed engine-tool catalogue. Definitions (name/description/schema) come from the
		// built-in tools; per-step enablement is enforced at dispatch against the bound holder.
		IEngineTool[] definitions =
		[
			new SetStatusTool(),
			new CompleteTool(),
			new SaveToFileTool(),
			new ReadFromFileTool(),
			new RequestUserInputTool(),
		];
		var tools = definitions
			.Select(d => McpServerTool.Create(new OpenCodeEngineToolFunction(d, holder)))
			.ToList();

		var builder = WebApplication.CreateSlimBuilder();
		builder.Logging.ClearProviders();
		builder.Services.AddSingleton(loggerFactory);

		builder.Services
			.AddMcpServer(o => o.ServerInfo = new() { Name = "orchestra-engine-tools", Version = "1.0.0" })
			.WithHttpTransport()
			.WithTools(tools);

		var app = builder.Build();
		app.Urls.Add($"http://{hostname}:0");
		app.MapMcp("/mcp");
		await app.StartAsync(cancellationToken).ConfigureAwait(false);

		var baseUrl = app.Urls.FirstOrDefault()?.TrimEnd('/')
			?? throw new InvalidOperationException("OpenCode engine-tool bridge did not bind a URL.");
		return new OpenCodeEngineToolBridge(app, baseUrl + "/mcp");
	}

	public async ValueTask DisposeAsync()
	{
		try
		{
			using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
			await _app.StopAsync(cts.Token).ConfigureAwait(false);
		}
		catch
		{
			// Best-effort shutdown.
		}
		await _app.DisposeAsync().ConfigureAwait(false);
	}
}
