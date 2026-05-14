using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orchestra.Engine;
using Orchestra.Host.McpServer;
using Orchestra.Host.Triggers;
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
	private static readonly McpServerOptions DefaultOptions = new();

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

	private static (IChildOrchestrationLauncher Launcher, Func<ChildLaunchRequest?> Captured) FakeLauncher(
		bool completeImmediately = false)
	{
		ChildLaunchRequest? captured = null;
		var launcher = Substitute.For<IChildOrchestrationLauncher>();
		launcher
			.LaunchAsync(Arg.Do<ChildLaunchRequest>(r => captured = r), Arg.Any<CancellationToken>())
			.Returns(callInfo =>
			{
				var req = callInfo.ArgAt<ChildLaunchRequest>(0);
				Task<ChildOrchestrationResult> completion;
				if (completeImmediately)
				{
					completion = Task.FromResult(new ChildOrchestrationResult
					{
						ExecutionId = "child-exec-1",
						OrchestrationId = req.OrchestrationId,
						OrchestrationName = req.OrchestrationId,
						Status = ExecutionStatus.Succeeded,
						OrchestrationResult = null,
						ErrorMessage = null,
						FinalContent = "test",
						StartedAt = DateTimeOffset.UtcNow,
						CompletedAt = DateTimeOffset.UtcNow,
						TimedOut = false,
					});
				}
				else
				{
					// Async mode in the tests below — the tool returns the dispatch JSON without
					// awaiting Completion, so we just need a never-completing placeholder.
					completion = new TaskCompletionSource<ChildOrchestrationResult>().Task;
				}
				return Task.FromResult(new ChildOrchestrationHandle
				{
					ExecutionId = "child-exec-1",
					OrchestrationId = req.OrchestrationId,
					OrchestrationName = req.OrchestrationId,
					Reporter = NullOrchestrationReporter.Instance,
					StartedAt = DateTimeOffset.UtcNow,
					Completion = completion,
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
		await DataPlaneTools.InvokeOrchestration(launcher, accessor, DefaultOptions, "child-orchestration");

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
			DefaultOptions,
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
		await DataPlaneTools.InvokeOrchestration(launcher, accessor, DefaultOptions, "child-orchestration");

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
		await DataPlaneTools.InvokeOrchestration(launcher, accessor, DefaultOptions, "child-orchestration");

		// Assert
		var req = captured();
		req!.ParentContext.Should().BeNull();
		req.TriggeredBy.Should().Be("mcp");
	}

	[Fact]
	public async Task InvokeOrchestration_Sync_TimeoutLargerThanTransport_ReturnsTimeoutMismatchError()
	{
		// Arrange — host has a 60s transport timeout, agent asks for 600s sync. The tool must
		// refuse and explain. No child is launched.
		var accessor = HttpContextWith();
		var (launcher, captured) = FakeLauncher();
		var options = new McpServerOptions { DefaultOrchestraInvokeTimeoutSeconds = 60 };

		// Act
		var json = await DataPlaneTools.InvokeOrchestration(
			launcher,
			accessor,
			options,
			"child-orchestration",
			mode: "sync",
			timeoutSeconds: 600);

		// Assert
		captured().Should().BeNull("launcher must not be invoked when validation fails");
		json.Should().Contain("timeout-mismatch");
		json.Should().Contain("600");
		json.Should().Contain("60");
		json.Should().Contain("transportTimeoutSeconds");
	}

	[Fact]
	public async Task InvokeOrchestration_Sync_TransportUnbounded_NoValidation()
	{
		// Arrange — host has DefaultOrchestraInvokeTimeoutSeconds = 0 (unbounded transport).
		// The validation must be skipped even for very large sync timeouts.
		var accessor = HttpContextWith();
		var (launcher, captured) = FakeLauncher(completeImmediately: true);
		var options = new McpServerOptions { DefaultOrchestraInvokeTimeoutSeconds = 0 };

		// Act
		await DataPlaneTools.InvokeOrchestration(
			launcher,
			accessor,
			options,
			"child-orchestration",
			mode: "sync",
			timeoutSeconds: 999999);

		// Assert
		captured().Should().NotBeNull("with transport timeout 0, the call should launch");
		captured()!.TimeoutSeconds.Should().Be(999999);
	}

	[Fact]
	public async Task InvokeOrchestration_Async_TimeoutMismatchValidation_DoesNotApply()
	{
		// Arrange — async invocations don't wait for completion; the transport timeout
		// mismatch is irrelevant. Validation must NOT block them.
		var accessor = HttpContextWith();
		var (launcher, captured) = FakeLauncher();
		var options = new McpServerOptions { DefaultOrchestraInvokeTimeoutSeconds = 60 };

		// Act
		await DataPlaneTools.InvokeOrchestration(
			launcher,
			accessor,
			options,
			"child-orchestration",
			mode: "async",
			timeoutSeconds: 600);

		// Assert
		captured().Should().NotBeNull("async mode ignores timeoutSeconds; validation must be skipped");
	}

	[Fact]
	public void CancelOrchestration_SetsExternalCauseOverrideWithMcpDetail_BeforeCancelling()
	{
		// Verify the cancel_orchestration MCP tool attributes the cancel by setting
		// CancellationCauseOverride on the ActiveExecutionInfo BEFORE calling .Cancel().
		// Without this, the engine's probe would fall back to the generic External record
		// with no detail, producing "cancelled by caller" with no source attribution.
		var execId = "exec-attribution-test";
		var cts = new CancellationTokenSource();
		var info = new ActiveExecutionInfo
		{
			ExecutionId = execId,
			OrchestrationId = "orch",
			OrchestrationName = "orch",
			StartedAt = DateTimeOffset.UtcNow,
			TriggeredBy = "manual",
			CancellationTokenSource = cts,
			Reporter = NullOrchestrationReporter.Instance,
		};

		var executions = new System.Collections.Concurrent.ConcurrentDictionary<string, CancellationTokenSource>();
		executions[execId] = cts;
		var infos = new System.Collections.Concurrent.ConcurrentDictionary<string, ActiveExecutionInfo>();
		infos[execId] = info;

		var loggerFactory = NullLoggerFactory.Instance;

		// Act — cancel without an explicit reason
		var json1 = DataPlaneTools.CancelOrchestration(executions, infos, loggerFactory, execId);

		// Assert — override populated, cancel requested, status updated
		json1.Should().Contain("cancelling");
		info.CancellationCauseOverride.Should().NotBeNull();
		info.CancellationCauseOverride!.Kind.Should().Be(CancellationCauseKind.External);
		info.CancellationCauseOverride.Source.Should().Be("caller");
		info.CancellationCauseOverride.Detail.Should().Be("mcp:cancel_orchestration");
		info.CancellationCauseOverride.RequestedAt.Should().NotBeNull();
		cts.IsCancellationRequested.Should().BeTrue();

		cts.Dispose();
	}

	[Fact]
	public void CancelOrchestration_WithReason_PropagatesReasonIntoDetail()
	{
		// The new `reason` parameter on cancel_orchestration must end up in the run record's
		// cancellation.detail field. This is the primary mechanism for the self-healing
		// pattern to record WHY it cancelled an attempt (e.g., "winning attempt succeeded").
		var execId = "exec-reason-test";
		var cts = new CancellationTokenSource();
		var info = new ActiveExecutionInfo
		{
			ExecutionId = execId,
			OrchestrationId = "orch",
			OrchestrationName = "orch",
			StartedAt = DateTimeOffset.UtcNow,
			TriggeredBy = "manual",
			CancellationTokenSource = cts,
			Reporter = NullOrchestrationReporter.Instance,
		};

		var executions = new System.Collections.Concurrent.ConcurrentDictionary<string, CancellationTokenSource>();
		executions[execId] = cts;
		var infos = new System.Collections.Concurrent.ConcurrentDictionary<string, ActiveExecutionInfo>();
		infos[execId] = info;

		// Act
		var json = DataPlaneTools.CancelOrchestration(
			executions, infos, NullLoggerFactory.Instance, execId,
			reason: "winning attempt succeeded");

		// Assert
		json.Should().Contain("winning attempt succeeded");
		info.CancellationCauseOverride!.Detail.Should().Be("mcp:cancel_orchestration: winning attempt succeeded");

		cts.Dispose();
	}
}
