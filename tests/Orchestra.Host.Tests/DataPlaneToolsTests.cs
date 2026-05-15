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
	public async Task InvokeOrchestration_Sync_OmittedTimeout_UsesConfiguredDefault()
	{
		// Arrange — host configures the default sync timeout. When the LLM doesn't pass
		// `timeoutSeconds`, the tool must resolve the host default and apply it to the
		// ChildLaunchRequest, not the legacy hardcoded 300.
		var accessor = HttpContextWith();
		var (launcher, captured) = FakeLauncher(completeImmediately: true);
		var options = new McpServerOptions
		{
			DefaultInvokeOrchestrationSyncTimeoutSeconds = 1800,
			DefaultOrchestraInvokeTimeoutSeconds = 0,
		};

		// Act — note: NO timeoutSeconds argument
		await DataPlaneTools.InvokeOrchestration(
			launcher,
			accessor,
			options,
			"child-orchestration",
			mode: "sync");

		// Assert
		var req = captured();
		req.Should().NotBeNull();
		req!.TimeoutSeconds.Should().Be(1800,
			"the host-configured default must apply when the LLM omits timeoutSeconds");
	}

	[Fact]
	public async Task InvokeOrchestration_Sync_ExplicitTimeout_OverridesConfiguredDefault()
	{
		// Arrange — host has a default of 1800, caller asks for 120. The explicit value wins.
		var accessor = HttpContextWith();
		var (launcher, captured) = FakeLauncher(completeImmediately: true);
		var options = new McpServerOptions
		{
			DefaultInvokeOrchestrationSyncTimeoutSeconds = 1800,
			DefaultOrchestraInvokeTimeoutSeconds = 0,
		};

		// Act
		await DataPlaneTools.InvokeOrchestration(
			launcher,
			accessor,
			options,
			"child-orchestration",
			mode: "sync",
			timeoutSeconds: 120);

		// Assert
		captured()!.TimeoutSeconds.Should().Be(120,
			"an explicit timeoutSeconds argument must always win over the host-configured default");
	}

	[Fact]
	public async Task InvokeOrchestration_Async_TimeoutDefault_NotPropagated()
	{
		// Async invocations don't wait for completion; the resolved timeoutSeconds (even
		// if it came from the host default) must NOT land on ChildLaunchRequest.
		var accessor = HttpContextWith();
		var (launcher, captured) = FakeLauncher();
		var options = new McpServerOptions
		{
			DefaultInvokeOrchestrationSyncTimeoutSeconds = 1800,
		};

		// Act
		await DataPlaneTools.InvokeOrchestration(
			launcher,
			accessor,
			options,
			"child-orchestration",
			mode: "async");

		// Assert
		captured()!.TimeoutSeconds.Should().BeNull("async mode never carries a sync timeout");
	}

	[Fact]
	public async Task InvokeOrchestration_Sync_TimeoutMismatch_UsesResolvedValueInError()
	{
		// When the LLM doesn't supply `timeoutSeconds`, the timeout-mismatch guard must
		// report the resolved host default in its error payload, not a stale 300.
		var accessor = HttpContextWith();
		var (launcher, captured) = FakeLauncher();
		var options = new McpServerOptions
		{
			DefaultOrchestraInvokeTimeoutSeconds = 60,         // transport: 60s
			DefaultInvokeOrchestrationSyncTimeoutSeconds = 600, // resolved sync wait: 600s
		};

		// Act — no explicit timeoutSeconds, so the resolved default is 600 which exceeds 60-60.
		var json = await DataPlaneTools.InvokeOrchestration(
			launcher,
			accessor,
			options,
			"child-orchestration",
			mode: "sync");

		// Assert
		captured().Should().BeNull("launcher must not be invoked when validation fails");
		json.Should().Contain("timeout-mismatch");
		json.Should().Contain("\"requestedSyncTimeoutSeconds\":600",
			"the error must surface the RESOLVED sync timeout, not a hardcoded value");
		json.Should().Contain("\"transportTimeoutSeconds\":60");
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

	// ── Per-step parity fix: errorMessage + savedFiles + truncation metadata ──

	[Fact]
	public async Task InvokeOrchestration_Sync_PerStepProjection_IncludesErrorMessageAndSavedFiles()
	{
		// Parity with get_orchestration_status: the sync response must surface errorMessage
		// per failed child step AND each step's savedFiles. Without these, a self-healing
		// controller has no way to drill into per-step failures from a single sync call.
		var stepResults = new Dictionary<string, ExecutionResult>(StringComparer.OrdinalIgnoreCase)
		{
			["ok-step"] = ExecutionResult.Succeeded("ok-output"),
			["bad-step"] = ExecutionResult.Failed("compiler-error"),
		};
		var orchResult = new OrchestrationResult
		{
			Status = ExecutionStatus.Failed,
			Results = stepResults,
			StepResults = stepResults,
		};
		var launcher = MakeLauncherCompletingWith(new ChildOrchestrationResult
		{
			ExecutionId = "child-1",
			OrchestrationId = "child",
			OrchestrationName = "child",
			Status = ExecutionStatus.Failed,
			OrchestrationResult = orchResult,
			ErrorMessage = "child failed",
			FinalContent = "final",
			StartedAt = DateTimeOffset.UtcNow,
			CompletedAt = DateTimeOffset.UtcNow,
		});

		var json = await DataPlaneTools.InvokeOrchestration(
			launcher,
			HttpContextWith(),
			new McpServerOptions { DefaultOrchestraInvokeTimeoutSeconds = 0 },
			"child-orchestration",
			mode: "sync");

		using var doc = System.Text.Json.JsonDocument.Parse(json);
		var bad = doc.RootElement.GetProperty("stepResults").GetProperty("bad-step");
		bad.GetProperty("status").GetString().Should().Be("failed");
		bad.GetProperty("errorMessage").GetString().Should().Be("compiler-error",
			"sync invoke must include per-step errorMessage; parity with get_orchestration_status");
		bad.TryGetProperty("contentLength", out _).Should().BeTrue("metadata must include contentLength");
		bad.TryGetProperty("truncated", out _).Should().BeTrue("metadata must include truncated flag");
	}

	[Fact]
	public async Task InvokeOrchestration_Sync_DetailFull_ReturnsUntruncatedContent()
	{
		var bigContent = new string('Q', 50_000);
		var stepResults = new Dictionary<string, ExecutionResult>(StringComparer.OrdinalIgnoreCase)
		{
			["big-step"] = ExecutionResult.Succeeded(bigContent),
		};
		var orchResult = new OrchestrationResult
		{
			Status = ExecutionStatus.Succeeded,
			Results = stepResults,
			StepResults = stepResults,
		};
		var launcher = MakeLauncherCompletingWith(new ChildOrchestrationResult
		{
			ExecutionId = "c",
			OrchestrationId = "child",
			OrchestrationName = "child",
			Status = ExecutionStatus.Succeeded,
			OrchestrationResult = orchResult,
			FinalContent = bigContent,
			StartedAt = DateTimeOffset.UtcNow,
			CompletedAt = DateTimeOffset.UtcNow,
		});

		var json = await DataPlaneTools.InvokeOrchestration(
			launcher,
			HttpContextWith(),
			new McpServerOptions { DefaultOrchestraInvokeTimeoutSeconds = 0 },
			"child-orchestration",
			mode: "sync",
			detail: "full");

		using var doc = System.Text.Json.JsonDocument.Parse(json);
		var content = doc.RootElement.GetProperty("stepResults").GetProperty("big-step").GetProperty("content").GetString()!;
		content.Length.Should().Be(50_000, "detail=full must not truncate");
		doc.RootElement.GetProperty("stepResults").GetProperty("big-step").GetProperty("truncated").GetBoolean().Should().BeFalse();
	}

	[Fact]
	public async Task InvokeOrchestration_Sync_DetailSummary_OmitsContentButKeepsMetadata()
	{
		var bigContent = new string('Q', 20_000);
		var stepResults = new Dictionary<string, ExecutionResult>(StringComparer.OrdinalIgnoreCase)
		{
			["big-step"] = ExecutionResult.Succeeded(bigContent),
		};
		var orchResult = new OrchestrationResult
		{
			Status = ExecutionStatus.Succeeded,
			Results = stepResults,
			StepResults = stepResults,
		};
		var launcher = MakeLauncherCompletingWith(new ChildOrchestrationResult
		{
			ExecutionId = "c",
			OrchestrationId = "child",
			OrchestrationName = "child",
			Status = ExecutionStatus.Succeeded,
			OrchestrationResult = orchResult,
			FinalContent = bigContent,
			StartedAt = DateTimeOffset.UtcNow,
			CompletedAt = DateTimeOffset.UtcNow,
		});

		var json = await DataPlaneTools.InvokeOrchestration(
			launcher,
			HttpContextWith(),
			new McpServerOptions { DefaultOrchestraInvokeTimeoutSeconds = 0 },
			"child-orchestration",
			mode: "sync",
			detail: "summary");

		using var doc = System.Text.Json.JsonDocument.Parse(json);
		var bigStep = doc.RootElement.GetProperty("stepResults").GetProperty("big-step");
		// In summary mode the content field is null/omitted by the serializer's
		// "WhenWritingNull" policy, but contentLength + truncated must remain so the caller
		// can decide whether to follow up with get_orchestration_step.
		var hasContent = bigStep.TryGetProperty("content", out var contentEl);
		(hasContent && contentEl.ValueKind != System.Text.Json.JsonValueKind.Null)
			.Should().BeFalse("detail=summary must omit content but report metadata");
		bigStep.GetProperty("contentLength").GetInt32().Should().Be(20_000);
		bigStep.GetProperty("truncated").GetBoolean().Should().BeTrue();
	}

	[Fact]
	public async Task InvokeOrchestration_Sync_InvalidDetail_ReturnsError()
	{
		var launcher = FakeLauncher(completeImmediately: true).Launcher;
		var json = await DataPlaneTools.InvokeOrchestration(
			launcher, HttpContextWith(), new McpServerOptions { DefaultOrchestraInvokeTimeoutSeconds = 0 },
			"child", detail: "verbose");
		json.Should().Contain("Invalid detail level");
	}

	// ── get_orchestration_step ────────────────────────────────────────────────

	[Fact]
	public async Task GetOrchestrationStep_UnknownExecution_ReturnsError()
	{
		using var store = TempRunStore.Empty();
		var json = await DataPlaneTools.GetOrchestrationStep(store.Store, EmptyActiveExecutions(), "no-such-id", "any-step");
		json.Should().Contain("No run found");
	}

	[Fact]
	public async Task GetOrchestrationStep_UnknownStep_ReturnsErrorWithAvailableList()
	{
		using var store = await TempRunStore.WithSampleRunAsync(
			executionId: "exec-known",
			orchestrationName: "sample",
			stepName: "step-1",
			content: "anything");

		var json = await DataPlaneTools.GetOrchestrationStep(store.Store, EmptyActiveExecutions(), "exec-known", "missing-step");
		json.Should().Contain("not found");
		json.Should().Contain("step-1", "the error must list available steps to help the caller correct the name");
	}

	[Fact]
	public async Task GetOrchestrationStep_ContentSliceAtOffset_ReturnsExactSubrangeAndTotalLength()
	{
		var content = "0123456789ABCDEFGHIJ"; // length 20
		using var store = await TempRunStore.WithSampleRunAsync(
			executionId: "exec-slice",
			orchestrationName: "sample",
			stepName: "the-step",
			content: content);

		var json = await DataPlaneTools.GetOrchestrationStep(store.Store, EmptyActiveExecutions(), "exec-slice", "the-step",
			part: "content", offset: 4, length: 6);

		using var doc = System.Text.Json.JsonDocument.Parse(json);
		var slice = doc.RootElement.GetProperty("slice");
		slice.GetProperty("content").GetString().Should().Be("456789");
		slice.GetProperty("totalLength").GetInt32().Should().Be(20);
		slice.GetProperty("offset").GetInt32().Should().Be(4);
		slice.GetProperty("truncated").GetBoolean().Should().BeTrue();
	}

	[Fact]
	public async Task GetOrchestrationStep_LengthMinusOne_ReturnsRemainder()
	{
		using var store = await TempRunStore.WithSampleRunAsync(
			executionId: "exec-full",
			orchestrationName: "sample",
			stepName: "the-step",
			content: "abcdefghij");

		var json = await DataPlaneTools.GetOrchestrationStep(store.Store, EmptyActiveExecutions(), "exec-full", "the-step",
			part: "content", offset: 3, length: -1);

		using var doc = System.Text.Json.JsonDocument.Parse(json);
		doc.RootElement.GetProperty("slice").GetProperty("content").GetString().Should().Be("defghij");
		doc.RootElement.GetProperty("slice").GetProperty("truncated").GetBoolean().Should().BeFalse();
	}

	[Fact]
	public async Task GetOrchestrationStep_PartAll_ReturnsContentRawAndError()
	{
		using var store = await TempRunStore.WithSampleRunAsync(
			executionId: "exec-all",
			orchestrationName: "sample",
			stepName: "the-step",
			content: "main",
			rawContent: "raw",
			errorMessage: "boom");

		var json = await DataPlaneTools.GetOrchestrationStep(store.Store, EmptyActiveExecutions(), "exec-all", "the-step", part: "all");

		using var doc = System.Text.Json.JsonDocument.Parse(json);
		doc.RootElement.GetProperty("content").GetProperty("content").GetString().Should().Be("main");
		doc.RootElement.GetProperty("rawContent").GetProperty("content").GetString().Should().Be("raw");
		doc.RootElement.GetProperty("errorMessage").GetProperty("content").GetString().Should().Be("boom");
	}

	[Fact]
	public async Task GetOrchestrationStep_InvalidPart_ReturnsError()
	{
		using var store = await TempRunStore.WithSampleRunAsync(
			executionId: "exec-x",
			orchestrationName: "s",
			stepName: "st",
			content: "x");
		var json = await DataPlaneTools.GetOrchestrationStep(store.Store, EmptyActiveExecutions(), "exec-x", "st", part: "garbage");
		json.Should().Contain("Invalid part");
	}

	[Fact]
	public async Task GetOrchestrationStep_ActiveRun_CompletedStep_ReturnsContentWithRunStatusRunning()
	{
		// Self-healing controllers need to read sibling steps of an in-flight orchestration
		// without waiting for it to terminate. The tool must serve that data from the
		// in-memory PartialStepRecords map, marking the response with source="active" and
		// runStatus="running" so the caller knows the overall run is still going even
		// though THIS step is done.
		using var store = TempRunStore.Empty();
		var actives = EmptyActiveExecutions();
		var execId = "live-1";
		var info = new ActiveExecutionInfo
		{
			ExecutionId = execId,
			OrchestrationId = "child-orch",
			OrchestrationName = "child-orch",
			StartedAt = DateTimeOffset.UtcNow,
			TriggeredBy = "manual",
			CancellationTokenSource = new CancellationTokenSource(),
			Reporter = NullOrchestrationReporter.Instance,
			TotalSteps = 3,
			CompletedSteps = 1,
			CurrentStep = "step-2",
		};
		info.PublishStepRecord("step-1", new StepRunRecord
		{
			StepName = "step-1",
			Status = ExecutionStatus.Succeeded,
			StartedAt = DateTimeOffset.UtcNow.AddSeconds(-30),
			CompletedAt = DateTimeOffset.UtcNow.AddSeconds(-25),
			Content = "completed sibling output",
		});
		actives[execId] = info;

		var json = await DataPlaneTools.GetOrchestrationStep(store.Store, actives, execId, "step-1");

		using var doc = System.Text.Json.JsonDocument.Parse(json);
		doc.RootElement.GetProperty("source").GetString().Should().Be("active");
		doc.RootElement.GetProperty("runStatus").GetString().Should().Be("running",
			"runStatus must reflect the OVERALL run status, not the step's status");
		doc.RootElement.GetProperty("status").GetString().Should().Be("succeeded",
			"step status is separate — the step finished, the run is still going");
		doc.RootElement.GetProperty("slice").GetProperty("content").GetString().Should().Be("completed sibling output");

		info.CancellationTokenSource.Dispose();
	}

	[Fact]
	public async Task GetOrchestrationStep_ActiveRun_StillRunningStep_ReturnsInFlightResponse()
	{
		// When the requested step is the currently-executing one, the response is a
		// structured in-flight object (not the slice shape) carrying runStatus and
		// completedStepNames so the caller can decide what siblings to drill into instead.
		using var store = TempRunStore.Empty();
		var actives = EmptyActiveExecutions();
		var info = new ActiveExecutionInfo
		{
			ExecutionId = "live-2",
			OrchestrationId = "child-orch",
			OrchestrationName = "child-orch",
			StartedAt = DateTimeOffset.UtcNow,
			TriggeredBy = "manual",
			CancellationTokenSource = new CancellationTokenSource(),
			Reporter = NullOrchestrationReporter.Instance,
			TotalSteps = 5,
			CompletedSteps = 2,
			CurrentStep = "step-3",
		};
		info.PublishStepRecord("step-1", new StepRunRecord
		{
			StepName = "step-1",
			Status = ExecutionStatus.Succeeded,
			StartedAt = DateTimeOffset.UtcNow.AddSeconds(-50),
			CompletedAt = DateTimeOffset.UtcNow.AddSeconds(-40),
			Content = "a",
		});
		info.PublishStepRecord("step-2", new StepRunRecord
		{
			StepName = "step-2",
			Status = ExecutionStatus.Succeeded,
			StartedAt = DateTimeOffset.UtcNow.AddSeconds(-40),
			CompletedAt = DateTimeOffset.UtcNow.AddSeconds(-30),
			Content = "b",
		});
		actives["live-2"] = info;

		var json = await DataPlaneTools.GetOrchestrationStep(store.Store, actives, "live-2", "step-3");

		using var doc = System.Text.Json.JsonDocument.Parse(json);
		doc.RootElement.GetProperty("error").GetString().Should().Be("step-in-flight");
		doc.RootElement.GetProperty("runStatus").GetString().Should().Be("running");
		doc.RootElement.GetProperty("stepStatus").GetString().Should().Be("running");
		doc.RootElement.GetProperty("currentStep").GetString().Should().Be("step-3");
		// completedStepNames tells the caller which siblings to drill into right now —
		// no guesswork required, single round-trip discovery.
		var completed = doc.RootElement.GetProperty("completedStepNames").EnumerateArray()
			.Select(e => e.GetString()).ToHashSet();
		completed.Should().BeEquivalentTo(new[] { "step-1", "step-2" });

		info.CancellationTokenSource.Dispose();
	}

	[Fact]
	public async Task GetOrchestrationStep_ActiveRun_NotYetStartedStep_ReturnsInFlightWithPendingStatus()
	{
		// The step exists in the orchestration DAG but hasn't been scheduled yet. The hint
		// must guide the caller to either wait or query a different step.
		using var store = TempRunStore.Empty();
		var actives = EmptyActiveExecutions();
		var info = new ActiveExecutionInfo
		{
			ExecutionId = "live-3",
			OrchestrationId = "orch",
			OrchestrationName = "orch",
			StartedAt = DateTimeOffset.UtcNow,
			TriggeredBy = "manual",
			CancellationTokenSource = new CancellationTokenSource(),
			Reporter = NullOrchestrationReporter.Instance,
			CurrentStep = "step-2",
		};
		actives["live-3"] = info;

		var json = await DataPlaneTools.GetOrchestrationStep(store.Store, actives, "live-3", "step-99-not-started");

		using var doc = System.Text.Json.JsonDocument.Parse(json);
		doc.RootElement.GetProperty("error").GetString().Should().Be("step-in-flight");
		doc.RootElement.GetProperty("stepStatus").GetString().Should().Be("pending",
			"unknown / not-yet-started steps map to pending when the run is active");
		doc.RootElement.GetProperty("hint").GetString().Should().Contain("No siblings have completed");

		info.CancellationTokenSource.Dispose();
	}

	[Fact]
	public async Task GetOrchestrationStep_PersistedRun_AnnotatesSourcePersistedAndRunStatus()
	{
		// Symmetric assertion: the persisted path also marks source/runStatus so callers
		// can uniformly distinguish active vs. terminal data.
		using var store = await TempRunStore.WithSampleRunAsync(
			executionId: "exec-persisted",
			orchestrationName: "s",
			stepName: "st",
			content: "done");

		var json = await DataPlaneTools.GetOrchestrationStep(store.Store, EmptyActiveExecutions(), "exec-persisted", "st");

		using var doc = System.Text.Json.JsonDocument.Parse(json);
		doc.RootElement.GetProperty("source").GetString().Should().Be("persisted");
		doc.RootElement.GetProperty("runStatus").GetString().Should().Be("succeeded");
	}

	[Fact]
	public async Task GetOrchestrationStatus_ActiveRun_ExposesCompletedStepNames()
	{
		// The companion signal to get_orchestration_step's active path: callers can read
		// completedStepNames from get_orchestration_status to know WHICH sibling steps they
		// can drill into right now — without trial-and-error 'step-in-flight' responses.
		using var store = TempRunStore.Empty();
		var actives = EmptyActiveExecutions();
		var info = new ActiveExecutionInfo
		{
			ExecutionId = "live-cs",
			OrchestrationId = "orch",
			OrchestrationName = "orch",
			StartedAt = DateTimeOffset.UtcNow,
			TriggeredBy = "manual",
			CancellationTokenSource = new CancellationTokenSource(),
			Reporter = NullOrchestrationReporter.Instance,
		};
		info.PublishStepRecord("validate", new StepRunRecord
		{
			StepName = "validate",
			Status = ExecutionStatus.Succeeded,
			StartedAt = DateTimeOffset.UtcNow.AddSeconds(-10),
			CompletedAt = DateTimeOffset.UtcNow,
			Content = "v",
		});
		info.PublishStepRecord("fetch", new StepRunRecord
		{
			StepName = "fetch",
			Status = ExecutionStatus.Succeeded,
			StartedAt = DateTimeOffset.UtcNow.AddSeconds(-5),
			CompletedAt = DateTimeOffset.UtcNow,
			Content = "f",
		});
		actives["live-cs"] = info;

		var json = await DataPlaneTools.GetOrchestrationStatus(actives, store.Store, "live-cs");

		using var doc = System.Text.Json.JsonDocument.Parse(json);
		var names = doc.RootElement.GetProperty("completedStepNames").EnumerateArray()
			.Select(e => e.GetString()).ToHashSet();
		names.Should().BeEquivalentTo(new[] { "fetch", "validate" });

		info.CancellationTokenSource.Dispose();
	}

	// ── list_child_runs ───────────────────────────────────────────────────────

	private static System.Collections.Concurrent.ConcurrentDictionary<string, ActiveExecutionInfo> EmptyActiveExecutions()
		=> new();

	[Fact]
	public async Task ListChildRuns_WithoutScope_AndNoHeaders_ReturnsError()
	{
		using var store = TempRunStore.Empty();
		var json = await DataPlaneTools.ListChildRuns(store.Store, EmptyActiveExecutions(), HttpContextWith());
		json.Should().Contain("No scope provided");
	}

	[Fact]
	public async Task ListChildRuns_WithRootHeader_DefaultsToCallerSubtree()
	{
		using var store = await TempRunStore.WithChildrenOfRootAsync("root-A",
			(executionId: "kid-1", parent: "root-A", root: "root-A", orchestrationName: "child1"),
			(executionId: "kid-2", parent: "kid-1",  root: "root-A", orchestrationName: "child2"),
			(executionId: "unrelated", parent: "other", root: "other", orchestrationName: "child3"));

		var accessor = HttpContextWith((OrchestraHeaders.RootExecutionId, "root-A"));
		var json = await DataPlaneTools.ListChildRuns(store.Store, EmptyActiveExecutions(), accessor);

		using var doc = System.Text.Json.JsonDocument.Parse(json);
		doc.RootElement.GetProperty("scope").GetProperty("source").GetString().Should().Be("header:root");
		doc.RootElement.GetProperty("count").GetInt32().Should().Be(2);
		var ids = doc.RootElement.GetProperty("runs").EnumerateArray()
			.Select(r => r.GetProperty("executionId").GetString()).ToHashSet();
		ids.Should().BeEquivalentTo(new[] { "kid-1", "kid-2" });
	}

	[Fact]
	public async Task ListChildRuns_WithExplicitParent_ReturnsOnlyDirectChildren()
	{
		using var store = await TempRunStore.WithChildrenOfRootAsync("root-B",
			(executionId: "direct-1", parent: "root-B", root: "root-B", orchestrationName: "child1"),
			(executionId: "grand-1",  parent: "direct-1", root: "root-B", orchestrationName: "child2"));

		var json = await DataPlaneTools.ListChildRuns(store.Store, EmptyActiveExecutions(), HttpContextWith(),
			parentExecutionId: "root-B");

		using var doc = System.Text.Json.JsonDocument.Parse(json);
		doc.RootElement.GetProperty("count").GetInt32().Should().Be(1);
		doc.RootElement.GetProperty("runs")[0].GetProperty("executionId").GetString().Should().Be("direct-1");
		doc.RootElement.GetProperty("scope").GetProperty("source").GetString().Should().Be("argument:parent");
	}

	[Fact]
	public async Task ListChildRuns_StatusFilter_OnlyReturnsMatchingStatus()
	{
		using var store = await TempRunStore.WithChildrenOfRootAsync("root-S",
			(executionId: "succeeded-1", parent: "root-S", root: "root-S", orchestrationName: "c1", status: ExecutionStatus.Succeeded),
			(executionId: "failed-1",    parent: "root-S", root: "root-S", orchestrationName: "c2", status: ExecutionStatus.Failed));

		var json = await DataPlaneTools.ListChildRuns(store.Store, EmptyActiveExecutions(), HttpContextWith(),
			rootExecutionId: "root-S", status: "failed");

		using var doc = System.Text.Json.JsonDocument.Parse(json);
		doc.RootElement.GetProperty("count").GetInt32().Should().Be(1);
		doc.RootElement.GetProperty("runs")[0].GetProperty("executionId").GetString().Should().Be("failed-1");
	}

	[Fact]
	public async Task ListChildRuns_InvalidStatus_ReturnsError()
	{
		using var store = TempRunStore.Empty();
		var json = await DataPlaneTools.ListChildRuns(store.Store, EmptyActiveExecutions(), HttpContextWith(),
			rootExecutionId: "x", status: "bogus");
		json.Should().Contain("Invalid status filter");
	}

	[Fact]
	public async Task ListChildRuns_LimitAndOffset_Paginate()
	{
		using var store = await TempRunStore.WithChildrenOfRootAsync("root-P",
			(executionId: "k1", parent: "root-P", root: "root-P", orchestrationName: "c", startedAt: DateTimeOffset.UtcNow.AddSeconds(-30)),
			(executionId: "k2", parent: "root-P", root: "root-P", orchestrationName: "c", startedAt: DateTimeOffset.UtcNow.AddSeconds(-20)),
			(executionId: "k3", parent: "root-P", root: "root-P", orchestrationName: "c", startedAt: DateTimeOffset.UtcNow.AddSeconds(-10)));

		var firstPage = await DataPlaneTools.ListChildRuns(store.Store, EmptyActiveExecutions(), HttpContextWith(),
			rootExecutionId: "root-P", limit: 2, offset: 0);
		var secondPage = await DataPlaneTools.ListChildRuns(store.Store, EmptyActiveExecutions(), HttpContextWith(),
			rootExecutionId: "root-P", limit: 2, offset: 2);

		using var doc1 = System.Text.Json.JsonDocument.Parse(firstPage);
		using var doc2 = System.Text.Json.JsonDocument.Parse(secondPage);
		doc1.RootElement.GetProperty("count").GetInt32().Should().Be(2);
		doc2.RootElement.GetProperty("count").GetInt32().Should().Be(1);
		// Newest-first ordering: k3 starts first, k2 second, k1 last.
		doc1.RootElement.GetProperty("runs")[0].GetProperty("executionId").GetString().Should().Be("k3");
		doc1.RootElement.GetProperty("runs")[1].GetProperty("executionId").GetString().Should().Be("k2");
		doc2.RootElement.GetProperty("runs")[0].GetProperty("executionId").GetString().Should().Be("k1");
	}

	[Fact]
	public async Task ListChildRuns_ParentHeader_FallsBackToRootScope()
	{
		// Older clients (or first-hop sync invocations from agents) may only stamp the parent
		// id; the tool should still scope to a subtree (treating parent as root) instead of
		// erroring out.
		using var store = await TempRunStore.WithChildrenOfRootAsync("parent-FB",
			(executionId: "fb1", parent: "parent-FB", root: "parent-FB", orchestrationName: "c"));

		var json = await DataPlaneTools.ListChildRuns(store.Store, EmptyActiveExecutions(),
			HttpContextWith((OrchestraHeaders.ParentExecutionId, "parent-FB")));

		using var doc = System.Text.Json.JsonDocument.Parse(json);
		doc.RootElement.GetProperty("scope").GetProperty("source").GetString().Should().Be("header:parent-as-root");
		doc.RootElement.GetProperty("count").GetInt32().Should().Be(1);
	}

	[Fact]
	public async Task ListChildRuns_IncludesActiveRunsInCallerSubtree()
	{
		// In-flight runs aren't persisted to FileSystemRunStore until completion, so a
		// self-healing controller polling its children mid-run would miss them without the
		// active-runs merge. Asserts the active record appears with source="active" and
		// the lineage fields surfaced from NestingMetadata.
		using var store = TempRunStore.Empty();
		var actives = EmptyActiveExecutions();
		var activeId = "live-child-1";
		actives[activeId] = new ActiveExecutionInfo
		{
			ExecutionId = activeId,
			OrchestrationId = "child-orch",
			OrchestrationName = "child-orch",
			StartedAt = DateTimeOffset.UtcNow,
			TriggeredBy = "orchestration:root-AR",
			CancellationTokenSource = new CancellationTokenSource(),
			Reporter = NullOrchestrationReporter.Instance,
			NestingMetadata = new Orchestra.Host.McpServer.ExecutionMetadata
			{
				ParentExecutionId = "root-AR",
				ParentStepName = "invoke",
				RootExecutionId = "root-AR",
				Depth = 1,
			},
			TotalSteps = 5,
			CompletedSteps = 2,
			CurrentStep = "build",
		};

		var json = await DataPlaneTools.ListChildRuns(store.Store, actives, HttpContextWith(),
			rootExecutionId: "root-AR");

		using var doc = System.Text.Json.JsonDocument.Parse(json);
		doc.RootElement.GetProperty("count").GetInt32().Should().Be(1);
		var run = doc.RootElement.GetProperty("runs")[0];
		run.GetProperty("executionId").GetString().Should().Be(activeId);
		run.GetProperty("source").GetString().Should().Be("active");
		run.GetProperty("status").GetString().Should().Be("running");
		run.GetProperty("totalSteps").GetInt32().Should().Be(5);
		run.GetProperty("completedSteps").GetInt32().Should().Be(2);
		run.GetProperty("currentStep").GetString().Should().Be("build");
		run.GetProperty("nestingDepth").GetInt32().Should().Be(1);
		run.TryGetProperty("completedAt", out var completedAt).Should().BeFalse(
			"in-flight runs must not fabricate a completedAt");
	}

	[Fact]
	public async Task ListChildRuns_StatusRunning_OnlyReturnsActive()
	{
		// status='running' is the canonical filter for "what's in flight". Persisted runs
		// (which are terminal by definition) must be excluded so the response only shows
		// the live entries.
		using var store = await TempRunStore.WithChildrenOfRootAsync("root-MX",
			(executionId: "finished-1", parent: "root-MX", root: "root-MX", orchestrationName: "child-orch"));
		var actives = EmptyActiveExecutions();
		actives["live-1"] = new ActiveExecutionInfo
		{
			ExecutionId = "live-1",
			OrchestrationId = "child-orch",
			OrchestrationName = "child-orch",
			StartedAt = DateTimeOffset.UtcNow,
			TriggeredBy = "orchestration:root-MX",
			CancellationTokenSource = new CancellationTokenSource(),
			Reporter = NullOrchestrationReporter.Instance,
			NestingMetadata = new Orchestra.Host.McpServer.ExecutionMetadata
			{
				ParentExecutionId = "root-MX",
				RootExecutionId = "root-MX",
				Depth = 1,
			},
		};

		var json = await DataPlaneTools.ListChildRuns(store.Store, actives, HttpContextWith(),
			rootExecutionId: "root-MX", status: "running");

		using var doc = System.Text.Json.JsonDocument.Parse(json);
		doc.RootElement.GetProperty("count").GetInt32().Should().Be(1);
		doc.RootElement.GetProperty("runs")[0].GetProperty("executionId").GetString().Should().Be("live-1");
	}

	[Fact]
	public async Task ListChildRuns_ActiveAndPersistedDuplicate_PrefersActive()
	{
		// During the brief window between an active run finishing and its run.json hitting
		// the file system, the same execution id could appear in both stores. Dedupe in
		// favour of the active entry so callers see the live progress fields.
		using var store = await TempRunStore.WithChildrenOfRootAsync("root-DUP",
			(executionId: "race-id", parent: "root-DUP", root: "root-DUP", orchestrationName: "child-orch"));
		var actives = EmptyActiveExecutions();
		actives["race-id"] = new ActiveExecutionInfo
		{
			ExecutionId = "race-id",
			OrchestrationId = "child-orch",
			OrchestrationName = "child-orch",
			StartedAt = DateTimeOffset.UtcNow,
			TriggeredBy = "orchestration:root-DUP",
			CancellationTokenSource = new CancellationTokenSource(),
			Reporter = NullOrchestrationReporter.Instance,
			NestingMetadata = new Orchestra.Host.McpServer.ExecutionMetadata
			{
				ParentExecutionId = "root-DUP",
				RootExecutionId = "root-DUP",
				Depth = 1,
			},
		};

		var json = await DataPlaneTools.ListChildRuns(store.Store, actives, HttpContextWith(),
			rootExecutionId: "root-DUP");

		using var doc = System.Text.Json.JsonDocument.Parse(json);
		doc.RootElement.GetProperty("count").GetInt32().Should().Be(1,
			"duplicates between active and persisted stores must be deduped");
		doc.RootElement.GetProperty("runs")[0].GetProperty("source").GetString().Should().Be("active");
	}

	// ── helpers ───────────────────────────────────────────────────────────────

	private static IChildOrchestrationLauncher MakeLauncherCompletingWith(ChildOrchestrationResult terminal)
	{
		var launcher = Substitute.For<IChildOrchestrationLauncher>();
		launcher.LaunchAsync(Arg.Any<ChildLaunchRequest>(), Arg.Any<CancellationToken>())
			.Returns(callInfo =>
			{
				var req = callInfo.ArgAt<ChildLaunchRequest>(0);
				return Task.FromResult(new ChildOrchestrationHandle
				{
					ExecutionId = terminal.ExecutionId,
					OrchestrationId = req.OrchestrationId,
					OrchestrationName = req.OrchestrationId,
					Reporter = NullOrchestrationReporter.Instance,
					StartedAt = DateTimeOffset.UtcNow,
					Completion = Task.FromResult(terminal),
				});
			});
		return launcher;
	}
}

