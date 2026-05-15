using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Orchestra.Engine;
using Orchestra.Host.Mcp;
using Orchestra.Host.McpServer;
using Xunit;

namespace Orchestra.Host.Tests;

/// <summary>
/// Unit tests for <see cref="McpManager"/>.
/// Uses a <see cref="TestableMcpManager"/> subclass that overrides
/// <see cref="McpManager.StartProxyAsync"/> to avoid starting a real proxy.
/// </summary>
public class McpManagerTests : IAsyncLifetime
{
	private TestableMcpManager _manager = null!;

	public Task InitializeAsync()
	{
		_manager = new TestableMcpManager();
		return Task.CompletedTask;
	}

	public async Task DisposeAsync()
	{
		await _manager.DisposeAsync();
	}

	#region InitializeAsync

	[Fact]
	public async Task InitializeAsync_WithEmptyArray_DoesNotStartProxy()
	{
		// Act
		await _manager.InitializeAsync([]);

		// Assert
		_manager.GlobalMcps.Should().BeEmpty();
		_manager.IsRunning.Should().BeFalse();
		_manager.StartProxyCalled.Should().BeFalse();
	}

	[Fact]
	public async Task InitializeAsync_WithMcps_TracksGlobalInstances()
	{
		// Arrange
		var mcp1 = CreateLocalMcp("server1");
		var mcp2 = CreateLocalMcp("server2");

		// Act
		await _manager.InitializeAsync([mcp1, mcp2]);

		// Assert
		_manager.GlobalMcps.Should().HaveCount(2);
		_manager.GlobalMcps.Should().Contain(mcp1);
		_manager.GlobalMcps.Should().Contain(mcp2);
		_manager.StartProxyCalled.Should().BeTrue();
	}

	[Fact]
	public async Task InitializeAsync_CalledTwice_ThrowsInvalidOperationException()
	{
		// Arrange
		await _manager.InitializeAsync([]);

		// Act
		var act = () => _manager.InitializeAsync([]);

		// Assert
		await act.Should().ThrowAsync<InvalidOperationException>()
			.WithMessage("*already been initialized*");
	}

	[Fact]
	public async Task InitializeAsync_CalledTwice_WithMcps_ThrowsInvalidOperationException()
	{
		// Arrange
		var mcp = CreateLocalMcp("server1");
		await _manager.InitializeAsync([mcp]);

		// Act
		var act = () => _manager.InitializeAsync([CreateLocalMcp("server2")]);

		// Assert
		await act.Should().ThrowAsync<InvalidOperationException>()
			.WithMessage("*already been initialized*");
	}

	[Fact]
	public async Task InitializeAsync_WithRemoteMcp_TracksGlobalInstance()
	{
		// Arrange
		var mcp = CreateRemoteMcp("remote-server", "http://example.com/sse");

		// Act
		await _manager.InitializeAsync([mcp]);

		// Assert
		_manager.GlobalMcps.Should().HaveCount(1);
		_manager.GlobalMcps.Should().Contain(mcp);
	}

	#endregion

	#region Resolve — Empty / No-op Cases

	[Fact]
	public void Resolve_BeforeInitialization_ReturnsInputUnchanged()
	{
		// No InitializeAsync called, _globalMcpInstances is empty
		var mcp = CreateLocalMcp("server1");
		var input = new Engine.Mcp[] { mcp };

		// Act
		var result = _manager.Resolve(input);

		// Assert
		result.Should().BeSameAs(input);
	}

	[Fact]
	public async Task Resolve_WithEmptyInput_ReturnsEmptyArray()
	{
		// Arrange
		var globalMcp = CreateLocalMcp("server1");
		await _manager.InitializeAsync([globalMcp]);

		var input = Array.Empty<Engine.Mcp>();

		// Act
		var result = _manager.Resolve(input);

		// Assert
		result.Should().BeSameAs(input);
	}

	[Fact]
	public async Task Resolve_AfterEmptyInitialization_ReturnsInputUnchanged()
	{
		// Arrange — initialized with no global MCPs
		await _manager.InitializeAsync([]);

		var mcp = CreateLocalMcp("server1");
		var input = new Engine.Mcp[] { mcp };

		// Act
		var result = _manager.Resolve(input);

		// Assert
		result.Should().BeSameAs(input);
	}

	#endregion

	#region Resolve — Per-Server Proxy Routes

	[Fact]
	public async Task Resolve_SingleGlobalMcp_ReplacedWithPerServerRoute()
	{
		// Arrange
		var globalMcp = CreateLocalMcp("server1");
		await _manager.InitializeAsync([globalMcp]);

		// Act — pass the same object reference
		var result = _manager.Resolve([globalMcp]);

		// Assert — single global replaced with its per-server proxy route
		result.Should().HaveCount(1);
		var proxy = result[0].Should().BeOfType<RemoteMcp>().Subject;
		proxy.Name.Should().Be("server1");
		proxy.Type.Should().Be(McpType.Remote);
		proxy.Endpoint.Should().MatchRegex(@"^http://localhost:\d+/mcp/server1$");
		proxy.Headers.Should().BeEmpty();
	}

	[Fact]
	public async Task Resolve_MultipleGlobalMcps_EachGetsOwnRoute()
	{
		// Arrange
		var mcp1 = CreateLocalMcp("server1");
		var mcp2 = CreateLocalMcp("server2");
		await _manager.InitializeAsync([mcp1, mcp2]);

		// Act
		var result = _manager.Resolve([mcp1, mcp2]);

		// Assert — each global gets its own per-server route (no merging)
		result.Should().HaveCount(2);
		result[0].Should().BeOfType<RemoteMcp>()
			.Which.Endpoint.Should().MatchRegex(@"/mcp/server1$");
		result[1].Should().BeOfType<RemoteMcp>()
			.Which.Endpoint.Should().MatchRegex(@"/mcp/server2$");
	}

	[Fact]
	public async Task Resolve_MixOfGlobalAndInline_InlinePreservedGlobalsReplaced()
	{
		// Arrange
		var globalMcp = CreateLocalMcp("global-server");
		await _manager.InitializeAsync([globalMcp]);

		var inlineMcp = CreateLocalMcp("inline-server");

		// Act
		var result = _manager.Resolve([globalMcp, inlineMcp]);

		// Assert — global replaced with per-server route, inline preserved
		result.Should().HaveCount(2);
		result[0].Should().BeOfType<RemoteMcp>()
			.Which.Endpoint.Should().Contain("/mcp/global-server");
		result[1].Should().BeSameAs(inlineMcp, "inline MCPs should pass through unchanged");
	}

