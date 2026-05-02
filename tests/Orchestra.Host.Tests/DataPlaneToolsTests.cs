using FluentAssertions;
using Microsoft.AspNetCore.Http;
using NSubstitute;
using Orchestra.Engine;
using Orchestra.Host.McpServer;
using Xunit;

namespace Orchestra.Host.Tests;

/// <summary>
/// Verifies that <see cref="DataPlaneTools.InvokeOrchestration"/> auto-populates
/// <c>parentExecutionId</c> from the engine-stamped HTTP headers when called from inside
/// an orchestration's prompt step. This is the second half of the fix for the runaway
/// recursive-launch bug — the engine stamps headers via <c>McpManager.Resolve(_, parent)</c>,
/// and <see cref="DataPlaneTools"/> reads them here so the resulting child run carries
/// proper lineage instead of appearing as a top-level "manual" run.
/// </summary>
public class DataPlaneToolsTests
{
	private static IHttpContextAccessor HttpContextWith(params (string Name, string Value)[] headers)
	{
		var ctx = new DefaultHttpContext();
		foreach (var (name, value) in headers)
		{
			ctx.Request.Headers.Append(name, value);
		}
		var accessor = Substitute.For<IHttpContextAccessor>();
		accessor.HttpContext.Returns(ctx);
		return accessor;
	}

	private static (IChildOrchestrationLauncher Launcher, Func<ChildLaunchRequest?> Captured) FakeLauncher()
	{
		ChildLaunchRequest? captured = null;
		var launcher = Substitute.For<IChildOrchestrationLauncher>();
		launcher
			.LaunchAsync(Arg.Do<ChildLaunchRequest>(r => captured = r), Arg.Any<CancellationToken>())
			.Returns(callInfo =>
			{
				var req = callInfo.ArgAt<ChildLaunchRequest>(0);
				return Task.FromResult(new ChildOrchestrationHandle
				{
					ExecutionId = "child-exec-1",
					OrchestrationId = req.OrchestrationId,
					OrchestrationName = req.OrchestrationId,
					Reporter = NullOrchestrationReporter.Instance,
					StartedAt = DateTimeOffset.UtcNow,
					// Async mode in the tests below — the tool returns the dispatch JSON without
					// awaiting Completion, so we just need a never-completing placeholder.
					Completion = new TaskCompletionSource<ChildOrchestrationResult>().Task,
				});
			});
		return (launcher, () => captured);
	}

	[Fact]
	public async Task InvokeOrchestration_WithParentHeaders_PopulatesParentContextOnRequest()
	{
		// Arrange — simulate the engine having stamped parent-execution headers on the
		// outbound /mcp/data connection (which is what McpManager.Resolve(_, parent) does
		// for orchestration prompt steps that target the data plane).
		var accessor = HttpContextWith(
			(OrchestraHeaders.ParentExecutionId, "parent-run-id-xyz"),
			(OrchestraHeaders.ParentOrchestrationName, "find-meeting"),
			(OrchestraHeaders.ParentStepName, "search"));
		var (launcher, captured) = FakeLauncher();

		// Act
		await DataPlaneTools.InvokeOrchestration(launcher, accessor, "child-orchestration");

		// Assert
		var req = captured();
		req.Should().NotBeNull();
		req!.ParentContext.Should().NotBeNull(
			"DataPlaneTools must auto-populate ParentContext from engine-stamped headers when no explicit parentExecutionId is supplied");
		req.ParentContext!.ParentExecutionId.Should().Be("parent-run-id-xyz");
		req.ParentContext.ParentStepName.Should().Be("search");

		// TriggeredBy should encode the parent's orchestration name + run ID for clarity in
		// historical run views — without this, persisted runs lose their lineage and look
		// like top-level "manual" launches even though they were spawned by another orchestration.
		req.TriggeredBy.Should().Contain("parent-run-id-xyz");
		req.TriggeredBy.Should().Contain("find-meeting");
	}

	[Fact]
	public async Task InvokeOrchestration_ExplicitParentExecutionIdArgument_OverridesHeaders()
	{
		// Arrange — explicit caller-supplied parentExecutionId must win over the headers,
		// preserving backward compatibility for callers that already pass it.
		var accessor = HttpContextWith(
			(OrchestraHeaders.ParentExecutionId, "header-parent"),
			(OrchestraHeaders.ParentOrchestrationName, "header-orch"),
			(OrchestraHeaders.ParentStepName, "header-step"));
		var (launcher, captured) = FakeLauncher();

		// Act
		await DataPlaneTools.InvokeOrchestration(
			launcher,
			accessor,
			"child-orchestration",
			parentExecutionId: "explicit-parent");

		// Assert
		var req = captured();
		req!.ParentContext!.ParentExecutionId.Should().Be("explicit-parent",
			"explicit parentExecutionId argument must take priority over headers");
	}

	[Fact]
	public async Task InvokeOrchestration_NoHeadersAndNoExplicitParent_LeavesParentContextNull()
	{
		// Arrange — external MCP client (e.g. Claude Desktop) calling /mcp/data directly.
		// No parent-execution headers, no explicit parentExecutionId. Must remain a top-level
		// invocation with TriggeredBy="mcp" and no parent context.
		var accessor = HttpContextWith();
		var (launcher, captured) = FakeLauncher();

		// Act
		await DataPlaneTools.InvokeOrchestration(launcher, accessor, "child-orchestration");

		// Assert
		var req = captured();
		req!.ParentContext.Should().BeNull();
		req.TriggeredBy.Should().Be("mcp");
	}

	[Fact]
	public async Task InvokeOrchestration_NullHttpContext_DoesNotThrow()
	{
		// Arrange — defensive: even if HttpContextAccessor returns null (e.g. unit test
		// scenarios), the tool must remain functional and treat the call as headerless.
		var accessor = Substitute.For<IHttpContextAccessor>();
		accessor.HttpContext.Returns((HttpContext?)null);
		var (launcher, captured) = FakeLauncher();

		// Act
		await DataPlaneTools.InvokeOrchestration(launcher, accessor, "child-orchestration");

		// Assert
		var req = captured();
		req!.ParentContext.Should().BeNull();
		req.TriggeredBy.Should().Be("mcp");
	}
}
