using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Orchestra.Engine.Tests.TestHelpers;
using Xunit;

namespace Orchestra.Engine.Tests.Executor;

/// <summary>
/// Tests for <see cref="OrchestrationStepExecutor"/> — the engine-level executor that
/// invokes a child orchestration via <see cref="IChildOrchestrationLauncher"/>.
/// </summary>
public class OrchestrationStepExecutorTests
{
	private static readonly OrchestrationInfo s_parentInfo = new("parent-orch", "1.0.0", "parent-run-123", DateTimeOffset.UtcNow);

	private readonly IOrchestrationReporter _reporter = Substitute.For<IOrchestrationReporter>();
	private readonly ILogger<OrchestrationStepExecutor> _logger = NullLoggerFactory.Instance.CreateLogger<OrchestrationStepExecutor>();

	private OrchestrationStepExecutor CreateExecutor(IChildOrchestrationLauncher launcher, AgentBuilder? agentBuilder = null)
		=> new(launcher, agentBuilder ?? new MockAgentBuilder(), _reporter, _logger);

	private static OrchestrationInvocationStep MakeStep(
		string orchestrationName = "child-orch",
		Dictionary<string, string>? parameters = null,
		OrchestrationInvocationMode mode = OrchestrationInvocationMode.Sync,
		string? inputHandlerPrompt = null,
		string? inputHandlerModel = null,
		int? timeoutSeconds = null) => new()
	{
		Name = "invoke-child",
		Type = OrchestrationStepType.Orchestration,
		OrchestrationName = orchestrationName,
		ChildParameters = parameters ?? [],
		Mode = mode,
		InputHandlerPrompt = inputHandlerPrompt,
		InputHandlerModel = inputHandlerModel,
		TimeoutSeconds = timeoutSeconds,
	};

	private static OrchestrationExecutionContext MakeContext(Dictionary<string, string>? parameters = null)
		=> new()
		{
			OrchestrationInfo = s_parentInfo,
			Parameters = parameters ?? [],
		};

	private static ChildOrchestrationHandle MakeHandle(
		string executionId = "child-exec-1",
		string orchestrationId = "child-orch",
		string orchestrationName = "child-orch",
		ChildOrchestrationResult? terminal = null,
		IOrchestrationReporter? reporter = null)
	{
		var startedAt = DateTimeOffset.UtcNow;
		terminal ??= new ChildOrchestrationResult
		{
			ExecutionId = executionId,
			OrchestrationId = orchestrationId,
			OrchestrationName = orchestrationName,
			Status = ExecutionStatus.Succeeded,
			FinalContent = "child-output",
			StartedAt = startedAt,
			CompletedAt = DateTimeOffset.UtcNow,
		};
		return new ChildOrchestrationHandle
		{
			ExecutionId = executionId,
			OrchestrationId = orchestrationId,
			OrchestrationName = orchestrationName,
			Reporter = reporter ?? NullOrchestrationReporter.Instance,
			StartedAt = startedAt,
			Completion = Task.FromResult(terminal),
		};
	}

	// ── Sync mode ─────────────────────────────────────────────────────────────

	[Fact]
	public async Task SyncSuccess_ReturnsChildFinalContent()
	{
		var launcher = Substitute.For<IChildOrchestrationLauncher>();
		launcher.LaunchAsync(Arg.Any<ChildLaunchRequest>(), Arg.Any<CancellationToken>())
			.Returns(MakeHandle(terminal: new ChildOrchestrationResult
			{
				ExecutionId = "exec-1",
				OrchestrationId = "child",
				OrchestrationName = "child",
				Status = ExecutionStatus.Succeeded,
				FinalContent = "the-final-content",
				StartedAt = DateTimeOffset.UtcNow,
				CompletedAt = DateTimeOffset.UtcNow,
			}));

		var executor = CreateExecutor(launcher);
		var step = MakeStep();

		var result = await executor.ExecuteAsync(step, MakeContext());

		result.Status.Should().Be(ExecutionStatus.Succeeded);
		result.Content.Should().Be("the-final-content");
	}

	[Fact]
	public async Task SyncFailed_ReturnsFailedWithChildErrorMessage()
	{
		var launcher = Substitute.For<IChildOrchestrationLauncher>();
		launcher.LaunchAsync(Arg.Any<ChildLaunchRequest>(), Arg.Any<CancellationToken>())
			.Returns(MakeHandle(terminal: new ChildOrchestrationResult
			{
				ExecutionId = "exec-1",
				OrchestrationId = "child",
				OrchestrationName = "child",
				Status = ExecutionStatus.Failed,
				ErrorMessage = "child blew up",
				StartedAt = DateTimeOffset.UtcNow,
				CompletedAt = DateTimeOffset.UtcNow,
			}));

		var executor = CreateExecutor(launcher);
		var result = await executor.ExecuteAsync(MakeStep(), MakeContext());

		result.Status.Should().Be(ExecutionStatus.Failed);
		result.ErrorMessage.Should().Be("child blew up");
	}

