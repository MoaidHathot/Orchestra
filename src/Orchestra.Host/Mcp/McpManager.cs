using System.Net;
using System.Net.Sockets;
using McpProxy.Abstractions;
using McpProxy.Sdk.Configuration;
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
	/// The per-server proxy base URL (e.g. <c>http://localhost:{port}/mcp</c>).
	/// Individual server routes are at <c>{baseUrl}/{serverName}</c>.
	/// </summary>
	private string? _proxyBaseUrl;

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

		// Build the per-server proxy base URL
		_proxyBaseUrl = $"http://localhost:{_proxyPort}/mcp";

		// Start the in-process proxy
		try
		{
			await StartProxyAsync(globalMcps, cancellationToken);
		}
		catch (Exception)
		{
			// StartProxyAsync is expected to handle its own exceptions internally,
			// but if a subclass override throws, we handle it here as a fallback.
			_proxyBaseUrl = null;
		}

		if (_proxyBaseUrl is not null)
			LogProxyStarted(_proxyPort, globalMcps.Length, string.Join(", ", globalMcps.Select(m => m.Name)));
	}

	/// <summary>
	/// Resolves MCPs for a step. Each global MCP (identified by name) is replaced
	/// with a <see cref="RemoteMcp"/> pointing to its per-server proxy route
	/// (e.g. <c>http://localhost:{port}/mcp/{name}</c>).
	/// Inline MCPs are returned unchanged.
	/// </summary>
	/// <remarks>
	/// Name-based matching is used instead of reference equality because upstream
	/// template resolution (<see cref="TemplateResolver.ResolveStaticMcp"/>) creates
	/// new MCP object instances, which would break reference-equality checks.
	/// </remarks>
	public Engine.Mcp[] Resolve(Engine.Mcp[] mcps)
	{
		if (_globalMcpNames.Count == 0 || mcps.Length == 0 || _proxyBaseUrl is null)
			return mcps;

		var result = new List<Engine.Mcp>(mcps.Length);
		var hasAnyGlobal = false;

		foreach (var mcp in mcps)
		{
			if (_globalMcpNames.Contains(mcp.Name))
			{
				hasAnyGlobal = true;
				// Replace with a RemoteMcp pointing to this server's per-server proxy route
				result.Add(new RemoteMcp
				{
					Name = mcp.Name,
					Type = McpType.Remote,
					Endpoint = $"{_proxyBaseUrl}/{mcp.Name}",
					Headers = [],
				});
			}
			else
			{
				result.Add(mcp);
			}
		}

		if (!hasAnyGlobal)
			return mcps;

		return [.. result];
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

		// Configure the MCP proxy with per-server routing.
		// Each global MCP gets its own isolated route: /mcp/{serverName}
		builder.Services.AddMcpProxy(proxy =>
		{
			proxy.WithServerInfo(
				"Orchestra MCP Proxy",
				"1.0.0",
				"Shared MCP proxy managed by Orchestra Host.");

			proxy.WithRouting(RoutingMode.PerServer, "/mcp");

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
						var serverBuilder = proxy.AddHttpServer(mcp.Name, remote.Endpoint)
							.WithTitle(mcp.Name);
						if (remote.Headers.Count > 0)
						{
							serverBuilder.WithHeaders(remote.Headers.ToDictionary(
								h => h.Key, h => h.Value));
						}
						serverBuilder.Build();
						break;
				}
			}
		});

		// Register the unified MCP server with SDK proxy handlers.
		// In SDK 1.14.0+, WithSdkProxyHandlers() is route-aware: on per-server
		// routes it delegates to SingleServerProxy for tool isolation.
		builder.Services
			.AddMcpServer()
			.WithHttpTransport()
			.WithSdkProxyHandlers();

		_proxyApp = builder.Build();

		// Initialize backend connections and configure SingleServerProxy hook pipelines
		await _proxyApp.InitializeMcpProxyAsync(cancellationToken);

		// Map unified endpoint (all tools aggregated) and per-server endpoints
		// (isolated tools per backend, both MCP Streamable HTTP and REST sub-routes).
		_proxyApp.MapMcp("/mcp");
		_proxyApp.MapPerServerMcpEndpoints();

		// Start the host (non-blocking)
		await _proxyApp.StartAsync(cancellationToken);

		LogProxyReady(_proxyPort);
		}
		catch (Exception ex)
		{
			LogProxyStartFailed(ex);
			_proxyApp = null;
			_proxyBaseUrl = null;
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