	/// <summary>
	/// Regression test: TemplateResolver.ResolveStaticMcp creates new MCP object instances
	/// even when no templates are present. The cloned objects must still be recognized as
	/// global MCPs and routed through the proxy. This was the root cause of global MCPs
	/// being spawned as separate processes per orchestration run instead of being shared.
	/// </summary>
	[Fact]
	public async Task Resolve_ClonedGlobalMcp_StillRecognizedAsGlobal()
	{
		// Arrange — initialize with the original global MCP
		var globalMcp = CreateLocalMcp("shared-server");
		await _manager.InitializeAsync([globalMcp]);

		// Simulate what TemplateResolver.ResolveStaticMcp does:
		// creates a new LocalMcp object with the same name but different reference
		var clonedMcp = CreateLocalMcp("shared-server");

		// Act
		var result = _manager.Resolve([clonedMcp]);

		// Assert — the cloned object should be recognized as global by name and replaced
		result.Should().HaveCount(1);
		result[0].Should().BeOfType<RemoteMcp>()
			.Which.Name.Should().Be("shared-server");
	}

	/// <summary>
	/// Verifies that name matching is case-insensitive, consistent with how
	/// MCP names are resolved elsewhere in the system.
	/// </summary>
	[Fact]
	public async Task Resolve_GlobalMcpName_IsCaseInsensitive()
	{
		// Arrange
		var globalMcp = CreateLocalMcp("My-Server");
		await _manager.InitializeAsync([globalMcp]);

		var differentCase = CreateLocalMcp("my-server");

		// Act
		var result = _manager.Resolve([differentCase]);

		// Assert — should match regardless of case
		result.Should().HaveCount(1);
		result[0].Should().BeOfType<RemoteMcp>();
	}

	/// <summary>
	/// Simulates the full PromptExecutor pipeline: global MCPs are cloned by
	/// TemplateResolver, then mixed with inline MCPs, then resolved. Each global
	/// gets its own per-server proxy route.
	/// </summary>
	[Fact]
	public async Task Resolve_ClonedGlobalsWithInlineMcps_OnlyGlobalsReplaced()
	{
		// Arrange
		var global1 = CreateLocalMcp("global-a");
		var global2 = CreateLocalMcp("global-b");
		await _manager.InitializeAsync([global1, global2]);

		// Simulate TemplateResolver cloning + an unrelated inline MCP
		var clonedGlobal1 = CreateLocalMcp("global-a");
		var clonedGlobal2 = CreateLocalMcp("global-b");
		var inlineMcp = CreateLocalMcp("inline-only");

		// Act
		var result = _manager.Resolve([clonedGlobal1, inlineMcp, clonedGlobal2]);

		// Assert — two globals replaced with their own routes, inline preserved
		result.Should().HaveCount(3);
		result[0].Should().BeOfType<RemoteMcp>()
			.Which.Endpoint.Should().Contain("/mcp/global-a");
		result[1].Should().BeSameAs(inlineMcp);
		result[2].Should().BeOfType<RemoteMcp>()
			.Which.Endpoint.Should().Contain("/mcp/global-b");
	}

	#endregion

	#region Resolve — Name-Based Matching (Inline Override Handled at Parse Layer)

	[Fact]
	public async Task Resolve_McpWithSameNameAsGlobal_IsReplacedByProxy()
	{
		// Arrange
		var globalMcp = CreateLocalMcp("shared-server");
		await _manager.InitializeAsync([globalMcp]);

		// A different object with the same name — Resolve matches by name,
		// so this IS treated as a global MCP. Inline overrides (where a step
		// wants a different config for the same name) are handled upstream
		// by OrchestrationParser.ResolveStepMcps, which removes the global
		// MCP from the step's list before Resolve is ever called.
		var sameName = CreateLocalMcp("shared-server");

		// Act
		var result = _manager.Resolve([sameName]);

		// Assert — matched by name, replaced with per-server route
		result.Should().HaveCount(1);
		result[0].Should().BeOfType<RemoteMcp>()
			.Which.Name.Should().Be("shared-server");
	}

	[Fact]
	public async Task Resolve_NoGlobalRefsInInput_ReturnsSameArray()
	{
		// Arrange
		var globalMcp = CreateLocalMcp("global-server");
		await _manager.InitializeAsync([globalMcp]);

		var inline1 = CreateLocalMcp("other1");
		var inline2 = CreateLocalMcp("other2");
		var input = new Engine.Mcp[] { inline1, inline2 };

		// Act
		var result = _manager.Resolve(input);

		// Assert — no global names found, same array reference returned
		result.Should().BeSameAs(input);
	}

	#endregion

	#region Resolve — Per-Server Endpoint Format

	[Fact]
	public async Task Resolve_ProxyEndpointUsesPerServerFormat()
	{
		// Arrange
		var globalMcp = CreateLocalMcp("my-tool");
		await _manager.InitializeAsync([globalMcp]);

		// Act
		var result = _manager.Resolve([globalMcp]);

		// Assert — endpoint includes server name as sub-route
		var proxy = result[0].Should().BeOfType<RemoteMcp>().Subject;
		proxy.Endpoint.Should().MatchRegex(@"^http://localhost:\d+/mcp/my-tool$");
		proxy.Endpoint.Should().NotContain("/sse");
	}

	[Fact]
	public async Task Resolve_EachGlobalGetsDistinctEndpoint()
	{
		// Arrange
		var mcp1 = CreateLocalMcp("server1");
		var mcp2 = CreateLocalMcp("server2");
		await _manager.InitializeAsync([mcp1, mcp2]);

		// Act — resolve each separately
		var result1 = _manager.Resolve([mcp1]);
		var result2 = _manager.Resolve([mcp2]);

		// Assert — each resolves to its own distinct endpoint
		var proxy1 = result1[0].Should().BeOfType<RemoteMcp>().Subject;
		var proxy2 = result2[0].Should().BeOfType<RemoteMcp>().Subject;
		proxy1.Endpoint.Should().EndWith("/mcp/server1");
		proxy2.Endpoint.Should().EndWith("/mcp/server2");
		proxy1.Endpoint.Should().NotBe(proxy2.Endpoint);
	}