	[Fact]
	public async Task SyncCancelled_ReturnsFailedStatus()
	{
		var launcher = Substitute.For<IChildOrchestrationLauncher>();
		launcher.LaunchAsync(Arg.Any<ChildLaunchRequest>(), Arg.Any<CancellationToken>())
			.Returns(MakeHandle(terminal: new ChildOrchestrationResult
			{
				ExecutionId = "exec-1",
				OrchestrationId = "child",
				OrchestrationName = "child",
				Status = ExecutionStatus.Cancelled,
				ErrorMessage = "cancelled by parent",
				StartedAt = DateTimeOffset.UtcNow,
				CompletedAt = DateTimeOffset.UtcNow,
			}));

		var executor = CreateExecutor(launcher);
		var result = await executor.ExecuteAsync(MakeStep(), MakeContext());

		result.Status.Should().Be(ExecutionStatus.Failed);
		result.ErrorMessage.Should().Contain("cancelled");
	}

	[Fact]
	public async Task LaunchException_ReturnsFailed_WithErrorMessage()
	{
		var launcher = Substitute.For<IChildOrchestrationLauncher>();
		launcher.LaunchAsync(Arg.Any<ChildLaunchRequest>(), Arg.Any<CancellationToken>())
			.ThrowsAsyncForAnyArgs(new ChildOrchestrationLaunchException(
				ChildOrchestrationLaunchException.OrchestrationNotFound,
				"orchestration 'missing' not found"));

		var executor = CreateExecutor(launcher);
		var result = await executor.ExecuteAsync(MakeStep("missing"), MakeContext());

		result.Status.Should().Be(ExecutionStatus.Failed);
		result.ErrorMessage.Should().Contain("missing");
	}

	// ── Async mode ────────────────────────────────────────────────────────────

	[Fact]
	public async Task AsyncMode_ReturnsDispatchJson()
	{
		var launcher = Substitute.For<IChildOrchestrationLauncher>();
		launcher.LaunchAsync(Arg.Any<ChildLaunchRequest>(), Arg.Any<CancellationToken>())
			.Returns(MakeHandle(executionId: "async-exec-42", orchestrationName: "child-orch"));

		var executor = CreateExecutor(launcher);
		var step = MakeStep(mode: OrchestrationInvocationMode.Async);

		var result = await executor.ExecuteAsync(step, MakeContext());

		result.Status.Should().Be(ExecutionStatus.Succeeded);
		using var doc = JsonDocument.Parse(result.Content);
		var root = doc.RootElement;
		root.GetProperty("executionId").GetString().Should().Be("async-exec-42");
		root.GetProperty("status").GetString().Should().Be("dispatched");
		root.GetProperty("orchestrationName").GetString().Should().Be("child-orch");
	}

	// ── Dynamic resolution ───────────────────────────────────────────────────

	[Fact]
	public async Task OrchestrationName_TemplateExpression_ResolvesAtRuntime()
	{
		var launcher = Substitute.For<IChildOrchestrationLauncher>();
		ChildLaunchRequest? captured = null;
		launcher.LaunchAsync(Arg.Do<ChildLaunchRequest>(r => captured = r), Arg.Any<CancellationToken>())
			.Returns(MakeHandle());

		var executor = CreateExecutor(launcher);
		var step = MakeStep("{{param.target}}"); // Templated name
		var ctx = MakeContext(new Dictionary<string, string> { ["target"] = "selected-orch" });

		await executor.ExecuteAsync(step, ctx);

		captured.Should().NotBeNull();
		captured!.OrchestrationId.Should().Be("selected-orch");
	}

	[Fact]
	public async Task EmptyResolvedOrchestrationName_ReturnsFailed()
	{
		var launcher = Substitute.For<IChildOrchestrationLauncher>();
		var executor = CreateExecutor(launcher);
		var step = MakeStep("{{param.target}}");
		var ctx = MakeContext(new Dictionary<string, string> { ["target"] = "" });

		var result = await executor.ExecuteAsync(step, ctx);

		result.Status.Should().Be(ExecutionStatus.Failed);
		result.ErrorMessage.Should().Contain("empty");
	}

	[Fact]
	public async Task ChildParameters_AreTemplateResolved()
	{
		var launcher = Substitute.For<IChildOrchestrationLauncher>();
		ChildLaunchRequest? captured = null;
		launcher.LaunchAsync(Arg.Do<ChildLaunchRequest>(r => captured = r), Arg.Any<CancellationToken>())
			.Returns(MakeHandle());

		var executor = CreateExecutor(launcher);
		var step = MakeStep(parameters: new Dictionary<string, string>
		{
			["who"] = "{{param.name}}",
			["literal"] = "static-value",
		});
		var ctx = MakeContext(new Dictionary<string, string> { ["name"] = "world" });

		await executor.ExecuteAsync(step, ctx);

		captured!.Parameters.Should().ContainKey("who").WhoseValue.Should().Be("world");
		captured.Parameters!.Should().ContainKey("literal").WhoseValue.Should().Be("static-value");
	}

