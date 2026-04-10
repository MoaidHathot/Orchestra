using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Orchestra.Engine;
using Orchestra.Host.Mcp;
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

	#region Resolve — Global MCP Replacement (Unified Proxy)

	[Fact]
	public async Task Resolve_SingleGlobalMcp_ReplacedWithUnifiedProxy()
	{
		// Arrange
		var globalMcp = CreateLocalMcp("server1");
		await _manager.InitializeAsync([globalMcp]);

		// Act — pass the same object reference
		var result = _manager.Resolve([globalMcp]);

		// Assert — single global replaced with unified proxy MCP
		result.Should().HaveCount(1);
		var proxy = result[0].Should().BeOfType<RemoteMcp>().Subject;
		proxy.Name.Should().Be("orchestra-mcp-proxy");
		proxy.Type.Should().Be(McpType.Remote);
		proxy.Endpoint.Should().MatchRegex(@"^http://localhost:\d+/mcp$");
		proxy.Headers.Should().BeEmpty();
	}

	[Fact]
	public async Task Resolve_MultipleGlobalMcps_CollapsedIntoSingleProxy()
	{
		// Arrange
		var mcp1 = CreateLocalMcp("server1");
		var mcp2 = CreateLocalMcp("server2");
		await _manager.InitializeAsync([mcp1, mcp2]);

		// Act
		var result = _manager.Resolve([mcp1, mcp2]);

		// Assert — two globals collapsed into one unified proxy
		result.Should().HaveCount(1);
		var proxy = result[0].Should().BeOfType<RemoteMcp>().Subject;
		proxy.Name.Should().Be("orchestra-mcp-proxy");
	}

	[Fact]
	public async Task Resolve_MixOfGlobalAndInline_InlinePreservedPlusProxy()
	{
		// Arrange
		var globalMcp = CreateLocalMcp("global-server");
		await _manager.InitializeAsync([globalMcp]);

		var inlineMcp = CreateLocalMcp("inline-server");

		// Act
		var result = _manager.Resolve([globalMcp, inlineMcp]);

		// Assert — inline preserved, global replaced with unified proxy appended at end
		result.Should().HaveCount(2);
		result[0].Should().BeSameAs(inlineMcp, "inline MCPs should pass through unchanged");
		result[1].Should().BeOfType<RemoteMcp>().Which.Name.Should().Be("orchestra-mcp-proxy");
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
			.Which.Name.Should().Be("orchestra-mcp-proxy");
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
	/// TemplateResolver, then mixed with inline MCPs, then resolved. The proxy
	/// should replace only the global ones.
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

		// Assert — two globals replaced with one proxy, inline preserved
		result.Should().HaveCount(2);
		result[0].Should().BeSameAs(inlineMcp);
		result[1].Should().BeOfType<RemoteMcp>()
			.Which.Name.Should().Be("orchestra-mcp-proxy");
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

		// Assert — matched by name, replaced with proxy
		result.Should().HaveCount(1);
		result[0].Should().BeOfType<RemoteMcp>()
			.Which.Name.Should().Be("orchestra-mcp-proxy");
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

	#region Resolve — Unified Endpoint

	[Fact]
	public async Task Resolve_ProxyEndpointUsesStreamableHttpFormat()
	{
		// Arrange
		var globalMcp = CreateLocalMcp("my-tool");
		await _manager.InitializeAsync([globalMcp]);

		// Act
		var result = _manager.Resolve([globalMcp]);

		// Assert — endpoint is Streamable HTTP (no /sse suffix)
		var proxy = result[0].Should().BeOfType<RemoteMcp>().Subject;
		proxy.Endpoint.Should().MatchRegex(@"^http://localhost:\d+/mcp$");
		proxy.Endpoint.Should().NotContain("/sse");
	}

	[Fact]
	public async Task Resolve_AllGlobalsShareSameProxyEndpoint()
	{
		// Arrange
		var mcp1 = CreateLocalMcp("server1");
		var mcp2 = CreateLocalMcp("server2");
		await _manager.InitializeAsync([mcp1, mcp2]);

		// Act — resolve each separately
		var result1 = _manager.Resolve([mcp1]);
		var result2 = _manager.Resolve([mcp2]);

		// Assert — both resolve to the same endpoint
		var proxy1 = result1[0].Should().BeOfType<RemoteMcp>().Subject;
		var proxy2 = result2[0].Should().BeOfType<RemoteMcp>().Subject;
		proxy1.Endpoint.Should().Be(proxy2.Endpoint);
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

		// Assert — must be recognized as global and replaced with proxy
		result.Should().HaveCount(1);
		result[0].Should().BeOfType<RemoteMcp>()
			.Which.Name.Should().Be("orchestra-mcp-proxy");
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

		// Assert — inline preserved, both globals replaced with single proxy
		result.Should().HaveCount(2);
		result[0].Should().BeSameAs(inlineMcp);
		result[1].Should().BeOfType<RemoteMcp>()
			.Which.Name.Should().Be("orchestra-mcp-proxy");
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

		public TestableMcpManager()
			: base(NullLogger<McpManager>.Instance)
		{
		}

		protected override Task StartProxyAsync(Engine.Mcp[] globalMcps, CancellationToken cancellationToken)
		{
			StartProxyCalled = true;
			return Task.CompletedTask;
		}
	}
}
