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
using Orchestra.Host.McpServer;

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
	private readonly McpServerOptions _mcpServerOptions;

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
	private bool _stopped;

	public McpManager(ILogger<McpManager> logger, McpServerOptions? mcpServerOptions = null)
	{
		_logger = logger;
		_mcpServerOptions = mcpServerOptions ?? new McpServerOptions();
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
	/// Inline MCPs are returned unchanged, except that any <see cref="RemoteMcp"/>
	/// targeting Orchestra's own data-plane endpoint without an explicit
	/// <see cref="Engine.Mcp.Timeout"/> has the configured
	/// <see cref="McpServerOptions.DefaultOrchestraInvokeTimeoutSeconds"/> applied
	/// so that long-running <c>invoke_orchestration</c> calls in sync mode do not
	/// hit the Copilot SDK's ~3-minute default MCP request timeout.
	///
	/// When <paramref name="parent"/> is supplied, additionally stamps parent-execution
	/// headers (see <see cref="OrchestraHeaders"/>) on any <see cref="RemoteMcp"/> whose
	/// endpoint targets this Orchestra host. The headers let server-side MCP tool handlers
	/// (e.g. <c>DataPlaneTools.InvokeOrchestration</c>) auto-populate <c>parentExecutionId</c>
	/// for nested invocations, restoring run lineage that was previously lost when an LLM
	/// agent recursively invoked orchestrations through MCP.
	/// Endpoints that are NOT Orchestra-owned receive no headers (avoiding leakage of
	/// internal execution IDs to foreign servers).
	/// </summary>
	/// <remarks>
	/// Name-based matching is used instead of reference equality because upstream
	/// template resolution (<see cref="TemplateResolver.ResolveStaticMcp"/>) creates
	/// new MCP object instances, which would break reference-equality checks.
	/// </remarks>
	public Engine.Mcp[] Resolve(Engine.Mcp[] mcps, ParentExecutionAnnotation? parent = null)
	{
		if (mcps.Length == 0)
			return mcps;

		var result = new List<Engine.Mcp>(mcps.Length);
		var changed = false;

		foreach (var mcp in mcps)
		{
			Engine.Mcp current = mcp;

			// Step 1 — replace global MCPs (defined in orchestra.mcp.json) with a
			// RemoteMcp pointing at the per-server proxy route.
			if (_globalMcpNames.Contains(mcp.Name) && _proxyBaseUrl is not null)
			{
				// Look up the original global definition so we can preserve its semantic
				// timeout when the orchestration didn't override it.
				var originalGlobal = _globalMcpList.FirstOrDefault(g =>
					string.Equals(g.Name, mcp.Name, StringComparison.OrdinalIgnoreCase));

				current = new RemoteMcp
				{
					Name = mcp.Name,
					Type = McpType.Remote,
					Endpoint = $"{_proxyBaseUrl}/{mcp.Name}",
					Headers = [],
					Timeout = mcp.Timeout ?? originalGlobal?.Timeout, // Orchestration override > global default
				};
				changed = true;
			}

			// Step 2 — for the Orchestra data-plane MCP specifically, apply the configured
			// default timeout when neither the orchestration nor a global definition supplied
			// one. Detection considers both the inline MCP (current.Endpoint == /mcp/data)
			// and global MCPs whose original definition pointed at /mcp/data, since by step 1
			// the endpoint has already been rewritten to the proxy URL for global ones.
			if (current.Timeout is null && _mcpServerOptions.DefaultOrchestraInvokeTimeoutSeconds > 0)
			{
				var originalGlobal = _globalMcpNames.Contains(mcp.Name)
					? _globalMcpList.FirstOrDefault(g =>
						string.Equals(g.Name, mcp.Name, StringComparison.OrdinalIgnoreCase))
					: null;

				if (TargetsOrchestraDataPlane(mcp) || TargetsOrchestraDataPlane(originalGlobal))
				{
					var defaultTimeout = TimeSpan.FromSeconds(_mcpServerOptions.DefaultOrchestraInvokeTimeoutSeconds);

					current = current switch
					{
						RemoteMcp r => new RemoteMcp
						{
							Name = r.Name,
							Type = r.Type,
							Endpoint = r.Endpoint,
							Headers = r.Headers,
							Timeout = defaultTimeout,
						},
						LocalMcp l => new LocalMcp
						{
							Name = l.Name,
							Type = l.Type,
							Command = l.Command,
							Arguments = l.Arguments,
							WorkingDirectory = l.WorkingDirectory,
							Timeout = defaultTimeout,
						},
						_ => current,
					};

					LogAppliedDataPlaneDefaultTimeout(mcp.Name, _mcpServerOptions.DefaultOrchestraInvokeTimeoutSeconds);
					changed = true;
				}
			}

			// Step 3 — when a parent annotation is supplied, stamp parent-execution headers
			// on any RemoteMcp whose endpoint targets this Orchestra host. Headers are
			// overwritten (not merged with caller-supplied values) so the orchestration YAML
			// cannot spoof the parent ID.
			if (parent is not null
				&& current is RemoteMcp remoteForParent
				&& IsOrchestraOwnedEndpoint(remoteForParent.Endpoint))
			{
				var headers = new Dictionary<string, string>(remoteForParent.Headers, StringComparer.OrdinalIgnoreCase)
				{
					[OrchestraHeaders.ParentExecutionId] = parent.ExecutionId,
					[OrchestraHeaders.ParentOrchestrationName] = parent.OrchestrationName,
					[OrchestraHeaders.ParentStepName] = parent.StepName,
				};

				current = new RemoteMcp
				{
					Name = remoteForParent.Name,
					Type = remoteForParent.Type,
					Endpoint = remoteForParent.Endpoint,
					Headers = headers,
					Timeout = remoteForParent.Timeout,
				};
				changed = true;
			}

			result.Add(current);
		}

		return changed ? [.. result] : mcps;
	}

	/// <summary>
	/// Returns <c>true</c> when the given endpoint URL targets this Orchestra host's own
	/// MCP surface — either the data plane directly or the per-MCP proxy route that
	/// global MCPs are rewritten to in <see cref="Resolve(Engine.Mcp[], ParentExecutionAnnotation?)"/>.
	/// </summary>
	private bool IsOrchestraOwnedEndpoint(string? endpoint)
	{
		if (string.IsNullOrWhiteSpace(endpoint))
		{
			return false;
		}

		// Direct data-plane endpoint (handles inline orchestration MCPs that target /mcp/data).
		if (TargetsOrchestraDataPlane(new RemoteMcp { Name = "_", Type = McpType.Remote, Endpoint = endpoint!, Headers = [] }))
		{
			return true;
		}

		// Global-MCP proxy rewrites point at the local proxy base URL; those proxies forward
		// to the underlying server, which may itself be the Orchestra data plane.
		if (_proxyBaseUrl is not null
			&& endpoint!.StartsWith(_proxyBaseUrl, StringComparison.OrdinalIgnoreCase))
		{
			return true;
		}

		return false;
	}

	/// <summary>
	/// Returns <c>true</c> when the given MCP is a <see cref="RemoteMcp"/> whose endpoint
	/// targets this host's configured data-plane route (default <c>/mcp/data</c>). The
	/// match compares the URL path component (case-insensitive) so it tolerates port
	/// numbers, schemes, and trailing slashes.
	/// </summary>
	private bool TargetsOrchestraDataPlane(Engine.Mcp? mcp)
	{
		if (mcp is not RemoteMcp remote || string.IsNullOrEmpty(remote.Endpoint))
			return false;

		var configuredRoute = (_mcpServerOptions.DataPlaneRoute ?? string.Empty).TrimEnd('/');
		if (configuredRoute.Length == 0)
			return false;

		// Try parsing as an absolute URI so we can match on the path component without
		// being fooled by scheme/host/port/query differences.
		if (Uri.TryCreate(remote.Endpoint, UriKind.Absolute, out var uri))
		{
			var path = uri.AbsolutePath.TrimEnd('/');
			return path.Equals(configuredRoute, StringComparison.OrdinalIgnoreCase)
				|| path.EndsWith(configuredRoute, StringComparison.OrdinalIgnoreCase);
		}

		// Fallback: substring match for unparseable endpoints (e.g., still-templated values).
		return remote.Endpoint.Contains(configuredRoute, StringComparison.OrdinalIgnoreCase);
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

	public async Task StopAsync(CancellationToken cancellationToken = default)
	{
		if (_stopped)
			return;

		_stopped = true;

		if (_proxyApp is not null)
		{
			try
			{
				await _proxyApp.StopAsync(cancellationToken);
				await _proxyApp.DisposeAsync();
			}
			catch (Exception ex)
			{
				LogProxyStopError(ex.Message);
			}
			finally
			{
				_proxyApp = null;
				_proxyBaseUrl = null;
			}
		}

		LogProxyStopped();
	}

	public async ValueTask DisposeAsync()
	{
		await StopAsync();
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

	[LoggerMessage(
		EventId = 8,
		Level = LogLevel.Information,
		Message = "Applied default Orchestra data-plane MCP timeout {DefaultTimeoutSeconds}s to MCP entry '{McpName}' (no timeoutSeconds set on the orchestration's mcps[] entry).")]
	private partial void LogAppliedDataPlaneDefaultTimeout(string mcpName, int defaultTimeoutSeconds);

	#endregion
}