	[Fact]
	public async Task ChildParameters_CanUseOrchestrationSourceDirectory()
	{
		var launcher = Substitute.For<IChildOrchestrationLauncher>();
		ChildLaunchRequest? captured = null;
		launcher.LaunchAsync(Arg.Do<ChildLaunchRequest>(r => captured = r), Arg.Any<CancellationToken>())
			.Returns(MakeHandle());

		var executor = CreateExecutor(launcher);
		var step = MakeStep(parameters: new Dictionary<string, string>
		{
			["outputPath"] = "{{orchestration.sourceDirectory}}/../Ephermal/{{orchestration.runId}}.yaml",
		});
		var sourceDirectory = Path.GetFullPath(Path.Combine("workspace", "orchestrations", "System"));
		var ctx = new OrchestrationExecutionContext
		{
			OrchestrationInfo = s_parentInfo with { SourceDirectory = sourceDirectory },
			Parameters = [],
		};

		await executor.ExecuteAsync(step, ctx);

		captured!.Parameters.Should().ContainKey("outputPath")
			.WhoseValue.Should().Be($"{sourceDirectory}/../Ephermal/{s_parentInfo.RunId}.yaml");
	}

	// ── Parent context lineage ────────────────────────────────────────────────

	[Fact]
	public async Task LaunchRequest_CarriesParentExecutionContext()
	{
		var launcher = Substitute.For<IChildOrchestrationLauncher>();
		ChildLaunchRequest? captured = null;
		launcher.LaunchAsync(Arg.Do<ChildLaunchRequest>(r => captured = r), Arg.Any<CancellationToken>())
			.Returns(MakeHandle());

		var executor = CreateExecutor(launcher);
		await executor.ExecuteAsync(MakeStep(), MakeContext());

		captured!.ParentContext.Should().NotBeNull();
		captured.ParentContext!.ParentExecutionId.Should().Be(s_parentInfo.RunId);
		captured.ParentContext.ParentStepName.Should().Be("invoke-child");
	}

	// ── Mode → ChildLaunchMode mapping ────────────────────────────────────────

	[Fact]
	public async Task SyncMode_PassesSyncToLauncher()
	{
		var launcher = Substitute.For<IChildOrchestrationLauncher>();
		ChildLaunchRequest? captured = null;
		launcher.LaunchAsync(Arg.Do<ChildLaunchRequest>(r => captured = r), Arg.Any<CancellationToken>())
			.Returns(MakeHandle());

		var executor = CreateExecutor(launcher);
		await executor.ExecuteAsync(MakeStep(mode: OrchestrationInvocationMode.Sync), MakeContext());

		captured!.Mode.Should().Be(ChildLaunchMode.Sync);
	}

	[Fact]
	public async Task AsyncMode_PassesAsyncToLauncher()
	{
		var launcher = Substitute.For<IChildOrchestrationLauncher>();
		ChildLaunchRequest? captured = null;
		launcher.LaunchAsync(Arg.Do<ChildLaunchRequest>(r => captured = r), Arg.Any<CancellationToken>())
			.Returns(MakeHandle());

		var executor = CreateExecutor(launcher);
		await executor.ExecuteAsync(MakeStep(mode: OrchestrationInvocationMode.Async), MakeContext());

		captured!.Mode.Should().Be(ChildLaunchMode.Async);
	}

	[Fact]
	public async Task TimeoutSeconds_OnlyPassedInSyncMode()
	{
		var launcher = Substitute.For<IChildOrchestrationLauncher>();
		ChildLaunchRequest? captured = null;
		launcher.LaunchAsync(Arg.Do<ChildLaunchRequest>(r => captured = r), Arg.Any<CancellationToken>())
			.Returns(MakeHandle());

		var executor = CreateExecutor(launcher);
		await executor.ExecuteAsync(MakeStep(mode: OrchestrationInvocationMode.Sync, timeoutSeconds: 999), MakeContext());
		captured!.TimeoutSeconds.Should().Be(999);

		captured = null;
		await executor.ExecuteAsync(MakeStep(mode: OrchestrationInvocationMode.Async, timeoutSeconds: 999), MakeContext());
		captured!.TimeoutSeconds.Should().BeNull("async mode runs detached, hard timeout makes no sense");
	}

	// ── Input handler ─────────────────────────────────────────────────────────

