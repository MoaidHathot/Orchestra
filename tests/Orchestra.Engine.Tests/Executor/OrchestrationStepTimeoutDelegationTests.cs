using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orchestra.Engine.Tests.TestHelpers;

namespace Orchestra.Engine.Tests.Executor;

/// <summary>
/// Executor-level coverage for the interplay between the engine's generic per-step timeout and
/// the child launcher's sync-invoke timeout on <c>Orchestration</c> steps, plus the visibility
/// of the pre-execution parameter transform (input handler) phase.
/// </summary>
/// <remarks>
/// Background: a single-child sync <c>Orchestration</c> step used to get TWO timers for the same
/// <c>timeoutSeconds</c> — the engine's generic step CTS (armed first) and the launcher's
/// sync-invoke CTS. The generic one always won, cancelling the child through the caller token
/// and leaving its run record with an inaccurate cause instead of <c>SyncInvokeTimeout</c>, and
/// the parent step reported a generic failure instead of a timeout.
/// </remarks>
public class OrchestrationStepTimeoutDelegationTests
{
	private readonly IScheduler _scheduler = new OrchestrationScheduler();
	private readonly ILoggerFactory _loggerFactory = NullLoggerFactory.Instance;

	private static Orchestration SingleSyncChildStep(int? timeoutSeconds, int? defaultStepTimeoutSeconds = null) => new()
	{
		Name = "parent",
		Description = "parent with one sync Orchestration step",
		DefaultStepTimeoutSeconds = defaultStepTimeoutSeconds,
		Steps =
		[
			new OrchestrationInvocationStep
			{
				Name = "invoke-child",
				Type = OrchestrationStepType.Orchestration,
				OrchestrationName = "child-orch",
				Mode = OrchestrationInvocationMode.Sync,
				TimeoutSeconds = timeoutSeconds,
			},
		],
	};

	private static Orchestration ForEachSyncChildStep(int timeoutSeconds) => new()
	{
		Name = "parent-foreach",
		Description = "parent fanning out sync children",
		Steps =
		[
			new OrchestrationInvocationStep
			{
				Name = "fan-out",
				Type = OrchestrationStepType.Orchestration,
				OrchestrationName = "child-orch",
				Mode = OrchestrationInvocationMode.Sync,
				TimeoutSeconds = timeoutSeconds,
				ForEach = "[1]",
				ItemParameter = "item",
			},
		],
	};

	/// <summary>
	/// A launcher stand-in whose child never finishes on its own. The completion resolves when
	/// either the caller token fires (returns a plain Cancelled result, TimedOut=false — what the
	/// real launcher does when the parent's token cancels the child) or the test releases it via
	/// <see cref="TriggerSyncTimeout"/> (returns Cancelled + TimedOut=true — what the real
	/// launcher does when its own sync-invoke timeout fires).
	/// </summary>
	private sealed class HangingChildLauncher : IChildOrchestrationLauncher
	{
		private readonly TaskCompletionSource<ChildOrchestrationResult> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

