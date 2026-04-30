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
}