	[Fact]
	public async Task InputHandlerPrompt_BuildsPreExecutionTransform()
	{
		var launcher = Substitute.For<IChildOrchestrationLauncher>();
		ChildLaunchRequest? captured = null;
		launcher.LaunchAsync(Arg.Do<ChildLaunchRequest>(r => captured = r), Arg.Any<CancellationToken>())
			.Returns(MakeHandle());

		var executor = CreateExecutor(launcher);
		var step = MakeStep(
			parameters: new Dictionary<string, string> { ["raw"] = "value" },
			inputHandlerPrompt: "Reshape these inputs");

		await executor.ExecuteAsync(step, MakeContext());

		captured!.PreExecutionParameterTransform.Should().NotBeNull(
			"InputHandlerPrompt + non-empty parameters must produce a transform delegate");
	}

	[Fact]
	public async Task NoInputHandler_NoTransformDelegate()
	{
		var launcher = Substitute.For<IChildOrchestrationLauncher>();
		ChildLaunchRequest? captured = null;
		launcher.LaunchAsync(Arg.Do<ChildLaunchRequest>(r => captured = r), Arg.Any<CancellationToken>())
			.Returns(MakeHandle());

		var executor = CreateExecutor(launcher);
		var step = MakeStep(parameters: new Dictionary<string, string> { ["raw"] = "value" });

		await executor.ExecuteAsync(step, MakeContext());

		captured!.PreExecutionParameterTransform.Should().BeNull();
	}

	[Fact]
	public async Task InputHandler_WithEmptyParameters_NoTransformDelegate()
	{
		// When there are no parameters to transform, the transform delegate should not be built
		// (no point invoking the LLM on an empty map).
		var launcher = Substitute.For<IChildOrchestrationLauncher>();
		ChildLaunchRequest? captured = null;
		launcher.LaunchAsync(Arg.Do<ChildLaunchRequest>(r => captured = r), Arg.Any<CancellationToken>())
			.Returns(MakeHandle());

		var executor = CreateExecutor(launcher);
		var step = MakeStep(inputHandlerPrompt: "Reshape inputs"); // No parameters

		await executor.ExecuteAsync(step, MakeContext());

		captured!.PreExecutionParameterTransform.Should().BeNull();
	}

	[Fact]
	public async Task InputHandler_LlmReturnsTransformedMap_ReturnsTransformed()
	{
		// End-to-end of the input handler delegate: when invoked, it should call the agent
		// builder with the configured prompt and return the parsed JSON dictionary.
		var launcher = Substitute.For<IChildOrchestrationLauncher>();
		ChildLaunchRequest? captured = null;
		launcher.LaunchAsync(Arg.Do<ChildLaunchRequest>(r => captured = r), Arg.Any<CancellationToken>())
			.Returns(MakeHandle());

		var agentBuilder = MockAgentBuilderExtensions.CreateWithResponse("""{"shaped": "yes", "raw": "transformed"}""");
		var executor = CreateExecutor(launcher, agentBuilder);
		var step = MakeStep(
			parameters: new Dictionary<string, string> { ["raw"] = "value" },
			inputHandlerPrompt: "Reshape these inputs",
			inputHandlerModel: "claude-opus-4.6");

		await executor.ExecuteAsync(step, MakeContext());

		captured!.PreExecutionParameterTransform.Should().NotBeNull();
		var transformed = await captured.PreExecutionParameterTransform!(CancellationToken.None);
		transformed.Should().NotBeNull();
		transformed!.Should().ContainKey("shaped").WhoseValue.Should().Be("yes");
		transformed.Should().ContainKey("raw").WhoseValue.Should().Be("transformed");
	}

	[Fact]
	public async Task InputHandler_LlmReturnsInvalidJson_FallsBackToNull()
	{
		var launcher = Substitute.For<IChildOrchestrationLauncher>();
		ChildLaunchRequest? captured = null;
		launcher.LaunchAsync(Arg.Do<ChildLaunchRequest>(r => captured = r), Arg.Any<CancellationToken>())
			.Returns(MakeHandle());

		var agentBuilder = MockAgentBuilderExtensions.CreateWithResponse("not valid json");
		var executor = CreateExecutor(launcher, agentBuilder);
		var step = MakeStep(
			parameters: new Dictionary<string, string> { ["raw"] = "value" },
			inputHandlerPrompt: "Reshape these inputs");

		await executor.ExecuteAsync(step, MakeContext());

		var transformed = await captured!.PreExecutionParameterTransform!(CancellationToken.None);
		transformed.Should().BeNull("invalid JSON should fall back to null so the original parameters are used");
	}

	// ── ChildOrchestrationInfo population ──────────────────────────────────────