	[Fact]
	public async Task Resolve_AllGlobalsShareSameBaseUrl()
	{
		// Arrange
		var mcp1 = CreateLocalMcp("server1");
		var mcp2 = CreateLocalMcp("server2");
		await _manager.InitializeAsync([mcp1, mcp2]);

		// Act — resolve each separately
		var result1 = _manager.Resolve([mcp1]);
		var result2 = _manager.Resolve([mcp2]);

		// Assert — same base URL, different routes
		var proxy1 = result1[0].Should().BeOfType<RemoteMcp>().Subject;
		var proxy2 = result2[0].Should().BeOfType<RemoteMcp>().Subject;
		var base1 = proxy1.Endpoint.Replace("/server1", "");
		var base2 = proxy2.Endpoint.Replace("/server2", "");
		base1.Should().Be(base2, "all global MCPs should share the same proxy base URL");
	}

	#endregion

	#region Resolve — Integration with TemplateResolver

	/// <summary>
	/// Integration test that reproduces the exact bug scenario: TemplateResolver.ResolveStaticMcp
	/// creates new MCP objects (breaking reference equality), then McpManager.Resolve must still
	/// recognize them as global MCPs by name and route them through the proxy.
	/// </summary>
	[Fact]
	public async Task Resolve_AfterTemplateResolverClone_GlobalMcpsStillRoutedThroughProxy()
	{
		// Arrange — register a global MCP
		var globalMcp = new LocalMcp
		{
			Name = "debug-mcp",
			Type = McpType.Local,
			Command = "dotnet",
			Arguments = ["run", "--file", "McpDebug.cs"],
		};
		await _manager.InitializeAsync([globalMcp]);

		// Simulate what PromptExecutor does: TemplateResolver.ResolveStaticMcp clones the MCP
		var context = new OrchestrationExecutionContext
		{
			OrchestrationInfo = new OrchestrationInfo("test", "1.0", "run-1", DateTimeOffset.UtcNow),
		};
		var cloned = TemplateResolver.ResolveStaticMcp(globalMcp, [], context);

		// Verify that TemplateResolver actually produced a different object
		cloned.Should().NotBeSameAs(globalMcp, "TemplateResolver must clone — if this fails, the resolver changed behavior");
		cloned.Name.Should().Be(globalMcp.Name);

		// Act — pass the cloned object to Resolve (exactly what PromptExecutor does)
		var result = _manager.Resolve([cloned]);

		// Assert — must be recognized as global and replaced with per-server route
		result.Should().HaveCount(1);
		result[0].Should().BeOfType<RemoteMcp>()
			.Which.Name.Should().Be("debug-mcp");
		result[0].Should().BeOfType<RemoteMcp>()
			.Which.Endpoint.Should().Contain("/mcp/debug-mcp");
	}

	/// <summary>
	/// Same as above but with multiple global MCPs and an inline MCP mixed in.
	/// </summary>
	[Fact]
	public async Task Resolve_AfterTemplateResolverClone_MixedGlobalsAndInlines()
	{
		// Arrange
		var global1 = CreateLocalMcp("server-a");
		var global2 = CreateRemoteMcp("server-b", "http://example.com/mcp");
		await _manager.InitializeAsync([global1, global2]);

		var context = new OrchestrationExecutionContext
		{
			OrchestrationInfo = new OrchestrationInfo("test", "1.0", "run-1", DateTimeOffset.UtcNow),
		};

		// Clone globals via TemplateResolver
		var clonedGlobal1 = TemplateResolver.ResolveStaticMcp(global1, [], context);
		var clonedGlobal2 = TemplateResolver.ResolveStaticMcp(global2, [], context);
		var inlineMcp = CreateLocalMcp("inline-tool");

		// Act
		var result = _manager.Resolve([clonedGlobal1, inlineMcp, clonedGlobal2]);

		// Assert — inline preserved, each global replaced with its own per-server route
		result.Should().HaveCount(3);
		result[0].Should().BeOfType<RemoteMcp>()
			.Which.Endpoint.Should().Contain("/mcp/server-a");
		result[1].Should().BeSameAs(inlineMcp);
		result[2].Should().BeOfType<RemoteMcp>()
			.Which.Endpoint.Should().Contain("/mcp/server-b");
	}

	#endregion

	#region GlobalMcps Property

	[Fact]
	public async Task GlobalMcps_ReturnsTrackedInstances()
	{
		// Arrange
		var mcp1 = CreateLocalMcp("a");
		var mcp2 = CreateRemoteMcp("b", "http://example.com/sse");
		await _manager.InitializeAsync([mcp1, mcp2]);

		// Act
		var globals = _manager.GlobalMcps;

		// Assert
		globals.Should().HaveCount(2);
		globals.Should().Contain(mcp1);
		globals.Should().Contain(mcp2);
	}

	[Fact]
	public void GlobalMcps_BeforeInitialization_IsEmpty()
	{
		_manager.GlobalMcps.Should().BeEmpty();
	}

	#endregion

	#region Resolve — Proxy Failure Fallback

	[Fact]
	public async Task Resolve_WhenProxyFailedToStart_ReturnsInputUnchanged()
	{
		// Arrange — use a manager whose proxy fails to start
		await using var failingManager = new FailingProxyMcpManager();
		var globalMcp = CreateLocalMcp("server1");
		await failingManager.InitializeAsync([globalMcp]);

		// Act — the proxy failed, so Resolve should return original MCPs
		var input = new Engine.Mcp[] { globalMcp };
		var result = failingManager.Resolve(input);

		// Assert — original MCPs returned unchanged (no dead proxy URLs)
		result.Should().BeSameAs(input);
	}

	[Fact]
	public async Task Resolve_WhenProxyFailedToStart_IsRunningIsFalse()
	{
		// Arrange
		await using var failingManager = new FailingProxyMcpManager();
		var globalMcp = CreateLocalMcp("server1");
		await failingManager.InitializeAsync([globalMcp]);

		// Assert
		failingManager.IsRunning.Should().BeFalse();
	}

