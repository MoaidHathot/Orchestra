using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Orchestra.Engine;
using Orchestra.Host.Mcp;
using Xunit;
using McpServerOptions = Orchestra.Host.McpServer.McpServerOptions;

namespace Orchestra.Host.Tests;

/// <summary>
/// Exercises real HTTP and stdio transports without external services, credentials, or packages.
/// Every global backend must pass startup validation before the host can serve runs.
/// </summary>
public class McpManagerStartupTests
{
	[Fact]
	public async Task InitializeAsync_ValidatesAllBackendsBeforeReturning()
	{
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
		await using var backendA = new HttpBackend();
		await using var backendB = new HttpBackend();
		await backendA.StartAsync(timeout.Token);
		await backendB.StartAsync(timeout.Token);
		await using var manager = CreateManager();
		var mcpA = CreateRemoteMcp("server-a", backendA.Endpoint);
		var mcpB = CreateRemoteMcp("server-b", backendB.Endpoint);

		await manager.InitializeAsync([mcpA, mcpB], timeout.Token);

		manager.IsRunning.Should().BeTrue();
		backendA.InitializeCount.Should().Be(1);
		backendB.InitializeCount.Should().Be(1);
		backendA.DiscoveryCount.Should().Be(1, "startup must also validate deferred upstream authentication");
		backendB.DiscoveryCount.Should().Be(1);

		await using var client = await ConnectAsync(manager, mcpA, timeout.Token);
		var tools = await client.ListToolsAsync(cancellationToken: timeout.Token);
		tools.Should().ContainSingle(t => t.Name == "echo");
		var result = await client.CallToolAsync("echo", cancellationToken: timeout.Token);
		result.Content.Should().ContainSingle().Which.Should().BeOfType<TextContentBlock>()
			.Which.Text.Should().Be("ready");
		backendA.InitializeCount.Should().Be(1, "discovery and invocation must share one backend session");
		backendB.DiscoveryCount.Should().Be(1, "using one route must not query another backend");
	}

	[Fact]
	public async Task InitializeAsync_OfflineBackend_FailsWithNameAndEndpoint()
	{
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
		// Reserve a non-listening port so the internal proxy cannot accidentally take it.
		using var reservation = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
		reservation.Bind(new IPEndPoint(IPAddress.Loopback, 0));
		var port = ((IPEndPoint)reservation.LocalEndPoint!).Port;
		await using var backend = new HttpBackend(port);
		await using var manager = CreateManager();
		var mcp = CreateRemoteMcp("calendar", $"http://127.0.0.1:{port}/mcp");
		var initialize = () => manager.InitializeAsync([mcp], timeout.Token);
		var failure = (await initialize.Should().ThrowAsync<McpInitializationException>()).Which;
		failure.ServerName.Should().Be("calendar");
		failure.Target.Should().Be(mcp.Endpoint);
		failure.InnerException.Should().BeOfType<HttpRequestException>();
		manager.IsRunning.Should().BeFalse();

		reservation.Dispose();
		await backend.StartAsync(timeout.Token);
		await using var retry = CreateManager();
		await retry.InitializeAsync([mcp], timeout.Token);
		retry.IsRunning.Should().BeTrue("restarting after fixing the backend must succeed");
		backend.InitializeCount.Should().Be(1);
	}

	[Fact]
	public async Task InitializeAsync_ExpiredHandshakeSession_FailsStartupWithOriginalError()
	{
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
		await using var backend = new HttpBackend { RejectSessionRequests = true };
		await backend.StartAsync(timeout.Token);
		await using var manager = CreateManager();
		var mcp = CreateRemoteMcp("calendar", backend.Endpoint);
		var initialize = () => manager.InitializeAsync([mcp], timeout.Token);
		var failure = (await initialize.Should().ThrowAsync<McpInitializationException>()).Which;
		failure.ServerName.Should().Be("calendar");
		failure.Message.Should().Contain("404");
		failure.InnerException.Should().BeOfType<ClientTransportClosedException>();
		manager.IsRunning.Should().BeFalse();
		backend.InitializeCount.Should().Be(1);

		backend.RejectSessionRequests = false;
		await using var retry = CreateManager();
		await retry.InitializeAsync([mcp], timeout.Token);
		retry.IsRunning.Should().BeTrue();
		backend.InitializeCount.Should().Be(2, "a retry must initialize a fresh backend session");
	}