	[Fact]
	public async Task SyncSuccess_PopulatesChildOrchestrationInfo_WithAllStepResults()
	{
		// A successful child run must expose its executionId, status, and per-step results on
		// the parent step's ExecutionResult so templates like {{stepName.steps.X.output}} and
		// {{stepName.executionId}} can drill into the child without going through MCP.
		var stepResults = new Dictionary<string, ExecutionResult>(StringComparer.OrdinalIgnoreCase)
		{
			["validate"] = ExecutionResult.Succeeded("validate-content", rawContent: "validate-raw"),
			["build"] = ExecutionResult.Succeeded("build-output", savedFiles: ["/tmp/build.log"]),
		};
		var orchResult = new OrchestrationResult
		{
			Status = ExecutionStatus.Succeeded,
			Results = stepResults,
			StepResults = stepResults,
		};
		var launcher = Substitute.For<IChildOrchestrationLauncher>();
		launcher.LaunchAsync(Arg.Any<ChildLaunchRequest>(), Arg.Any<CancellationToken>())
			.Returns(MakeHandle(executionId: "child-success-id", terminal: new ChildOrchestrationResult
			{
				ExecutionId = "child-success-id",
				OrchestrationId = "child-orch",
				OrchestrationName = "child-orch",
				Status = ExecutionStatus.Succeeded,
				FinalContent = "build-output",
				OrchestrationResult = orchResult,
				StartedAt = DateTimeOffset.UtcNow,
				CompletedAt = DateTimeOffset.UtcNow,
			}));

		var executor = CreateExecutor(launcher);
		var result = await executor.ExecuteAsync(MakeStep(), MakeContext());

		result.Status.Should().Be(ExecutionStatus.Succeeded);
		result.ChildOrchestrationInfo.Should().NotBeNull();
		result.ChildOrchestrationInfo!.ExecutionId.Should().Be("child-success-id");
		result.ChildOrchestrationInfo.Status.Should().Be(ExecutionStatus.Succeeded);
		result.ChildOrchestrationInfo.StepResults.Should().ContainKeys("validate", "build");
		result.ChildOrchestrationInfo.StepResults["validate"].Content.Should().Be("validate-content");
		result.ChildOrchestrationInfo.StepResults["validate"].RawContent.Should().Be("validate-raw");
		result.ChildOrchestrationInfo.StepResults["build"].SavedFiles.Should().Contain("/tmp/build.log");
	}

	[Fact]
	public async Task SyncFailed_PopulatesChildOrchestrationInfo_WithPartialStepResults_AndPreservesSucceededSiblings()
	{
		// Self-healing scenario: a child run fails on step 'failing' but step 'succeeded-before'
		// completed first. The parent must see BOTH so it can incorporate the partial progress
		// into the next attempt's repair prompt.
		var stepResults = new Dictionary<string, ExecutionResult>(StringComparer.OrdinalIgnoreCase)
		{
			["succeeded-before"] = ExecutionResult.Succeeded("good-content"),
			["failing"] = ExecutionResult.Failed("this is why it failed"),
		};
		var orchResult = new OrchestrationResult
		{
			Status = ExecutionStatus.Failed,
			Results = stepResults,
			StepResults = stepResults,
		};
		var launcher = Substitute.For<IChildOrchestrationLauncher>();
		launcher.LaunchAsync(Arg.Any<ChildLaunchRequest>(), Arg.Any<CancellationToken>())
			.Returns(MakeHandle(executionId: "child-failed-id", terminal: new ChildOrchestrationResult
			{
				ExecutionId = "child-failed-id",
				OrchestrationId = "child-orch",
				OrchestrationName = "child-orch",
				Status = ExecutionStatus.Failed,
				ErrorMessage = "overall child failure",
				OrchestrationResult = orchResult,
				StartedAt = DateTimeOffset.UtcNow,
				CompletedAt = DateTimeOffset.UtcNow,
			}));

		var executor = CreateExecutor(launcher);
		var result = await executor.ExecuteAsync(MakeStep(), MakeContext());

		result.Status.Should().Be(ExecutionStatus.Failed);
		result.ChildOrchestrationInfo.Should().NotBeNull();
		result.ChildOrchestrationInfo!.Status.Should().Be(ExecutionStatus.Failed);
		result.ChildOrchestrationInfo.ErrorMessage.Should().Be("overall child failure");
		result.ChildOrchestrationInfo.StepResults.Should().ContainKeys("succeeded-before", "failing");
		result.ChildOrchestrationInfo.StepResults.Should()
			.HaveCount(2, "the parent needs visibility into both succeeded siblings AND the failing step to repair");
		result.ChildOrchestrationInfo.StepResults["succeeded-before"].Status.Should().Be(ExecutionStatus.Succeeded);
		result.ChildOrchestrationInfo.StepResults["failing"].Status.Should().Be(ExecutionStatus.Failed);
		result.ChildOrchestrationInfo.StepResults["failing"].ErrorMessage.Should().Be("this is why it failed");
	}