	#endregion

	#region Resolve — Data-Plane Default Timeout

	/// <summary>
	/// When an inline RemoteMcp targets the configured data-plane route and the
	/// orchestration didn't set timeoutSeconds, the configured default is applied
	/// so long-running invoke_orchestration calls in sync mode aren't capped at
	/// the Copilot SDK's ~3-minute default.
	/// </summary>
	[Fact]
	public async Task Resolve_DataPlaneRemoteMcp_NoYamlTimeout_AppliesDefault()
	{
		// Arrange
		var options = new McpServerOptions
		{
			DataPlaneRoute = "/mcp/data",
			DefaultOrchestraInvokeTimeoutSeconds = 1800,
		};
		await using var manager = new TestableMcpManager(options);
		await manager.InitializeAsync([]);

		var inline = new RemoteMcp
		{
			Name = "orchestra",
			Type = McpType.Remote,
			Endpoint = "http://localhost:5001/mcp/data",
			Headers = [],
			// Timeout deliberately null
		};

		// Act
		var result = manager.Resolve([inline]);

		// Assert — entry is replaced with one carrying the default timeout
		result.Should().HaveCount(1);
		var resolved = result[0].Should().BeOfType<RemoteMcp>().Subject;
		resolved.Name.Should().Be("orchestra");
		resolved.Endpoint.Should().Be("http://localhost:5001/mcp/data");
		resolved.Timeout.Should().Be(TimeSpan.FromSeconds(1800));
	}

	/// <summary>
	/// When the orchestration explicitly sets <c>timeoutSeconds</c> on the
	/// data-plane MCP entry, the YAML override wins over the host default.
	/// </summary>
	[Fact]
	public async Task Resolve_DataPlaneRemoteMcp_WithYamlTimeout_OverrideWins()
	{
		// Arrange
		var options = new McpServerOptions
		{
			DataPlaneRoute = "/mcp/data",
			DefaultOrchestraInvokeTimeoutSeconds = 1800,
		};
		await using var manager = new TestableMcpManager(options);
		await manager.InitializeAsync([]);

		var inline = new RemoteMcp
		{
			Name = "orchestra",
			Type = McpType.Remote,
			Endpoint = "http://localhost:5001/mcp/data",
			Headers = [],
			Timeout = TimeSpan.FromSeconds(60),
		};

		// Act
		var result = manager.Resolve([inline]);

		// Assert — original entry passed through (no override) with caller-supplied 60s
		result.Should().HaveCount(1);
		result[0].Should().BeSameAs(inline);
		result[0].Timeout.Should().Be(TimeSpan.FromSeconds(60));
	}

	/// <summary>
	/// Non-data-plane MCPs (other Remote MCPs and Local MCPs) MUST NOT receive the
	/// data-plane default; they retain whatever the YAML specified (including null).
	/// </summary>
	[Fact]
	public async Task Resolve_NonDataPlaneMcp_NoDefaultApplied()
	{
		// Arrange
		var options = new McpServerOptions
		{
			DataPlaneRoute = "/mcp/data",
			DefaultOrchestraInvokeTimeoutSeconds = 1800,
		};
		await using var manager = new TestableMcpManager(options);
		await manager.InitializeAsync([]);

		var localTool = CreateLocalMcp("filesystem");
		var unrelatedRemote = new RemoteMcp
		{
			Name = "github",
			Type = McpType.Remote,
			Endpoint = "https://api.example.com/mcp",
			Headers = [],
		};
		var input = new Engine.Mcp[] { localTool, unrelatedRemote };

		// Act
		var result = manager.Resolve(input);

		// Assert — no transformation; same reference returned and timeouts unchanged
		result.Should().BeSameAs(input);
		result[0].Timeout.Should().BeNull();
		result[1].Timeout.Should().BeNull();
	}

	/// <summary>
	/// Setting <c>DefaultOrchestraInvokeTimeoutSeconds = 0</c> means "no host-side
	/// transport timeout" — Orchestra must NOT let the Copilot SDK's built-in ~3-minute
	/// default kick in (the well-known "180-second cliff"). To honor the contract
	/// documented on <see cref="McpServerOptions.DefaultOrchestraInvokeTimeoutSeconds"/>,
	/// the resolver stamps an effectively-infinite transport timeout
	/// (<see cref="McpManager.EffectivelyInfiniteTransportTimeout"/>) onto the
	/// data-plane MCP entry. Server-side engine deadlines remain authoritative.
	/// </summary>
	[Fact]
	public async Task Resolve_DataPlaneRemoteMcp_DefaultDisabled_AppliesInfiniteTimeout()
	{
		// Arrange
		var options = new McpServerOptions
		{
			DataPlaneRoute = "/mcp/data",
			DefaultOrchestraInvokeTimeoutSeconds = 0,
		};
		await using var manager = new TestableMcpManager(options);
		await manager.InitializeAsync([]);

		var inline = new RemoteMcp
		{
			Name = "orchestra",
			Type = McpType.Remote,
			Endpoint = "http://localhost:5001/mcp/data",
			Headers = [],
			// Timeout deliberately null — caller didn't override.
		};

		// Act
		var result = manager.Resolve([inline]);

		// Assert — entry is replaced with one carrying the effectively-infinite timeout.
		result.Should().HaveCount(1);
		var resolved = result[0].Should().BeOfType<RemoteMcp>().Subject;
		resolved.Name.Should().Be("orchestra");
		resolved.Endpoint.Should().Be("http://localhost:5001/mcp/data");
		resolved.Timeout.Should().Be(McpManager.EffectivelyInfiniteTransportTimeout,
			"DefaultOrchestraInvokeTimeoutSeconds == 0 must not silently fall back to the " +
			"Copilot SDK's ~3-minute default; the resolver stamps a sentinel value so the " +
			"transport layer has no opinion and server-side deadlines remain authoritative.");
	}