	[Fact]
	public async Task InitializeAsync_StdioRestoreFailure_AbortsAndDisposesEarlierConnections()
	{
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
		using var backend = new FailingStdioBackend();
		await using var healthy = new HttpBackend();
		await healthy.StartAsync(timeout.Token);
		await using var manager = CreateManager();
		var initialize = () => manager.InitializeAsync([
			CreateRemoteMcp("healthy", healthy.Endpoint), backend.Mcp,
		], timeout.Token);

		var failure = (await initialize.Should().ThrowAsync<McpInitializationException>()).Which;
		failure.ServerName.Should().Be("icm-full");
		failure.Message.Should().Contain(FailingStdioBackend.Error);
		backend.StartCount.Should().Be(1);
		manager.IsRunning.Should().BeFalse();
		healthy.InitializeCount.Should().Be(1);
		healthy.DeleteCount.Should().Be(1, "startup failure must dispose the session already opened");
	}

	[Fact]
	public async Task InitializeAsync_DiscoveryFailure_AbortsBeforeConnectingRemainingBackends()
	{
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
		await using var backend = new HttpBackend { RejectDiscovery = true };
		await using var remaining = new HttpBackend();
		await backend.StartAsync(timeout.Token);
		await remaining.StartAsync(timeout.Token);
		await using var manager = CreateManager();
		var initialize = () => manager.InitializeAsync([
			CreateRemoteMcp("calendar", backend.Endpoint),
			CreateRemoteMcp("remaining", remaining.Endpoint),
		], timeout.Token);

		var failure = (await initialize.Should().ThrowAsync<McpInitializationException>()).Which;
		failure.ServerName.Should().Be("calendar");
		failure.Message.Should().Contain("upstream authentication failed");
		backend.InitializeCount.Should().Be(1, "a successful handshake is insufficient for a nested proxy");
		backend.DiscoveryCount.Should().Be(1);
		remaining.RequestCount.Should().Be(0, "startup must stop on the first failed backend");
		manager.IsRunning.Should().BeFalse();
	}

	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public async Task InitializeAsync_UnresponsiveBackend_FailsWithinStartupBudget(bool blockHandshake)
	{
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
		await using var backend = new HttpBackend
		{
			BlockInitialization = blockHandshake,
			BlockDiscovery = !blockHandshake,
		};
		await backend.StartAsync(timeout.Token);
		await using var manager = CreateManager(startupTimeoutSeconds: 1);
		var initialize = () => manager.InitializeAsync([CreateRemoteMcp("slow", backend.Endpoint)], timeout.Token);

		var failure = (await initialize.Should().ThrowAsync<McpInitializationException>()).Which;
		failure.ServerName.Should().Be("slow");
		failure.InnerException.Should().BeOfType<TimeoutException>();
		failure.Message.Should().Contain("after 1s").And.Contain("backendStartupTimeoutSeconds");
		timeout.IsCancellationRequested.Should().BeFalse("the startup budget must expire before caller cancellation");
		manager.IsRunning.Should().BeFalse();
	}

	[Fact]
	public async Task InitializeAsync_ServerWithoutTools_ValidatesItsSession()
	{
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
		await using var backend = new HttpBackend(exposeTools: false);
		await backend.StartAsync(timeout.Token);
		await using var manager = CreateManager();
		await manager.InitializeAsync([CreateRemoteMcp("resources-only", backend.Endpoint)], timeout.Token);

		manager.IsRunning.Should().BeTrue("tools/list is an optional MCP capability");
		backend.InitializeCount.Should().Be(1);
	}