	[Fact]
	public async Task SyncCancelled_PopulatesChildOrchestrationInfo_WithCancellationDetails()
	{
		var cancellation = new CancellationDetails
		{
			Kind = CancellationCauseKind.External,
			Detail = "user-initiated cancel",
			Source = "caller",
		};
		var stepResults = new Dictionary<string, ExecutionResult>(StringComparer.OrdinalIgnoreCase)
		{
			["before-cancel"] = ExecutionResult.Succeeded("happened first"),
		};
		var orchResult = new OrchestrationResult
		{
			Status = ExecutionStatus.Cancelled,
			Results = stepResults,
			StepResults = stepResults,
			Cancellation = cancellation,
		};
		var launcher = Substitute.For<IChildOrchestrationLauncher>();
		launcher.LaunchAsync(Arg.Any<ChildLaunchRequest>(), Arg.Any<CancellationToken>())
			.Returns(MakeHandle(executionId: "child-cancel-id", terminal: new ChildOrchestrationResult
			{
				ExecutionId = "child-cancel-id",
				OrchestrationId = "child-orch",
				OrchestrationName = "child-orch",
				Status = ExecutionStatus.Cancelled,
				ErrorMessage = "cancelled",
				OrchestrationResult = orchResult,
				StartedAt = DateTimeOffset.UtcNow,
				CompletedAt = DateTimeOffset.UtcNow,
			}));

		var executor = CreateExecutor(launcher);
		var result = await executor.ExecuteAsync(MakeStep(), MakeContext());

		result.Status.Should().Be(ExecutionStatus.Failed); // mapped from Cancelled
		result.ChildOrchestrationInfo.Should().NotBeNull();
		result.ChildOrchestrationInfo!.Status.Should().Be(ExecutionStatus.Cancelled);
		result.ChildOrchestrationInfo.Cancellation.Should().NotBeNull();
		result.ChildOrchestrationInfo.Cancellation!.Detail.Should().Be("user-initiated cancel");
		result.ChildOrchestrationInfo.StepResults.Should().ContainKey("before-cancel");
	}

	[Fact]
	public async Task AsyncMode_PopulatesChildOrchestrationInfo_WithDispatchedStatus()
	{
		var launcher = Substitute.For<IChildOrchestrationLauncher>();
		launcher.LaunchAsync(Arg.Any<ChildLaunchRequest>(), Arg.Any<CancellationToken>())
			.Returns(MakeHandle(executionId: "async-id-99"));

		var executor = CreateExecutor(launcher);
		var step = MakeStep(mode: OrchestrationInvocationMode.Async);

		var result = await executor.ExecuteAsync(step, MakeContext());

		result.Status.Should().Be(ExecutionStatus.Succeeded);
		result.ChildOrchestrationInfo.Should().NotBeNull();
		result.ChildOrchestrationInfo!.ExecutionId.Should().Be("async-id-99");
		// Async dispatch is "Pending" — surfaced as "pending" in templates ("dispatched but
		// not yet known to a terminal state").
		result.ChildOrchestrationInfo.Status.Should().Be(ExecutionStatus.Pending);
		result.ChildOrchestrationInfo.StepResults.Should().BeEmpty();
	}

	[Fact]
	public async Task LaunchException_PopulatesMinimalChildOrchestrationInfo()
	{
		var launcher = Substitute.For<IChildOrchestrationLauncher>();
		launcher.LaunchAsync(Arg.Any<ChildLaunchRequest>(), Arg.Any<CancellationToken>())
			.ThrowsAsyncForAnyArgs(new ChildOrchestrationLaunchException(
				ChildOrchestrationLaunchException.OrchestrationNotFound,
				"missing"));

		var executor = CreateExecutor(launcher);
		var result = await executor.ExecuteAsync(MakeStep("missing-orch"), MakeContext());

		result.Status.Should().Be(ExecutionStatus.Failed);
		result.ChildOrchestrationInfo.Should().NotBeNull();
		result.ChildOrchestrationInfo!.OrchestrationName.Should().Be("missing-orch");
		result.ChildOrchestrationInfo.Status.Should().Be(ExecutionStatus.Failed);
		result.ChildOrchestrationInfo.ErrorMessage.Should().Contain("missing");
	}

	// ── forEach fan-out ───────────────────────────────────────────────────────

	private static OrchestrationInvocationStep MakeForEachStep(
		string forEach,
		string itemParameter = "itemData",
		string orchestrationName = "child-orch",
		Dictionary<string, string>? staticParameters = null,
		string? forEachPath = null,
		int? maxConcurrency = null,
		bool continueOnItemFailure = true,
		OrchestrationInvocationMode mode = OrchestrationInvocationMode.Sync) => new()
	{
		Name = "dispatch-children",
		Type = OrchestrationStepType.Orchestration,
		OrchestrationName = orchestrationName,
		ChildParameters = staticParameters ?? [],
		Mode = mode,
		ForEach = forEach,
		ForEachPath = forEachPath,
		ItemParameter = itemParameter,
		MaxConcurrency = maxConcurrency,
		ContinueOnItemFailure = continueOnItemFailure,
	};

