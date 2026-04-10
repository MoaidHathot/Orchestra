using System.Net;
using System.Net.Sockets;
using McpProxy.Sdk.Sdk;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orchestra.Engine;

namespace Orchestra.Host.Mcp;

/// <summary>
/// Manages globally shared MCP servers defined in the global orchestra.mcp.json file.
/// Uses the McpProxy SDK to host an in-process proxy that aggregates all global
/// MCP servers into a single Streamable HTTP endpoint. Steps that reference global
/// MCPs have their configurations transparently replaced with a single
/// <see cref="RemoteMcp"/> pointing to the unified proxy endpoint.
/// </summary>
public partial class McpManager : IMcpResolver, IAsyncDisposable
{
	private readonly ILogger<McpManager> _logger;

	/// <summary>
	/// The names of global MCP servers managed by this instance.
	/// Used for name-based matching in <see cref="Resolve"/> so that cloned/template-resolved
	/// copies of global MCPs are still correctly identified and routed through the proxy.
	/// </summary>
	private readonly HashSet<string> _globalMcpNames = new(StringComparer.OrdinalIgnoreCase);

	/// <summary>
	/// The original global <see cref="Engine.Mcp"/> objects from orchestra.mcp.json,
	/// exposed via <see cref="GlobalMcps"/> for other components to inspect.
	/// </summary>
	private readonly List<Engine.Mcp> _globalMcpList = [];

	/// <summary>
	/// The in-process WebApplication hosting the MCP proxy.
	/// </summary>
	private WebApplication? _proxyApp;

	/// <summary>
	/// The unified proxy endpoint URL (Streamable HTTP).
	/// </summary>
	private string? _proxyEndpoint;

	/// <summary>
	/// The port the proxy is listening on.
	/// </summary>
	private int _proxyPort;

	/// <summary>
	/// Whether the manager has been initialized with global MCPs.
	/// </summary>
	private bool _initialized;

	public McpManager(ILogger<McpManager> logger)
	{
		_logger = logger;
	}

	/// <summary>
	/// Gets all globally managed MCPs (the original config objects from orchestra.mcp.json).
	/// </summary>
	public IReadOnlyCollection<Engine.Mcp> GlobalMcps => _globalMcpList;

	/// <summary>
	/// Gets whether the proxy is running and managing global MCPs.
	/// </summary>
	public bool IsRunning => _proxyApp is not null;

	/// <summary>
	/// Initializes the MCP proxy with the given global MCPs from orchestra.mcp.json.
	/// Starts an in-process proxy using the McpProxy SDK.
	/// </summary>
	public async Task InitializeAsync(Engine.Mcp[] globalMcps, CancellationToken cancellationToken = default)
	{
		if (_initialized)
			throw new InvalidOperationException("McpManager has already been initialized.");

		_initialized = true;

		if (globalMcps.Length == 0)
		{
			LogNoGlobalMcps();
			return;
		}

		// Track the global MCP names for name-based matching in Resolve
		foreach (var mcp in globalMcps)
		{
			_globalMcpNames.Add(mcp.Name);
			_globalMcpList.Add(mcp);
		}

		// Find an available port
		_proxyPort = GetAvailablePort();

		// Build the unified proxy endpoint
		_proxyEndpoint = $"http://localhost:{_proxyPort}/mcp";

		// Start the in-process proxy
		await StartProxyAsync(globalMcps, cancellationToken);

		LogProxyStarted(_proxyPort, globalMcps.Length, string.Join(", ", globalMcps.Select(m => m.Name)));
	}

	/// <summary>
	/// Resolves MCPs for a step. All global MCPs (identified by name) are replaced
	/// with a single <see cref="RemoteMcp"/> pointing to the unified proxy endpoint.
	/// Inline MCPs are returned unchanged.
	/// </summary>
	/// <remarks>
	/// Name-based matching is used instead of reference equality because upstream
	/// template resolution (<see cref="TemplateResolver.ResolveStaticMcp"/>) creates
	/// new MCP object instances, which would break reference-equality checks.
	/// </remarks>
	public Engine.Mcp[] Resolve(Engine.Mcp[] mcps)
	{
		if (_globalMcpNames.Count == 0 || mcps.Length == 0 || _proxyEndpoint is null)
			return mcps;

		var nonGlobals = new List<Engine.Mcp>();
		var hasAnyGlobal = false;

		foreach (var mcp in mcps)
		{
			if (_globalMcpNames.Contains(mcp.Name))
			{
				hasAnyGlobal = true;
			}
			else
			{
				nonGlobals.Add(mcp);
			}
		}

		if (!hasAnyGlobal)
			return mcps;

		// Replace all global MCP references with one unified proxy endpoint
		nonGlobals.Add(new RemoteMcp
		{
			Name = "orchestra-mcp-proxy",
			Type = McpType.Remote,
			Endpoint = _proxyEndpoint,
			Headers = [],
		});

		return [.. nonGlobals];
	}