	/// <summary>
	/// When the host default is disabled (<c>0</c>) but the caller's <c>mcps[]</c> entry
	/// supplies an explicit per-server <c>timeoutSeconds</c>, the per-entry override wins
	/// over the "no transport timeout" sentinel. This preserves the per-orchestration
	/// belt-and-suspenders contract described on
	/// <see cref="McpServerOptions.DefaultOrchestraInvokeTimeoutSeconds"/>.
	/// </summary>
	[Fact]
	public async Task Resolve_DataPlaneRemoteMcp_DefaultDisabled_YamlOverrideWins()
	{
		// Arrange
		var options = new McpServerOptions
		{
			DataPlaneRoute = "/mcp/data",
			DefaultOrchestraInvokeTimeoutSeconds = 0,
		};
		await using var manager = new TestableMcpManager(options);
		await manager.InitializeAsync([]);

		var inline = new RemoteMcp
		{
			Name = "orchestra",
			Type = McpType.Remote,
			Endpoint = "http://localhost:5001/mcp/data",
			Headers = [],
			Timeout = TimeSpan.FromSeconds(900),
		};

		// Act
		var result = manager.Resolve([inline]);

		// Assert — caller's 900s wins; resolver does not overwrite it with the sentinel.
		result.Should().HaveCount(1);
		result[0].Should().BeSameAs(inline);
		result[0].Timeout.Should().Be(TimeSpan.FromSeconds(900));
	}

	/// <summary>
	/// When the host default is disabled (<c>0</c>), non-data-plane MCPs MUST NOT receive
	/// the effectively-infinite sentinel — the sentinel is reserved for Orchestra's own
	/// data plane where the engine guarantees server-side deadlines. External MCPs are
	/// left alone and continue to use whatever timeout the YAML supplied (including null,
	/// which falls back to the SDK default — the appropriate behavior for foreign tools).
	/// </summary>
	[Fact]
	public async Task Resolve_DataPlaneRemoteMcp_DefaultDisabled_NonDataPlaneUnaffected()
	{
		// Arrange
		var options = new McpServerOptions
		{
			DataPlaneRoute = "/mcp/data",
			DefaultOrchestraInvokeTimeoutSeconds = 0,
		};
		await using var manager = new TestableMcpManager(options);
		await manager.InitializeAsync([]);

		var localTool = CreateLocalMcp("filesystem");
		var unrelatedRemote = new RemoteMcp
		{
			Name = "github",
			Type = McpType.Remote,
			Endpoint = "https://api.example.com/mcp",
			Headers = [],
		};
		var input = new Engine.Mcp[] { localTool, unrelatedRemote };

		// Act
		var result = manager.Resolve(input);

		// Assert — no transformation; same reference returned and timeouts unchanged.
		result.Should().BeSameAs(input);
		result[0].Timeout.Should().BeNull();
		result[1].Timeout.Should().BeNull();
	}

	/// <summary>
	/// The default value is configurable: changing it changes the applied timeout.
	/// </summary>
	[Fact]
	public async Task Resolve_DataPlaneRemoteMcp_CustomDefault_Applied()
	{
		// Arrange — operator overrides default to 600s
		var options = new McpServerOptions
		{
			DataPlaneRoute = "/mcp/data",
			DefaultOrchestraInvokeTimeoutSeconds = 600,
		};
		await using var manager = new TestableMcpManager(options);
		await manager.InitializeAsync([]);

		var inline = new RemoteMcp
		{
			Name = "orchestra",
			Type = McpType.Remote,
			Endpoint = "http://localhost:5001/mcp/data",
			Headers = [],
		};

		// Act
		var result = manager.Resolve([inline]);

		// Assert
		result[0].Timeout.Should().Be(TimeSpan.FromSeconds(600));
	}

	/// <summary>
	/// Endpoint detection tolerates trailing slashes, ports, and absolute URIs.
	/// </summary>
	[Theory]
	[InlineData("http://localhost:5001/mcp/data")]
	[InlineData("http://localhost:5001/mcp/data/")]
	[InlineData("https://orchestra.example.com:443/mcp/data")]
	[InlineData("http://127.0.0.1:8080/mcp/data?session=abc")]
	public async Task Resolve_DataPlaneEndpoint_VariousFormats_DetectedAndDefaultApplied(string endpoint)
	{
		// Arrange
		var options = new McpServerOptions
		{
			DataPlaneRoute = "/mcp/data",
			DefaultOrchestraInvokeTimeoutSeconds = 1800,
		};
		await using var manager = new TestableMcpManager(options);
		await manager.InitializeAsync([]);

		var inline = new RemoteMcp
		{
			Name = "orchestra",
			Type = McpType.Remote,
			Endpoint = endpoint,
			Headers = [],
		};

		// Act
		var result = manager.Resolve([inline]);

		// Assert
		result[0].Timeout.Should().Be(TimeSpan.FromSeconds(1800),
			$"endpoint '{endpoint}' should be recognized as the data-plane MCP");
	}

	/// <summary>
	/// A custom DataPlaneRoute is honored: the same endpoint that previously matched
	/// no longer triggers the default once the route changes.
	/// </summary>
	[Fact]
	public async Task Resolve_DataPlaneRoute_Customized_DetectionFollowsConfig()
	{
		// Arrange — operator changed the route from /mcp/data to /api/mcp/data
		var options = new McpServerOptions
		{
			DataPlaneRoute = "/api/mcp/data",
			DefaultOrchestraInvokeTimeoutSeconds = 1800,
		};
		await using var manager = new TestableMcpManager(options);
		await manager.InitializeAsync([]);

		var customRouteInline = new RemoteMcp
		{
			Name = "orchestra",
			Type = McpType.Remote,
			Endpoint = "http://localhost:5001/api/mcp/data",
			Headers = [],
		};

		var oldRouteInline = new RemoteMcp
		{
			Name = "orchestra-old",
			Type = McpType.Remote,
			Endpoint = "http://localhost:5001/mcp/data", // no longer the configured route
			Headers = [],
		};

		// Act
		var result = manager.Resolve([customRouteInline, oldRouteInline]);

		// Assert
		result[0].Timeout.Should().Be(TimeSpan.FromSeconds(1800),
			"endpoint matching the custom data-plane route should get the default");
		result[1].Timeout.Should().BeNull(
			"endpoint that does NOT match the configured data-plane route should not get the default");
	}