	[Fact]
	public async Task ForEach_EmptyArray_SucceedsWithEmptyRollup()
	{
		var launcher = Substitute.For<IChildOrchestrationLauncher>();
		var executor = CreateExecutor(launcher);

		var step = MakeForEachStep(forEach: "[]");
		var result = await executor.ExecuteAsync(step, MakeContext());

		result.Status.Should().Be(ExecutionStatus.Succeeded);
		await launcher.DidNotReceiveWithAnyArgs().LaunchAsync(default!, default);

		using var doc = JsonDocument.Parse(result.Content);
		doc.RootElement.GetProperty("totalDispatched").GetInt32().Should().Be(0);
		doc.RootElement.GetProperty("succeeded").GetInt32().Should().Be(0);
		doc.RootElement.GetProperty("failed").GetInt32().Should().Be(0);
		doc.RootElement.GetProperty("results").GetArrayLength().Should().Be(0);
	}

	[Fact]
	public async Task ForEach_LaunchesOneChildPerItem_AggregatesResults()
	{
		var launcher = Substitute.For<IChildOrchestrationLauncher>();
		var launchCount = 0;
		launcher.LaunchAsync(Arg.Any<ChildLaunchRequest>(), Arg.Any<CancellationToken>())
			.Returns(call =>
			{
				var req = call.Arg<ChildLaunchRequest>();
				var idx = Interlocked.Increment(ref launchCount);
				var execId = $"exec-{idx}";
				return MakeHandle(executionId: execId, terminal: new ChildOrchestrationResult
				{
					ExecutionId = execId,
					OrchestrationId = req.OrchestrationId,
					OrchestrationName = req.OrchestrationId,
					Status = ExecutionStatus.Succeeded,
					FinalContent = $"final-{req.Parameters["itemData"]}",
					StartedAt = DateTimeOffset.UtcNow,
					CompletedAt = DateTimeOffset.UtcNow,
				});
			});

		var executor = CreateExecutor(launcher);
		var step = MakeForEachStep(forEach: """[{"id":1},{"id":2},{"id":3}]""");

		var result = await executor.ExecuteAsync(step, MakeContext());

		result.Status.Should().Be(ExecutionStatus.Succeeded);
		launchCount.Should().Be(3);

		using var doc = JsonDocument.Parse(result.Content);
		doc.RootElement.GetProperty("totalDispatched").GetInt32().Should().Be(3);
		doc.RootElement.GetProperty("succeeded").GetInt32().Should().Be(3);
		doc.RootElement.GetProperty("failed").GetInt32().Should().Be(0);
		var results = doc.RootElement.GetProperty("results");
		results.GetArrayLength().Should().Be(3);
		// Each entry includes the per-item input JSON verbatim.
		foreach (var entry in results.EnumerateArray())
		{
			entry.GetProperty("status").GetString().Should().Be("succeeded");
			entry.GetProperty("input").GetRawText().Should().Contain("id");
		}
	}

	[Fact]
	public async Task ForEach_ForEachPath_DrillsIntoJsonObject()
	{
		var launcher = Substitute.For<IChildOrchestrationLauncher>();
		launcher.LaunchAsync(Arg.Any<ChildLaunchRequest>(), Arg.Any<CancellationToken>())
			.Returns(call =>
			{
				var req = call.Arg<ChildLaunchRequest>();
				return MakeHandle(executionId: "x", terminal: new ChildOrchestrationResult
				{
					ExecutionId = "x",
					OrchestrationId = req.OrchestrationId,
					OrchestrationName = req.OrchestrationId,
					Status = ExecutionStatus.Succeeded,
					FinalContent = "ok",
					StartedAt = DateTimeOffset.UtcNow,
					CompletedAt = DateTimeOffset.UtcNow,
				});
			});

		var executor = CreateExecutor(launcher);
		var step = MakeForEachStep(
			forEach: """{"meetingsToProcess":[{"id":"a"},{"id":"b"}],"meetingsSkipped":[]}""",
			forEachPath: "meetingsToProcess");

		var result = await executor.ExecuteAsync(step, MakeContext());

		result.Status.Should().Be(ExecutionStatus.Succeeded);
		using var doc = JsonDocument.Parse(result.Content);
		doc.RootElement.GetProperty("totalDispatched").GetInt32().Should().Be(2);
	}