		public ChildLaunchRequest? CapturedRequest { get; private set; }
		public CancellationToken CapturedToken { get; private set; }
		public TaskCompletionSource Launched { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

		public Task<ChildOrchestrationHandle> LaunchAsync(ChildLaunchRequest request, CancellationToken cancellationToken = default)
		{
			CapturedRequest = request;
			CapturedToken = cancellationToken;

			cancellationToken.Register(() => _completion.TrySetResult(new ChildOrchestrationResult
			{
				ExecutionId = "child-exec",
				OrchestrationId = request.OrchestrationId,
				OrchestrationName = request.OrchestrationId,
				Status = ExecutionStatus.Cancelled,
				TimedOut = false,
				ErrorMessage = "Child orchestration ended with status 'Cancelled': cancelled by caller: propagated from parent.",
				StartedAt = DateTimeOffset.UtcNow,
				CompletedAt = DateTimeOffset.UtcNow,
			}));

			Launched.TrySetResult();
			return Task.FromResult(new ChildOrchestrationHandle
			{
				ExecutionId = "child-exec",
				OrchestrationId = request.OrchestrationId,
				OrchestrationName = request.OrchestrationId,
				Reporter = NullOrchestrationReporter.Instance,
				StartedAt = DateTimeOffset.UtcNow,
				Completion = _completion.Task,
			});
		}

		public void TriggerSyncTimeout(int timeoutSeconds) => _completion.TrySetResult(new ChildOrchestrationResult
		{
			ExecutionId = "child-exec",
			OrchestrationId = CapturedRequest!.OrchestrationId,
			OrchestrationName = CapturedRequest.OrchestrationId,
			Status = ExecutionStatus.Cancelled,
			TimedOut = true,
			ErrorMessage = $"Child orchestration ended with status 'Cancelled': sync invocation timed out after {timeoutSeconds}s.",
			StartedAt = DateTimeOffset.UtcNow,
			CompletedAt = DateTimeOffset.UtcNow,
		});
	}

	[Fact]
	public async Task SyncOrchestrationStep_WithTimeoutSeconds_EngineDoesNotArmGenericStepTimeout()
	{
		// Arrange — step timeoutSeconds = 1, but the launcher (our stand-in) is the one that
		// should enforce it. If the engine still armed its own CTS, the token handed to the
		// launcher would be cancelled ~1s after the step started.
		var launcher = new HangingChildLauncher();
		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new OrchestrationExecutor(_scheduler, new MockAgentBuilder(), reporter, _loggerFactory, childLauncher: launcher);
		var orchestration = SingleSyncChildStep(timeoutSeconds: 1);

		// Act
		var executeTask = executor.ExecuteAsync(orchestration);
		await launcher.Launched.Task.WaitAsync(TimeSpan.FromSeconds(5));
		await Task.Delay(TimeSpan.FromSeconds(1.6));

		// Assert — well past the 1s step timeout, the caller token must still be live.
		launcher.CapturedToken.IsCancellationRequested.Should().BeFalse(
			"the launcher owns the sync timeout for single-child sync Orchestration steps; the engine must not race it with a generic step CTS");
		launcher.CapturedRequest!.TimeoutSeconds.Should().Be(1, "the effective step timeout is handed to the launcher");
		launcher.CapturedRequest.Origin.Should().Be(ChildLaunchOrigin.OrchestrationStep);

		// Now let the "launcher" report its sync timeout and check the parent classifies it as one.
		launcher.TriggerSyncTimeout(1);
		var result = await executeTask.WaitAsync(TimeSpan.FromSeconds(10));

		result.Status.Should().Be(ExecutionStatus.Failed);
		var stepResult = result.StepResults["invoke-child"];
		stepResult.Status.Should().Be(ExecutionStatus.Failed);
		stepResult.ErrorCategory.Should().Be(StepErrorCategory.Timeout);
		stepResult.ErrorMessage.Should().Contain("timed out").And.Contain("child-orch");
		reporter.Received().ReportStepError("invoke-child", Arg.Is<string>(m => m.Contains("timed out")));
	}

	[Fact]
	public async Task SyncOrchestrationStep_WithoutOwnTimeout_UsesDefaultStepTimeout_ViaLauncher()
	{
		var launcher = new HangingChildLauncher();
		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new OrchestrationExecutor(_scheduler, new MockAgentBuilder(), reporter, _loggerFactory, childLauncher: launcher);
		var orchestration = SingleSyncChildStep(timeoutSeconds: null, defaultStepTimeoutSeconds: 1);

		var executeTask = executor.ExecuteAsync(orchestration);
		await launcher.Launched.Task.WaitAsync(TimeSpan.FromSeconds(5));
		await Task.Delay(TimeSpan.FromSeconds(1.6));

		launcher.CapturedToken.IsCancellationRequested.Should().BeFalse(
			"defaultStepTimeoutSeconds is delegated to the launcher exactly like an explicit step timeout");
		launcher.CapturedRequest!.TimeoutSeconds.Should().Be(1);

		launcher.TriggerSyncTimeout(1);
		var result = await executeTask.WaitAsync(TimeSpan.FromSeconds(10));
		result.StepResults["invoke-child"].ErrorCategory.Should().Be(StepErrorCategory.Timeout);
	}