	/// <summary>
	/// When the orchestration references a global MCP (defined in orchestra.mcp.json)
	/// whose original definition pointed at the data-plane route, the resulting proxy
	/// RemoteMcp inherits the host default timeout — even though the proxy URL itself
	/// no longer contains <c>/mcp/data</c>.
	/// </summary>
	[Fact]
	public async Task Resolve_GlobalMcpPointingAtDataPlane_AppliesDefault()
	{
		// Arrange — register a global MCP whose endpoint is the data plane
		var options = new McpServerOptions
		{
			DataPlaneRoute = "/mcp/data",
			DefaultOrchestraInvokeTimeoutSeconds = 1800,
		};
		await using var manager = new TestableMcpManager(options);

		var globalDataPlane = new RemoteMcp
		{
			Name = "orchestra",
			Type = McpType.Remote,
			Endpoint = "http://localhost:5001/mcp/data",
			Headers = [],
			// no Timeout configured globally either
		};
		await manager.InitializeAsync([globalDataPlane]);

		// The orchestration references the global by name (post-template-resolver clone)
		var clonedReference = new RemoteMcp
		{
			Name = "orchestra",
			Type = McpType.Remote,
			Endpoint = "http://localhost:5001/mcp/data",
			Headers = [],
		};

		// Act
		var result = manager.Resolve([clonedReference]);

		// Assert — replaced with proxy URL but carrying the data-plane default timeout
		result.Should().HaveCount(1);
		var proxy = result[0].Should().BeOfType<RemoteMcp>().Subject;
		proxy.Endpoint.Should().Contain("/mcp/orchestra", "global MCPs are routed via the proxy");
		proxy.Timeout.Should().Be(TimeSpan.FromSeconds(1800));
	}

	/// <summary>
	/// When the global MCP itself sets a timeout, that explicit value is preserved
	/// across proxy replacement and the host default does not override it.
	/// </summary>
	[Fact]
	public async Task Resolve_GlobalMcpWithExplicitTimeout_TakesPrecedenceOverDefault()
	{
		// Arrange
		var options = new McpServerOptions
		{
			DataPlaneRoute = "/mcp/data",
			DefaultOrchestraInvokeTimeoutSeconds = 1800,
		};
		await using var manager = new TestableMcpManager(options);

		var globalDataPlane = new RemoteMcp
		{
			Name = "orchestra",
			Type = McpType.Remote,
			Endpoint = "http://localhost:5001/mcp/data",
			Headers = [],
			Timeout = TimeSpan.FromMinutes(5), // explicit value
		};
		await manager.InitializeAsync([globalDataPlane]);

		var reference = new RemoteMcp
		{
			Name = "orchestra",
			Type = McpType.Remote,
			Endpoint = "http://localhost:5001/mcp/data",
			Headers = [],
		};

		// Act
		var result = manager.Resolve([reference]);

		// Assert — explicit global timeout (5 min) wins over host default (30 min)
		result[0].Timeout.Should().Be(TimeSpan.FromMinutes(5));
	}

	/// <summary>
	/// The orchestration's per-entry <c>timeoutSeconds</c> still wins over both the
	/// global definition's timeout and the host default — orchestration intent
	/// always takes top priority.
	/// </summary>
	[Fact]
	public async Task Resolve_OrchestrationOverride_BeatsGlobalAndHostDefault()
	{
		// Arrange
		var options = new McpServerOptions
		{
			DataPlaneRoute = "/mcp/data",
			DefaultOrchestraInvokeTimeoutSeconds = 1800,
		};
		await using var manager = new TestableMcpManager(options);

		var globalDataPlane = new RemoteMcp
		{
			Name = "orchestra",
			Type = McpType.Remote,
			Endpoint = "http://localhost:5001/mcp/data",
			Headers = [],
			Timeout = TimeSpan.FromMinutes(5), // global default
		};
		await manager.InitializeAsync([globalDataPlane]);

		var orchestrationOverride = new RemoteMcp
		{
			Name = "orchestra",
			Type = McpType.Remote,
			Endpoint = "http://localhost:5001/mcp/data",
			Headers = [],
			Timeout = TimeSpan.FromSeconds(45), // orchestration YAML override
		};

		// Act
		var result = manager.Resolve([orchestrationOverride]);

		// Assert — orchestration override wins
		result[0].Timeout.Should().Be(TimeSpan.FromSeconds(45));
	}

	// ── Catch-all default for non-Orchestra-data-plane MCPs ──

	/// <summary>
	/// When <c>DefaultMcpToolCallTimeoutSeconds</c> is null (the default), non-Orchestra
	/// MCPs that don't carry a per-orchestration <c>timeoutSeconds</c> override must
	/// keep <c>Timeout = null</c>. This preserves backward-compatible behavior: the
	/// Copilot SDK's built-in ~3-minute default takes over for everyone who hasn't opted
	/// in to the catch-all.
	/// </summary>
	[Fact]
	public async Task Resolve_NonDataPlaneMcp_CatchAllNull_LeavesTimeoutNull()
	{
		var options = new McpServerOptions
		{
			DataPlaneRoute = "/mcp/data",
			DefaultMcpToolCallTimeoutSeconds = null,
		};
		await using var manager = new TestableMcpManager(options);
		await manager.InitializeAsync([]);

		var external = new RemoteMcp
		{
			Name = "github",
			Type = McpType.Remote,
			Endpoint = "https://api.example.com/mcp",
			Headers = [],
		};
		var localTool = CreateLocalMcp("filesystem");
		var input = new Engine.Mcp[] { external, localTool };

		var result = manager.Resolve(input);

		result.Should().BeSameAs(input);
		result[0].Timeout.Should().BeNull();
		result[1].Timeout.Should().BeNull();
	}