	[Fact]
	public async Task ForEach_MixedResults_CapturesFailuresAndStillSucceeds_WhenContinueOnItemFailure()
	{
		var launcher = Substitute.For<IChildOrchestrationLauncher>();
		var launchIndex = 0;
		launcher.LaunchAsync(Arg.Any<ChildLaunchRequest>(), Arg.Any<CancellationToken>())
			.Returns(call =>
			{
				var req = call.Arg<ChildLaunchRequest>();
				var idx = Interlocked.Increment(ref launchIndex);
				var status = idx == 2 ? ExecutionStatus.Failed : ExecutionStatus.Succeeded;
				return MakeHandle(executionId: $"e-{idx}", terminal: new ChildOrchestrationResult
				{
					ExecutionId = $"e-{idx}",
					OrchestrationId = req.OrchestrationId,
					OrchestrationName = req.OrchestrationId,
					Status = status,
					ErrorMessage = status == ExecutionStatus.Failed ? "boom" : null,
					FinalContent = status == ExecutionStatus.Succeeded ? "ok" : null,
					StartedAt = DateTimeOffset.UtcNow,
					CompletedAt = DateTimeOffset.UtcNow,
				});
			});

		var executor = CreateExecutor(launcher);
		var step = MakeForEachStep(forEach: """[{"id":1},{"id":2},{"id":3}]""", continueOnItemFailure: true);

		var result = await executor.ExecuteAsync(step, MakeContext());

		result.Status.Should().Be(ExecutionStatus.Succeeded);
		using var doc = JsonDocument.Parse(result.Content);
		doc.RootElement.GetProperty("succeeded").GetInt32().Should().Be(2);
		doc.RootElement.GetProperty("failed").GetInt32().Should().Be(1);
	}

	[Fact]
	public async Task ForEach_FailureWithContinueOnItemFailureFalse_FailsStep()
	{
		var launcher = Substitute.For<IChildOrchestrationLauncher>();
		var launchIndex = 0;
		launcher.LaunchAsync(Arg.Any<ChildLaunchRequest>(), Arg.Any<CancellationToken>())
			.Returns(call =>
			{
				var req = call.Arg<ChildLaunchRequest>();
				var idx = Interlocked.Increment(ref launchIndex);
				var status = idx == 2 ? ExecutionStatus.Failed : ExecutionStatus.Succeeded;
				return MakeHandle(executionId: $"e-{idx}", terminal: new ChildOrchestrationResult
				{
					ExecutionId = $"e-{idx}",
					OrchestrationId = req.OrchestrationId,
					OrchestrationName = req.OrchestrationId,
					Status = status,
					ErrorMessage = status == ExecutionStatus.Failed ? "boom" : null,
					FinalContent = status == ExecutionStatus.Succeeded ? "ok" : null,
					StartedAt = DateTimeOffset.UtcNow,
					CompletedAt = DateTimeOffset.UtcNow,
				});
			});

		var executor = CreateExecutor(launcher);
		var step = MakeForEachStep(forEach: """[{"id":1},{"id":2}]""", continueOnItemFailure: false);

		var result = await executor.ExecuteAsync(step, MakeContext());

		result.Status.Should().Be(ExecutionStatus.Failed);
		result.ErrorMessage.Should().Contain("1 of 2");
	}

	[Fact]
	public async Task ForEach_InvalidJson_FailsStep()
	{
		var launcher = Substitute.For<IChildOrchestrationLauncher>();
		var executor = CreateExecutor(launcher);

		var step = MakeForEachStep(forEach: "not-json");
		var result = await executor.ExecuteAsync(step, MakeContext());

		result.Status.Should().Be(ExecutionStatus.Failed);
		await launcher.DidNotReceiveWithAnyArgs().LaunchAsync(default!, default);
	}

	[Fact]
	public async Task ForEach_BindsItemParameterAndStaticParameters()
	{
		var capturedRequests = new List<ChildLaunchRequest>();
		var launcher = Substitute.For<IChildOrchestrationLauncher>();
		launcher.LaunchAsync(Arg.Any<ChildLaunchRequest>(), Arg.Any<CancellationToken>())
			.Returns(call =>
			{
				var req = call.Arg<ChildLaunchRequest>();
				lock (capturedRequests) capturedRequests.Add(req);
				return MakeHandle(executionId: "x", terminal: new ChildOrchestrationResult
				{
					ExecutionId = "x",
					OrchestrationId = req.OrchestrationId,
					OrchestrationName = req.OrchestrationId,
					Status = ExecutionStatus.Succeeded,
					FinalContent = "ok",
					StartedAt = DateTimeOffset.UtcNow,
					CompletedAt = DateTimeOffset.UtcNow,
				});
			});

		var executor = CreateExecutor(launcher);
		var step = MakeForEachStep(
			forEach: """[{"id":1,"name":"alpha"},{"id":2,"name":"beta"}]""",
			itemParameter: "meetingData",
			staticParameters: new Dictionary<string, string>
			{
				["dryRun"] = "false",
				["actionItemsDir"] = "C:/tmp",
			});

		var result = await executor.ExecuteAsync(step, MakeContext());
		result.Status.Should().Be(ExecutionStatus.Succeeded);

		capturedRequests.Should().HaveCount(2);
		foreach (var req in capturedRequests)
		{
			req.Parameters.Should().ContainKey("meetingData");
			req.Parameters.Should().ContainKey("dryRun").WhoseValue.Should().Be("false");
			req.Parameters.Should().ContainKey("actionItemsDir").WhoseValue.Should().Be("C:/tmp");
			req.Parameters["meetingData"].Should().Contain("id");
		}
	}
}