	[Fact]
	public async Task ForEachSyncOrchestrationStep_KeepsGenericStepTimeout()
	{
		// forEach applies timeoutSeconds per child inside the launcher, while the engine's
		// generic step timeout still bounds the whole fan-out — so here the caller token DOES
		// fire after the step timeout.
		var launcher = new HangingChildLauncher();
		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new OrchestrationExecutor(_scheduler, new MockAgentBuilder(), reporter, _loggerFactory, childLauncher: launcher);
		var orchestration = ForEachSyncChildStep(timeoutSeconds: 1);

		var executeTask = executor.ExecuteAsync(orchestration);
		await launcher.Launched.Task.WaitAsync(TimeSpan.FromSeconds(5));
		await Task.Delay(TimeSpan.FromSeconds(1.6));

		launcher.CapturedToken.IsCancellationRequested.Should().BeTrue(
			"forEach fan-out keeps the engine's generic step timeout as the overall bound");

		await executeTask.WaitAsync(TimeSpan.FromSeconds(10));
	}

	[Fact]
	public async Task PreExecutionParameterTransform_ReportsInputHandlerPhase_BeforeFirstStep()
	{
		// The transform runs before any step starts and can take minutes for an LLM-backed
		// input handler; observers must get explicit start/complete events for the phase.
		var reporter = Substitute.For<IOrchestrationReporter>();
		var agentBuilder = new MockAgentBuilder().WithResponse("ok");
		var executor = new OrchestrationExecutor(_scheduler, agentBuilder, reporter, _loggerFactory);
		var orchestration = TestOrchestrations.SingleStep();

		var transformInvoked = 0;
		Func<CancellationToken, Task<Dictionary<string, string>?>> transform = _ =>
		{
			Interlocked.Increment(ref transformInvoked);
			return Task.FromResult<Dictionary<string, string>?>(new Dictionary<string, string>
			{
				["who"] = "transformed",
			});
		};

		var result = await executor.ExecuteAsync(
			orchestration,
			parameters: new Dictionary<string, string> { ["who"] = "raw" },
			preExecutionParameterTransform: transform);

		result.Status.Should().Be(ExecutionStatus.Succeeded);
		transformInvoked.Should().Be(1);

		Received.InOrder(() =>
		{
			reporter.ReportSessionInfo(OrchestrationExecutor.InputHandlerStartedInfoType, Arg.Any<string>());
			reporter.ReportSessionInfo(OrchestrationExecutor.InputHandlerCompletedInfoType, Arg.Is<string>(m => m.Contains("1 parameter(s)") && m.Contains("s.")));
			reporter.ReportStepStarted(Arg.Any<string>());
		});
	}

	[Fact]
	public async Task NoPreExecutionParameterTransform_EmitsNoInputHandlerEvents()
	{
		var reporter = Substitute.For<IOrchestrationReporter>();
		var executor = new OrchestrationExecutor(_scheduler, new MockAgentBuilder().WithResponse("ok"), reporter, _loggerFactory);

		var result = await executor.ExecuteAsync(TestOrchestrations.SingleStep());

		result.Status.Should().Be(ExecutionStatus.Succeeded);
		reporter.DidNotReceive().ReportSessionInfo(OrchestrationExecutor.InputHandlerStartedInfoType, Arg.Any<string>());
		reporter.DidNotReceive().ReportSessionInfo(OrchestrationExecutor.InputHandlerCompletedInfoType, Arg.Any<string>());
	}
}