	/// <summary>
	/// When <c>DefaultMcpToolCallTimeoutSeconds</c> is a positive number, non-Orchestra
	/// MCPs without a per-orchestration override receive that many seconds as their
	/// transport timeout. This is the primary path by which an operator removes the
	/// Copilot SDK's 180-second cliff for ALL their MCPs at once.
	/// </summary>
	[Fact]
	public async Task Resolve_NonDataPlaneMcp_CatchAllPositive_AppliesValue()
	{
		var options = new McpServerOptions
		{
			DataPlaneRoute = "/mcp/data",
			DefaultMcpToolCallTimeoutSeconds = 1800,
		};
		await using var manager = new TestableMcpManager(options);
		await manager.InitializeAsync([]);

		var external = new RemoteMcp
		{
			Name = "github",
			Type = McpType.Remote,
			Endpoint = "https://api.example.com/mcp",
			Headers = [],
		};
		var localTool = CreateLocalMcp("filesystem");

		var result = manager.Resolve([external, localTool]);

		result.Should().HaveCount(2);
		var resolvedRemote = result[0].Should().BeOfType<RemoteMcp>().Subject;
		resolvedRemote.Timeout.Should().Be(TimeSpan.FromSeconds(1800));
		resolvedRemote.Endpoint.Should().Be("https://api.example.com/mcp", "endpoint must be unchanged");

		var resolvedLocal = result[1].Should().BeOfType<LocalMcp>().Subject;
		resolvedLocal.Timeout.Should().Be(TimeSpan.FromSeconds(1800));
		resolvedLocal.Command.Should().Be("test-command", "command must be unchanged");
	}

	/// <summary>
	/// Setting <c>DefaultMcpToolCallTimeoutSeconds = 0</c> means "no client-side
	/// transport timeout for any MCP" — mirror of the data-plane knob's <c>0</c> semantics.
	/// The resolver must stamp <see cref="McpManager.EffectivelyInfiniteTransportTimeout"/>
	/// so the Copilot SDK's ~3-minute default does NOT silently kick in.
	/// </summary>
	[Fact]
	public async Task Resolve_NonDataPlaneMcp_CatchAllZero_AppliesInfiniteTimeout()
	{
		var options = new McpServerOptions
		{
			DataPlaneRoute = "/mcp/data",
			DefaultMcpToolCallTimeoutSeconds = 0,
		};
		await using var manager = new TestableMcpManager(options);
		await manager.InitializeAsync([]);

		var external = new RemoteMcp
		{
			Name = "github",
			Type = McpType.Remote,
			Endpoint = "https://api.example.com/mcp",
			Headers = [],
		};

		var result = manager.Resolve([external]);

		result[0].Timeout.Should().Be(McpManager.EffectivelyInfiniteTransportTimeout);
	}

	/// <summary>
	/// A per-orchestration <c>mcps[].timeoutSeconds</c> (already populated on the
	/// inbound entry) must take precedence over the catch-all host default.
	/// </summary>
	[Fact]
	public async Task Resolve_NonDataPlaneMcp_PerMcpsTimeoutWinsOverCatchAll()
	{
		var options = new McpServerOptions
		{
			DataPlaneRoute = "/mcp/data",
			DefaultMcpToolCallTimeoutSeconds = 1800,
		};
		await using var manager = new TestableMcpManager(options);
		await manager.InitializeAsync([]);

		var external = new RemoteMcp
		{
			Name = "github",
			Type = McpType.Remote,
			Endpoint = "https://api.example.com/mcp",
			Headers = [],
			Timeout = TimeSpan.FromSeconds(60), // YAML override
		};

		var result = manager.Resolve([external]);

		result[0].Timeout.Should().Be(TimeSpan.FromSeconds(60),
			"per-orchestration mcps[].timeoutSeconds must always win over the host catch-all default");
	}

	/// <summary>
	/// The two knobs are independent: the catch-all must not leak into Orchestra
	/// data-plane handling and vice versa. When BOTH are set, the data-plane MCP gets
	/// the data-plane knob, and non-data-plane MCPs get the catch-all knob.
	/// </summary>
	[Fact]
	public async Task Resolve_BothKnobsSet_EachAppliesToItsOwnEndpoint()
	{
		var options = new McpServerOptions
		{
			DataPlaneRoute = "/mcp/data",
			DefaultOrchestraInvokeTimeoutSeconds = 7200,  // data-plane: 2h
			DefaultMcpToolCallTimeoutSeconds = 600,       // catch-all: 10min
		};
		await using var manager = new TestableMcpManager(options);
		await manager.InitializeAsync([]);

		var dataPlane = new RemoteMcp
		{
			Name = "orchestra",
			Type = McpType.Remote,
			Endpoint = "http://localhost:5001/mcp/data",
			Headers = [],
		};
		var external = new RemoteMcp
		{
			Name = "github",
			Type = McpType.Remote,
			Endpoint = "https://api.example.com/mcp",
			Headers = [],
		};

		var result = manager.Resolve([dataPlane, external]);

		result[0].Timeout.Should().Be(TimeSpan.FromSeconds(7200),
			"Orchestra data-plane uses defaultOrchestraInvokeTimeoutSeconds, not the catch-all");
		result[1].Timeout.Should().Be(TimeSpan.FromSeconds(600),
			"Non-data-plane MCPs use defaultMcpToolCallTimeoutSeconds");
	}

	/// <summary>
	/// The data-plane knob being set to 0 (effectively infinite) MUST NOT be overridden
	/// by the catch-all default for data-plane MCPs — the data-plane endpoint always
	/// takes the data-plane knob.
	/// </summary>
	[Fact]
	public async Task Resolve_DataPlaneZero_CatchAllPositive_DataPlaneStaysInfinite()
	{
		var options = new McpServerOptions
		{
			DataPlaneRoute = "/mcp/data",
			DefaultOrchestraInvokeTimeoutSeconds = 0,     // data-plane: infinite
			DefaultMcpToolCallTimeoutSeconds = 600,       // catch-all: 10min
		};
		await using var manager = new TestableMcpManager(options);
		await manager.InitializeAsync([]);

		var dataPlane = new RemoteMcp
		{
			Name = "orchestra",
			Type = McpType.Remote,
			Endpoint = "http://localhost:5001/mcp/data",
			Headers = [],
		};

		var result = manager.Resolve([dataPlane]);

		result[0].Timeout.Should().Be(McpManager.EffectivelyInfiniteTransportTimeout,
			"data-plane stays governed by its own knob even when the catch-all is set");
	}

	#endregion

	#region Resolve(Mcps, ParentExecutionAnnotation) — parent-execution header injection