	[Fact]
	public async Task InitializeAsync_CancelledDuringDiscovery_PropagatesAndCleansUp()
	{
		using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
		await using var backend = new HttpBackend { BlockDiscovery = true };
		await backend.StartAsync(cancellation.Token);
		await using var manager = CreateManager();
		var initialization = manager.InitializeAsync([CreateRemoteMcp("calendar", backend.Endpoint)], cancellation.Token);
		await backend.DiscoveryStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
		cancellation.Cancel();

		var initialize = () => initialization;
		await initialize.Should().ThrowAsync<OperationCanceledException>();
		manager.IsRunning.Should().BeFalse();
	}

	[Fact]
	public async Task InitializeAsync_AlreadyCancelled_PropagatesCancellation()
	{
		using var cancellation = new CancellationTokenSource();
		cancellation.Cancel();
		await using var manager = CreateManager();

		var initialize = () => manager.InitializeAsync([], cancellation.Token);
		await initialize.Should().ThrowAsync<OperationCanceledException>();
		manager.IsRunning.Should().BeFalse();
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task InitializeAsync_FailureAfterBinding_ReleasesProxy(bool cancel)
	{
		using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
		await using var backend = new HttpBackend();
		await backend.StartAsync(cancellation.Token);
		await using var manager = new FailingAfterStartMcpManager(cancellation, cancel);
		var mcp = CreateRemoteMcp("calendar", backend.Endpoint);
		var initialize = () => manager.InitializeAsync([mcp], cancellation.Token);

		if (cancel)
			await initialize.Should().ThrowAsync<OperationCanceledException>();
		else
			await initialize.Should().ThrowAsync<InvalidOperationException>().WithMessage("Failure after binding the proxy");

		manager.IsRunning.Should().BeFalse();
		manager.Resolve([mcp]).Should().ContainSingle().Which.Should().BeSameAs(mcp);
		using var socket = new TcpClient();
		var connect = () => socket.ConnectAsync(IPAddress.Loopback, manager.ProxyPort);
		await connect.Should().ThrowAsync<SocketException>("failed startup must release its listener");
	}

	private static McpManager CreateManager(int startupTimeoutSeconds = 10) => new(NullLogger<McpManager>.Instance,
		new McpServerOptions
		{
			BackendStartupTimeoutSeconds = startupTimeoutSeconds,
			ToolDiscoveryProbeTimeoutSeconds = 300,
			DefaultMcpToolCallTimeoutSeconds = 86400,
		});

	private static RemoteMcp CreateRemoteMcp(string name, string endpoint) => new()
	{
		Name = name,
		Type = McpType.Remote,
		Endpoint = endpoint,
		Headers = [],
	};

	private static Task<McpClient> ConnectAsync(McpManager manager, Engine.Mcp mcp, CancellationToken cancellationToken)
	{
		var endpoint = ((RemoteMcp)manager.Resolve([mcp])[0]).Endpoint;
		return McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
		{
			Endpoint = new Uri(endpoint),
			TransportMode = HttpTransportMode.StreamableHttp,
		}), cancellationToken: cancellationToken);
	}

	private sealed class HttpBackend : IAsyncDisposable
	{
		private readonly WebApplication _app;
		private int _requestCount;
		private int _initializeCount;
		private int _discoveryCount;
		private int _deleteCount;
		private volatile bool _rejectSessionRequests;

		public HttpBackend(int port = 0, bool exposeTools = true)
		{
			var builder = WebApplication.CreateSlimBuilder();
			builder.Logging.ClearProviders();
			builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
			var mcp = builder.Services.AddMcpServer().WithHttpTransport();
			if (exposeTools)
			{
				mcp.WithListToolsHandler(async (_, token) =>
				{
					Interlocked.Increment(ref _discoveryCount);
					DiscoveryStarted.TrySetResult();
					if (BlockDiscovery)
						await Task.Delay(Timeout.InfiniteTimeSpan, token);
					if (RejectDiscovery)
						throw new McpProtocolException("upstream authentication failed", McpErrorCode.InternalError);
					return new ListToolsResult
					{
						Tools = [new Tool { Name = "echo", InputSchema = JsonSerializer.SerializeToElement(new { type = "object" }) }],
					};
				}).WithCallToolHandler((_, _) => ValueTask.FromResult(new CallToolResult
				{
					Content = [new TextContentBlock { Text = "ready" }],
				}));
			}
			_app = builder.Build();
			_app.Use(async (context, next) =>
			{
				Interlocked.Increment(ref _requestCount);
				var hasSession = context.Request.Headers.ContainsKey("Mcp-Session-Id");
				if (HttpMethods.IsPost(context.Request.Method) && !hasSession)
				{
					Interlocked.Increment(ref _initializeCount);
					if (BlockInitialization)
						await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted);
				}
				if (HttpMethods.IsDelete(context.Request.Method))
					Interlocked.Increment(ref _deleteCount);
				// Reject the initialized POST itself. An independent GET failure can cancel
				// that POST in the MCP SDK before its 404 response reaches the caller.
				if (_rejectSessionRequests && hasSession && HttpMethods.IsPost(context.Request.Method))
				{
					context.Response.StatusCode = StatusCodes.Status404NotFound;
					return;
				}
				await next(context);
			});
			_app.MapMcp("/mcp");
		}

		public string Endpoint => $"{_app.Urls.Single()}/mcp";
		public int RequestCount => Volatile.Read(ref _requestCount);
		public int InitializeCount => Volatile.Read(ref _initializeCount);
		public int DiscoveryCount => Volatile.Read(ref _discoveryCount);
		public int DeleteCount => Volatile.Read(ref _deleteCount);
		public TaskCompletionSource DiscoveryStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public bool RejectDiscovery { get; init; }
		public bool BlockInitialization { get; init; }
		public bool BlockDiscovery { get; init; }
		public bool RejectSessionRequests { set => _rejectSessionRequests = value; }
		public Task StartAsync(CancellationToken cancellationToken) => _app.StartAsync(cancellationToken);

		public async ValueTask DisposeAsync()
		{
			await _app.StopAsync();
			await _app.DisposeAsync();
		}
	}

	private sealed class FailingStdioBackend : IDisposable
	{
		public const string Error = "The process cannot access the file 'fixture.nupkg' because it is being used by another process.";
		private readonly string _directory = Directory.CreateTempSubdirectory("orchestra-mcp-startup-").FullName;
		private readonly string _marker;

		public FailingStdioBackend()
		{
			_marker = Path.Combine(_directory, "starts.txt");
			var windows = OperatingSystem.IsWindows();
			var script = Path.Combine(_directory, windows ? "backend.cmd" : "backend.sh");
			File.WriteAllText(script, windows
				? $"@echo off\necho started>>\"%ORCHESTRA_TEST_MCP_STARTS%\"\necho {Error} 1>&2\nexit /b 1\n"
				: $"printf 'started\\n' >> \"$ORCHESTRA_TEST_MCP_STARTS\"\nprintf '%s\\n' \"{Error}\" >&2\nexit 1\n");
			Mcp = new LocalMcp
			{
				Name = "icm-full",
				Type = McpType.Local,
				Command = windows ? "cmd.exe" : "/bin/sh",
				Arguments = windows ? ["/d", "/c", script] : [script],
				Environment = new Dictionary<string, string> { ["ORCHESTRA_TEST_MCP_STARTS"] = _marker },
			};
		}

		public LocalMcp Mcp { get; }
		public int StartCount => File.Exists(_marker) ? File.ReadAllLines(_marker).Length : 0;
		public void Dispose() => Directory.Delete(_directory, recursive: true);
	}

	private sealed class FailingAfterStartMcpManager(CancellationTokenSource cancellation, bool cancel)
		: McpManager(NullLogger<McpManager>.Instance)
	{
		public int ProxyPort { get; private set; }

		protected override async Task StartProxyAsync(Engine.Mcp[] globalMcps, CancellationToken cancellationToken)
		{
			await base.StartProxyAsync(globalMcps, cancellationToken);
			IsRunning.Should().BeTrue();
			ProxyPort = new Uri(((RemoteMcp)Resolve(globalMcps)[0]).Endpoint).Port;
			if (cancel)
			{
				cancellation.Cancel();
				cancellationToken.ThrowIfCancellationRequested();
			}
			throw new InvalidOperationException("Failure after binding the proxy");
		}
	}
}