/// <summary>
/// Self-contained FileSystemRunStore wrapper that writes minimal run.json fixtures into a
/// temp directory for tests that need to exercise <c>get_orchestration_step</c> /
/// <c>list_child_runs</c> against real persisted runs.
/// </summary>
internal sealed class TempRunStore : IDisposable
{
	public Orchestra.Host.Persistence.FileSystemRunStore Store { get; }
	public string Root { get; }

	private TempRunStore(string root, Orchestra.Host.Persistence.FileSystemRunStore store)
	{
		Root = root;
		Store = store;
	}

	public static TempRunStore Empty()
	{
		var root = Path.Combine(Path.GetTempPath(), "orchestra-temprunstore-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);
		return new TempRunStore(root, new Orchestra.Host.Persistence.FileSystemRunStore(root));
	}

	public static async Task<TempRunStore> WithSampleRunAsync(
		string executionId,
		string orchestrationName,
		string stepName,
		string content,
		string? rawContent = null,
		string? errorMessage = null)
	{
		var temp = Empty();
		var record = new OrchestrationRunRecord
		{
			RunId = executionId,
			OrchestrationName = orchestrationName,
			OrchestrationVersion = "1.0.0",
			TriggeredBy = "test",
			StartedAt = DateTimeOffset.UtcNow.AddSeconds(-10),
			CompletedAt = DateTimeOffset.UtcNow,
			Status = errorMessage is null ? ExecutionStatus.Succeeded : ExecutionStatus.Failed,
			FinalContent = content,
			Parameters = new Dictionary<string, string>(),
			StepRecords = new Dictionary<string, StepRunRecord>(StringComparer.OrdinalIgnoreCase)
			{
				[stepName] = new StepRunRecord
				{
					StepName = stepName,
					Status = errorMessage is null ? ExecutionStatus.Succeeded : ExecutionStatus.Failed,
					StartedAt = DateTimeOffset.UtcNow.AddSeconds(-5),
					CompletedAt = DateTimeOffset.UtcNow,
					Content = content,
					RawContent = rawContent,
					ErrorMessage = errorMessage,
				},
			},
			AllStepRecords = new Dictionary<string, StepRunRecord>(StringComparer.OrdinalIgnoreCase),
		};
		await temp.Store.SaveRunAsync(record, null);
		return temp;
	}

	public static async Task<TempRunStore> WithChildRunsAsync(
		(string executionId, string parent, string root, string orchestrationName, ExecutionStatus status, DateTimeOffset startedAt)[] children)
	{
		var temp = Empty();
		foreach (var c in children)
		{
			var record = new OrchestrationRunRecord
			{
				RunId = c.executionId,
				OrchestrationName = c.orchestrationName,
				OrchestrationVersion = "1.0.0",
				TriggeredBy = $"orchestration:{c.parent}",
				StartedAt = c.startedAt,
				CompletedAt = c.startedAt.AddSeconds(1),
				Status = c.status,
				FinalContent = string.Empty,
				ParentExecutionId = c.parent,
				ParentStepName = "invoke",
				RootExecutionId = c.root,
				NestingDepth = 1,
				Parameters = new Dictionary<string, string>(),
				StepRecords = new Dictionary<string, StepRunRecord>(),
				AllStepRecords = new Dictionary<string, StepRunRecord>(),
			};
			await temp.Store.SaveRunAsync(record, null);
		}
		return temp;
	}

	/// <summary>
	/// Convenience overload that defaults status to Succeeded and assigns staggered start
	/// times so newest-first ordering is deterministic across the test cases.
	/// </summary>
	public static Task<TempRunStore> WithChildrenOfRootAsync(
		string rootExecutionId,
		params (string executionId, string parent, string root, string orchestrationName)[] children)
	{
		var now = DateTimeOffset.UtcNow;
		var augmented = children
			.Select((c, i) => (c.executionId, c.parent, c.root, c.orchestrationName, ExecutionStatus.Succeeded, now.AddSeconds(i)))
			.ToArray();
		return WithChildRunsAsync(augmented);
	}

	/// <summary>
	/// Convenience overload that lets each child declare its own status, with staggered
	/// start times.
	/// </summary>
	public static Task<TempRunStore> WithChildrenOfRootAsync(
		string rootExecutionId,
		params (string executionId, string parent, string root, string orchestrationName, ExecutionStatus status)[] children)
	{
		var now = DateTimeOffset.UtcNow;
		var augmented = children
			.Select((c, i) => (c.executionId, c.parent, c.root, c.orchestrationName, c.status, now.AddSeconds(i)))
			.ToArray();
		return WithChildRunsAsync(augmented);
	}

	/// <summary>
	/// Convenience overload that lets each child declare its own start time, defaulting
	/// status to Succeeded.
	/// </summary>
	public static Task<TempRunStore> WithChildrenOfRootAsync(
		string rootExecutionId,
		params (string executionId, string parent, string root, string orchestrationName, DateTimeOffset startedAt)[] children)
	{
		var augmented = children
			.Select(c => (c.executionId, c.parent, c.root, c.orchestrationName, ExecutionStatus.Succeeded, c.startedAt))
			.ToArray();
		return WithChildRunsAsync(augmented);
	}

	public void Dispose()
	{
		try { Directory.Delete(Root, recursive: true); } catch { /* best-effort cleanup */ }
	}
}