	protected virtual async Task StartProxyAsync(Engine.Mcp[] globalMcps, CancellationToken cancellationToken)
	{
		try
		{
		var builder = WebApplication.CreateSlimBuilder();

		// Suppress Kestrel and hosting logs for the internal proxy
		builder.Logging.SetMinimumLevel(LogLevel.Warning);

		// Bind ONLY to our chosen port. UseUrls() replaces any addresses inherited
		// from the parent process (ASPNETCORE_URLS env var, launchSettings.json, etc.)
		// so Kestrel won't warn about "Overriding address(es)".
		builder.WebHost.UseUrls($"http://127.0.0.1:{_proxyPort}");

		// Configure the MCP proxy using the SDK
		builder.Services.AddMcpProxy(proxy =>
		{
			proxy.WithServerInfo(
				"Orchestra MCP Proxy",
				"1.0.0",
				"Shared MCP proxy managed by Orchestra Host.");

			foreach (var mcp in globalMcps)
			{
				switch (mcp)
				{
					case LocalMcp local:
						proxy.AddStdioServer(mcp.Name, local.Command, local.Arguments)
							.WithTitle(mcp.Name)
							.Build();
						break;

					case RemoteMcp remote:
						proxy.AddSseServer(mcp.Name, remote.Endpoint)
							.WithTitle(mcp.Name)
							.WithHeaders(remote.Headers)
							.Build();
						break;
				}
			}
		});

		// Register MCP server with HTTP transport and SDK proxy handlers
		builder.Services
			.AddMcpServer()
			.WithHttpTransport()
			.WithSdkProxyHandlers();

		_proxyApp = builder.Build();

		// Initialize backend connections
		await _proxyApp.InitializeMcpProxyAsync(cancellationToken);

		// Map the unified MCP Streamable HTTP endpoint
		_proxyApp.MapMcp("/mcp");

		// Start the host (non-blocking)
		await _proxyApp.StartAsync(cancellationToken);

		LogProxyReady(_proxyPort);
		}
		catch (Exception ex)
		{
			LogProxyStartFailed(ex);
			_proxyApp = null;
		}
	}

	private static int GetAvailablePort()
	{
		using var listener = new TcpListener(IPAddress.Loopback, 0);
		listener.Start();
		var port = ((IPEndPoint)listener.LocalEndpoint).Port;
		listener.Stop();
		return port;
	}

	public async ValueTask DisposeAsync()
	{
		if (_proxyApp is not null)
		{
			try
			{
				await _proxyApp.StopAsync();
				await _proxyApp.DisposeAsync();
			}
			catch (Exception ex)
			{
				LogProxyStopError(ex.Message);
			}
			finally
			{
				_proxyApp = null;
			}
		}

		LogProxyStopped();
	}

	#region Source-Generated Logging

	[LoggerMessage(
		EventId = 1,
		Level = LogLevel.Information,
		Message = "No global MCPs configured. MCP proxy will not be started.")]
	private partial void LogNoGlobalMcps();

	[LoggerMessage(
		EventId = 2,
		Level = LogLevel.Information,
		Message = "MCP proxy started on port {Port} with {Count} global MCP(s): [{McpNames}]")]
	private partial void LogProxyStarted(int port, int count, string mcpNames);

	[LoggerMessage(
		EventId = 3,
		Level = LogLevel.Information,
		Message = "MCP proxy is ready on port {Port}.")]
	private partial void LogProxyReady(int port);

	[LoggerMessage(Level = LogLevel.Error, Message = "MCP proxy failed to start. Global MCPs will be unavailable.")]
	private partial void LogProxyStartFailed(Exception ex);

	[LoggerMessage(
		EventId = 6,
		Level = LogLevel.Warning,
		Message = "Error stopping MCP proxy: {Error}")]
	private partial void LogProxyStopError(string error);

	[LoggerMessage(
		EventId = 7,
		Level = LogLevel.Information,
		Message = "MCP proxy stopped.")]
	private partial void LogProxyStopped();

	#endregion
}