	[Fact]
	public async Task ResolveWithParent_OnInlineDataPlaneMcp_StampsParentExecutionHeaders()
	{
		// Arrange — orchestration declares an inline RemoteMcp pointing at /mcp/data,
		// matching the misconfiguration that caused the recursive-launch bug.
		var options = new McpServerOptions { DataPlaneRoute = "/mcp/data" };
		await using var manager = new TestableMcpManager(options);
		await manager.InitializeAsync([]);

		var inlineDataPlane = new RemoteMcp
		{
			Name = "orchestra",
			Type = McpType.Remote,
			Endpoint = "http://localhost:5001/mcp/data",
			Headers = new Dictionary<string, string> { ["X-Existing"] = "preserve-me" },
		};

		var parent = new ParentExecutionAnnotation
		{
			ExecutionId = "abc123",
			OrchestrationName = "find-meeting",
			StepName = "search",
		};

		// Act
		var result = manager.Resolve([inlineDataPlane], parent);

		// Assert
		result.Should().HaveCount(1);
		var remote = result[0].Should().BeOfType<RemoteMcp>().Subject;
		remote.Headers.Should().ContainKey(OrchestraHeaders.ParentExecutionId).WhoseValue.Should().Be("abc123");
		remote.Headers.Should().ContainKey(OrchestraHeaders.ParentOrchestrationName).WhoseValue.Should().Be("find-meeting");
		remote.Headers.Should().ContainKey(OrchestraHeaders.ParentStepName).WhoseValue.Should().Be("search");

		// Existing user-defined headers must be preserved.
		remote.Headers.Should().ContainKey("X-Existing").WhoseValue.Should().Be("preserve-me");
	}

	[Fact]
	public async Task ResolveWithParent_OnNonOrchestraMcp_DoesNotInjectHeaders()
	{
		// Arrange — endpoint points at an external MCP server. Headers must NOT be added,
		// otherwise we'd leak internal execution IDs to third parties.
		var options = new McpServerOptions { DataPlaneRoute = "/mcp/data" };
		await using var manager = new TestableMcpManager(options);
		await manager.InitializeAsync([]);

		var externalMcp = new RemoteMcp
		{
			Name = "external",
			Type = McpType.Remote,
			Endpoint = "https://example.com/some-mcp",
			Headers = [],
		};

		var parent = new ParentExecutionAnnotation
		{
			ExecutionId = "abc123",
			OrchestrationName = "x",
			StepName = "y",
		};

		// Act
		var result = manager.Resolve([externalMcp], parent);

		// Assert — external endpoints are returned unchanged.
		result.Should().HaveCount(1);
		var remote = result[0].Should().BeOfType<RemoteMcp>().Subject;
		remote.Headers.Should().NotContainKey(OrchestraHeaders.ParentExecutionId);
		remote.Headers.Should().NotContainKey(OrchestraHeaders.ParentOrchestrationName);
		remote.Headers.Should().NotContainKey(OrchestraHeaders.ParentStepName);
	}

	[Fact]
	public async Task ResolveWithParent_NullAnnotation_BehavesLikePlainResolve()
	{
		// Arrange
		var options = new McpServerOptions { DataPlaneRoute = "/mcp/data" };
		await using var manager = new TestableMcpManager(options);
		await manager.InitializeAsync([]);

		var inlineDataPlane = new RemoteMcp
		{
			Name = "orchestra",
			Type = McpType.Remote,
			Endpoint = "http://localhost:5001/mcp/data",
			Headers = [],
		};

		// Act
		var result = manager.Resolve([inlineDataPlane], parent: null);

		// Assert — no headers injected when parent is null.
		var remote = result[0].Should().BeOfType<RemoteMcp>().Subject;
		remote.Headers.Should().NotContainKey(OrchestraHeaders.ParentExecutionId);
	}

	[Fact]
	public async Task ResolveWithParent_OverwritesUserSpoofedParentHeaders()
	{
		// Arrange — orchestration YAML contains hand-crafted headers that try to spoof
		// the parent execution ID. The resolver must overwrite them so the LLM cannot
		// forge run lineage by editing the YAML.
		var options = new McpServerOptions { DataPlaneRoute = "/mcp/data" };
		await using var manager = new TestableMcpManager(options);
		await manager.InitializeAsync([]);

		var spoofingMcp = new RemoteMcp
		{
			Name = "orchestra",
			Type = McpType.Remote,
			Endpoint = "http://localhost:5001/mcp/data",
			Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
			{
				[OrchestraHeaders.ParentExecutionId] = "spoofed-id",
			},
		};

		var parent = new ParentExecutionAnnotation
		{
			ExecutionId = "real-id",
			OrchestrationName = "real",
			StepName = "step",
		};

		// Act
		var result = manager.Resolve([spoofingMcp], parent);

		// Assert
		var remote = result[0].Should().BeOfType<RemoteMcp>().Subject;
		remote.Headers[OrchestraHeaders.ParentExecutionId].Should().Be("real-id",
			"the resolver must always win over caller-supplied parent headers");
	}

	#endregion

	#region Helpers

	private static LocalMcp CreateLocalMcp(string name) => new()
	{
		Name = name,
		Type = McpType.Local,
		Command = "test-command",
		Arguments = ["--arg1"],
	};

	private static RemoteMcp CreateRemoteMcp(string name, string endpoint) => new()
	{
		Name = name,
		Type = McpType.Remote,
		Endpoint = endpoint,
		Headers = [],
	};

	#endregion

	/// <summary>
	/// Test subclass that bypasses the real proxy startup.
	/// </summary>
	private sealed class TestableMcpManager : McpManager
	{
		public bool StartProxyCalled { get; private set; }

		public TestableMcpManager(McpServerOptions? options = null)
			: base(NullLogger<McpManager>.Instance, options)
		{
		}

		protected override Task StartProxyAsync(Engine.Mcp[] globalMcps, CancellationToken cancellationToken)
		{
			StartProxyCalled = true;
			return Task.CompletedTask;
		}
	}

	/// <summary>
	/// Test subclass that simulates a proxy startup failure.
	/// </summary>
	private sealed class FailingProxyMcpManager : McpManager
	{
		public FailingProxyMcpManager(McpServerOptions? options = null)
			: base(NullLogger<McpManager>.Instance, options)
		{
		}

		protected override Task StartProxyAsync(Engine.Mcp[] globalMcps, CancellationToken cancellationToken)
		{
			throw new InvalidOperationException("Simulated proxy startup failure");
		}
	}
}
