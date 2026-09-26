using System.Threading.Channels;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orchestra.Engine.Tests.TestHelpers;

namespace Orchestra.Engine.Tests.Executor;

/// <summary>
/// End-to-end fidelity of <see cref="ExecutionResult"/> through <see cref="OrchestrationExecutor"/>.
/// </summary>
/// <remarks>
/// The executor used to rebuild results by hand-copying fields (trace enrichment, retry history,
/// timeout conversion, checkpoint rehydration) and every copy site silently dropped a different
/// subset of properties. In practice <see cref="ExecutionResult.ChildOrchestrationInfo"/> and
/// <see cref="ExecutionResult.ErrorDetails"/> never survived a real run, so
/// <c>{{child.executionId}}</c> templates and the SDK error details in <c>run.json</c> were
/// always empty. <see cref="ExecutionResult"/> is now a <c>record</c> and derived results are
/// built with <c>with</c>; these tests pin that behaviour at the executor boundary.
/// </remarks>
public class ExecutionResultFidelityTests
{
	private readonly IScheduler _scheduler = new OrchestrationScheduler();
	private readonly IOrchestrationReporter _reporter = SerializingReporterProxy.CreateRecording();

	private static (IRunStore Store, Func<OrchestrationRunRecord?> Captured) CapturingRunStore()
	{
		OrchestrationRunRecord? captured = null;
		var store = Substitute.For<IRunStore>();
		store.SaveRunAsync(Arg.Do<OrchestrationRunRecord>(r => captured = r), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
		return (store, () => captured);
	}

	/// <summary>Launcher whose child completes immediately with one step result.</summary>
	private sealed class CompletedChildLauncher : IChildOrchestrationLauncher
	{
		public const string ChildExecutionId = "child-exec-42";

		public Task<ChildOrchestrationHandle> LaunchAsync(ChildLaunchRequest request, CancellationToken cancellationToken = default)
		{
			var childStep = ExecutionResult.Succeeded("child says hi");
			var completion = Task.FromResult(new ChildOrchestrationResult
			{
				ExecutionId = ChildExecutionId,
				OrchestrationId = request.OrchestrationId,
				OrchestrationName = request.OrchestrationId,
				Status = ExecutionStatus.Succeeded,
				FinalContent = "child says hi",
				OrchestrationResult = new OrchestrationResult
				{
					Status = ExecutionStatus.Succeeded,
					Results = new Dictionary<string, ExecutionResult> { ["greet"] = childStep },
					StepResults = new Dictionary<string, ExecutionResult> { ["greet"] = childStep },
				},
				StartedAt = DateTimeOffset.UtcNow,
				CompletedAt = DateTimeOffset.UtcNow,
			});

			return Task.FromResult(new ChildOrchestrationHandle
			{
				ExecutionId = ChildExecutionId,
				OrchestrationId = request.OrchestrationId,
				OrchestrationName = request.OrchestrationId,
				Reporter = NullOrchestrationReporter.Instance,
				StartedAt = DateTimeOffset.UtcNow,
				Completion = completion,
			});
		}
	}

	[Fact]
	public async Task ChildOrchestrationInfo_SurvivesTheRun_SoTemplatesAndRunRecordsSeeIt()
	{
		// Arrange — a sync Orchestration step followed by a Prompt step that drills into the
		// child via templates. Before the fix, trace enrichment dropped ChildOrchestrationInfo,
		// so the template resolver saw a result with no child data.
		var (runStore, captured) = CapturingRunStore();
		string? promptSeen = null;
		var agent = new MockAgentBuilder().WithHandler((prompt, ct) =>
		{
			promptSeen = prompt;
			var channel = Channel.CreateUnbounded<AgentEvent>();
			channel.Writer.Complete();
			return new AgentTask(channel.Reader, Task.FromResult(new AgentResult { Content = "done" }));
		});

		var executor = new OrchestrationExecutor(_scheduler, agent, _reporter, NullLoggerFactory.Instance,
			runStore: runStore, childLauncher: new CompletedChildLauncher());

		var orchestration = new Orchestration
		{
			Name = "parent",
			Description = "drills into the child",
			Steps =
			[
				new OrchestrationInvocationStep
				{
					Name = "child",
					Type = OrchestrationStepType.Orchestration,
					OrchestrationName = "child-orch",
					Mode = OrchestrationInvocationMode.Sync,
				},
				TestOrchestrations.CreatePromptStep("report", dependsOn: ["child"],
					userPrompt: "id={{child.executionId}} greet={{child.steps.greet.output}}"),
			],
		};

		// Act
		var result = await executor.ExecuteAsync(orchestration).WaitAsync(TimeSpan.FromSeconds(20));

		// Assert — the in-memory result, the template input and the persisted record all agree.
		result.Status.Should().Be(ExecutionStatus.Succeeded);
		result.StepResults["child"].ChildOrchestrationInfo.Should().NotBeNull();
		result.StepResults["child"].ChildOrchestrationInfo!.ExecutionId.Should().Be(CompletedChildLauncher.ChildExecutionId);

		promptSeen.Should().NotBeNull();
		promptSeen.Should().Contain($"id={CompletedChildLauncher.ChildExecutionId}");
		promptSeen.Should().Contain("greet=child says hi");

		var record = captured();
		record.Should().NotBeNull();
		record!.AllStepRecords["child"].ChildExecutionId.Should().Be(CompletedChildLauncher.ChildExecutionId);
		record.AllStepRecords["child"].ChildStatus.Should().Be(ExecutionStatus.Succeeded);
	}

	[Fact]
	public async Task ErrorDetails_SurviveTheRun_AndArePersistedOnTheStepRecord()
	{
		// Arrange — the agent fails with structured SDK details. Before the fix, trace enrichment
		// rebuilt the result without ErrorDetails, so run.json never carried them.
		var (runStore, captured) = CapturingRunStore();
		var details = new AgentSessionErrorDetails
		{
			ErrorType = "query",
			StatusCode = 502,
			ProviderCallId = "call-123",
		};
		var agent = new MockAgentBuilder().WithException(new SessionFailedException("upstream 502", details));
		var executor = new OrchestrationExecutor(_scheduler, agent, _reporter, NullLoggerFactory.Instance, runStore: runStore);

		var orchestration = new Orchestration
		{
			Name = "failing",
			Description = "one failing prompt step",
			Steps = [TestOrchestrations.CreatePromptStep("ask")],
		};

		// Act
		var result = await executor.ExecuteAsync(orchestration).WaitAsync(TimeSpan.FromSeconds(20));

		// Assert
		result.Status.Should().Be(ExecutionStatus.Failed);
		result.StepResults["ask"].ErrorDetails.Should().NotBeNull();
		result.StepResults["ask"].ErrorDetails!.StatusCode.Should().Be(502);

		var record = captured();
		record.Should().NotBeNull();
		record!.AllStepRecords["ask"].ErrorDetails.Should().NotBeNull();
		record.AllStepRecords["ask"].ErrorDetails!.ProviderCallId.Should().Be("call-123");
	}

	[Fact]
	public async Task ErrorDetails_SurviveRetryExhaustion()
	{
		// Arrange — retries wrap the final failed result to attach RetryHistory; that copy used to
		// drop ErrorDetails and the model-info fields.
		var (runStore, captured) = CapturingRunStore();
		var details = new AgentSessionErrorDetails { ErrorType = "query", StatusCode = 503 };
		var agent = new MockAgentBuilder().WithException(new SessionFailedException("still failing", details));
		var executor = new OrchestrationExecutor(_scheduler, agent, _reporter, NullLoggerFactory.Instance, runStore: runStore);

		var step = new PromptOrchestrationStep
		{
			Name = "ask",
			Type = OrchestrationStepType.Prompt,
			SystemPrompt = "You are a test assistant.",
			UserPrompt = "ask",
			Model = "claude-opus-4.8",
			Retry = new RetryPolicy { MaxRetries = 1, BackoffSeconds = 0 },
		};
		var orchestration = new Orchestration
		{
			Name = "retrying",
			Description = "fails after one retry",
			Steps = [step],
		};

		// Act
		var result = await executor.ExecuteAsync(orchestration).WaitAsync(TimeSpan.FromSeconds(20));

		// Assert
		var ask = result.StepResults["ask"];
		ask.Status.Should().Be(ExecutionStatus.Failed);
		ask.RetryHistory.Should().NotBeNull().And.HaveCount(1);
		ask.ErrorDetails.Should().NotBeNull("retry bookkeeping must not strip the structured SDK error");
		ask.ErrorDetails!.StatusCode.Should().Be(503);
		captured()!.AllStepRecords["ask"].ErrorDetails.Should().NotBeNull();
	}

	[Fact]
	public void CheckpointStepResult_RoundTripsSkipKindAndErrorDetails()
	{
		// A step skipped by its own `when:` must still not block dependents after a resume, so
		// SkipKind has to survive the checkpoint; ErrorDetails likewise for failed steps.
		var conditionSkip = ExecutionResult.SkippedByCondition("{{flag}} == 'yes'");
		var failed = ExecutionResult.Failed("boom", errorDetails: new AgentSessionErrorDetails { StatusCode = 500 });

		var restoredSkip = CheckpointStepResult.FromExecutionResult(conditionSkip).ToExecutionResult();
		var restoredFailed = CheckpointStepResult.FromExecutionResult(failed).ToExecutionResult();

		restoredSkip.SkipKind.Should().Be(SkipKind.Condition);
		restoredSkip.BlocksDependents.Should().BeFalse();
		restoredFailed.ErrorDetails.Should().NotBeNull();
		restoredFailed.ErrorDetails!.StatusCode.Should().Be(500);
	}

	[Fact]
	public void WithExpression_CopiesEveryProperty()
	{
		// Guard against a future property being added and forgotten by a hand-copy: with a record
		// there is nothing to forget, but this pins the contract that derived results are complete.
		var original = ExecutionResult.Failed(
			"boom",
			rawDependencyOutputs: new Dictionary<string, string> { ["dep"] = "x" },
			promptSent: "prompt",
			actualModel: "m1",
			errorCategory: StepErrorCategory.ModelError,
			retryHistory: [new RetryAttemptRecord { Attempt = 1, Error = "e", AttemptedAt = DateTimeOffset.UtcNow, DelaySeconds = 0 }],
			selectedModel: "m1-selected",
			savedFiles: ["a.txt"],
			childOrchestrationInfo: new ChildOrchestrationInfo { ExecutionId = "c", OrchestrationName = "child", Status = ExecutionStatus.Failed },
			errorDetails: new AgentSessionErrorDetails { StatusCode = 500 });

		var derived = original with { Status = ExecutionStatus.Cancelled };

		derived.Should().BeEquivalentTo(original, o => o.Excluding(r => r.Status));
		derived.Status.Should().Be(ExecutionStatus.Cancelled);
	}

	private sealed class SessionFailedException(string message, AgentSessionErrorDetails? details) : Exception(message), IAgentSessionFailedException
	{
		public AgentSessionErrorDetails? Details { get; } = details;
	}
}
