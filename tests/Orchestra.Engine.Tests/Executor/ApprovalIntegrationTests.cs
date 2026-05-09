using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Orchestra.Engine.Tests.TestHelpers;

namespace Orchestra.Engine.Tests.Executor;

/// <summary>
/// End-to-end tests for the Approval step type running through the full
/// <see cref="OrchestrationExecutor"/>. Verifies that the executor correctly registers
/// the ApprovalStepExecutor when an IHumanInputWaiter is supplied, that the wait
/// resolves through the response endpoint flow, and that downstream steps see the
/// resolved content via {{stepName.output}}.
/// </summary>
public class ApprovalIntegrationTests
{
	[Fact]
	public async Task ExecuteAsync_ApprovalThenTransform_PropagatesUserResponse()
	{
		var pendingStore = new InMemoryPendingInputStore();
		var waiter = new InMemoryWaiter();
		var orchestration = new Orchestration
		{
			Name = "approve-then-transform",
			Description = "Approval gate followed by a Transform that uses the response.",
			TimeoutSeconds = null, // No orchestration timeout
			Steps =
			[
				new ApprovalOrchestrationStep
				{
					Name = "review",
					Type = OrchestrationStepType.Approval,
					Prompt = "Approve deploy?",
					Choices = ["approve", "reject"],
				},
				new TransformOrchestrationStep
				{
					Name = "summarize",
					Type = OrchestrationStepType.Transform,
					DependsOn = ["review"],
					Template = "Decision: {{review.output}}",
				}
			],
		};

		var executor = new OrchestrationExecutor(
			scheduler: new OrchestrationScheduler(),
			agentBuilder: new MockAgentBuilder(),
			reporter: NullOrchestrationReporter.Instance,
			loggerFactory: NullLoggerFactory.Instance,
			pendingInputStore: pendingStore,
			humanInputWaiter: waiter);

		// Schedule the response after the wait registers (executor runs the step on a
		// LongRunning thread, so we have to coordinate via the waiter).
		_ = Task.Run(async () =>
		{
			await waiter.WaitForRegistration();
			waiter.Complete(new UserInputResponse
			{
				Choice = "approve",
				RespondedAt = DateTimeOffset.UtcNow,
			});
		});

		var result = await executor.ExecuteAsync(orchestration);

		result.Status.Should().Be(ExecutionStatus.Succeeded);
		result.StepResults["review"].Content.Should().Be("approve");
		result.StepResults["summarize"].Content.Should().Be("Decision: approve");
	}

	[Fact]
	public async Task ExecuteAsync_ApprovalTimeout_ProducesFailedStep()
	{
		var pendingStore = new InMemoryPendingInputStore();
		var waiter = new InMemoryWaiter();
		var orchestration = new Orchestration
		{
			Name = "approve-timeout",
			Description = "Approval that times out without a response.",
			TimeoutSeconds = null,
			Steps =
			[
				new ApprovalOrchestrationStep
				{
					Name = "review",
					Type = OrchestrationStepType.Approval,
					Prompt = "OK?",
					TimeoutSeconds = 1,
					OnTimeout = ApprovalTimeoutBehavior.Fail,
				}
			],
		};

		var executor = new OrchestrationExecutor(
			scheduler: new OrchestrationScheduler(),
			agentBuilder: new MockAgentBuilder(),
			reporter: NullOrchestrationReporter.Instance,
			loggerFactory: NullLoggerFactory.Instance,
			pendingInputStore: pendingStore,
			humanInputWaiter: waiter);

		// Don't respond — the per-step 1s timeout should fire and the OnTimeout=Fail
		// behavior should produce a Failed result with Timeout error category.
		var result = await executor.ExecuteAsync(orchestration);

		result.Status.Should().Be(ExecutionStatus.Failed);
		result.StepResults["review"].Status.Should().Be(ExecutionStatus.Failed);
		result.StepResults["review"].ErrorCategory.Should().Be(StepErrorCategory.Timeout);
	}

	[Fact]
	public async Task ExecuteAsync_ApprovalWithDefaultResponseTimeout_SucceedsWithFallback()
	{
		var pendingStore = new InMemoryPendingInputStore();
		var waiter = new InMemoryWaiter();
		var orchestration = new Orchestration
		{
			Name = "approve-default",
			Description = "Approval with a default fallback response on timeout.",
			TimeoutSeconds = null,
			Steps =
			[
				new ApprovalOrchestrationStep
				{
					Name = "review",
					Type = OrchestrationStepType.Approval,
					Prompt = "OK?",
					TimeoutSeconds = 1,
					OnTimeout = ApprovalTimeoutBehavior.DefaultResponse,
					DefaultResponse = "auto-rejected",
				}
			],
		};

		var executor = new OrchestrationExecutor(
			scheduler: new OrchestrationScheduler(),
			agentBuilder: new MockAgentBuilder(),
			reporter: NullOrchestrationReporter.Instance,
			loggerFactory: NullLoggerFactory.Instance,
			pendingInputStore: pendingStore,
			humanInputWaiter: waiter);

		var result = await executor.ExecuteAsync(orchestration);

		result.Status.Should().Be(ExecutionStatus.Succeeded);
		result.StepResults["review"].Status.Should().Be(ExecutionStatus.Succeeded);
		result.StepResults["review"].Content.Should().Be("auto-rejected");
	}

	[Fact]
	public async Task ExecuteAsync_ApprovalReply_OverridesChoiceAsContent()
	{
		var pendingStore = new InMemoryPendingInputStore();
		var waiter = new InMemoryWaiter();
		var orchestration = new Orchestration
		{
			Name = "reply-wins",
			Description = "Reply wins over choice.",
			TimeoutSeconds = null,
			Steps =
			[
				new ApprovalOrchestrationStep
				{
					Name = "review",
					Type = OrchestrationStepType.Approval,
					Prompt = "?",
				}
			],
		};

		var executor = new OrchestrationExecutor(
			scheduler: new OrchestrationScheduler(),
			agentBuilder: new MockAgentBuilder(),
			reporter: NullOrchestrationReporter.Instance,
			loggerFactory: NullLoggerFactory.Instance,
			pendingInputStore: pendingStore,
			humanInputWaiter: waiter);

		_ = Task.Run(async () =>
		{
			await waiter.WaitForRegistration();
			waiter.Complete(new UserInputResponse
			{
				Choice = "approve",
				Reply = "ship it but watch out",
				RespondedAt = DateTimeOffset.UtcNow,
			});
		});

		var result = await executor.ExecuteAsync(orchestration);

		result.StepResults["review"].Content.Should().Be("ship it but watch out");
	}

	private sealed class InMemoryWaiter : IHumanInputWaiter
	{
		private TaskCompletionSource<UserInputResponse>? _tcs;
		private readonly TaskCompletionSource _registered = new(TaskCreationOptions.RunContinuationsAsynchronously);

		public Task WaitForRegistration() => _registered.Task;

		public void Complete(UserInputResponse response) => _tcs?.TrySetResult(response);

		public Task<UserInputResponse> WaitAsync(string orchestrationName, string runId, string stepName, CancellationToken cancellationToken)
		{
			_tcs = new TaskCompletionSource<UserInputResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
			cancellationToken.Register(() => _tcs.TrySetCanceled(cancellationToken));
			_registered.TrySetResult();
			return _tcs.Task;
		}

		public bool TryComplete(string orchestrationName, string runId, string stepName, UserInputResponse response)
		{
			Complete(response);
			return true;
		}

		public bool TryCancel(string orchestrationName, string runId, string stepName) => false;
		public void BeginWait(string runId, string stepName) { }
		public void EndWait(string runId, string stepName) { }
	}

	private sealed class InMemoryPendingInputStore : IPendingInputStore
	{
		private readonly Dictionary<string, PendingInputRecord> _records = new();

		private static string Key(string o, string r, string s) => $"{o}|{r}|{s}";

		public Task SaveAsync(PendingInputRecord record, CancellationToken cancellationToken = default)
		{
			lock (_records) _records[Key(record.OrchestrationName, record.RunId, record.StepName)] = record;
			return Task.CompletedTask;
		}

		public Task<PendingInputRecord?> GetAsync(string orchestrationName, string runId, string stepName, CancellationToken cancellationToken = default)
		{
			lock (_records)
			{
				_records.TryGetValue(Key(orchestrationName, runId, stepName), out var record);
				return Task.FromResult(record);
			}
		}

		public Task<IReadOnlyList<PendingInputRecord>> ListAsync(string? orchestrationName = null, CancellationToken cancellationToken = default)
		{
			lock (_records)
			{
				IReadOnlyList<PendingInputRecord> all = orchestrationName is null
					? _records.Values.ToList()
					: _records.Values.Where(r => r.OrchestrationName == orchestrationName).ToList();
				return Task.FromResult(all);
			}
		}

		public Task DeleteAsync(string orchestrationName, string runId, string stepName, CancellationToken cancellationToken = default)
		{
			lock (_records) _records.Remove(Key(orchestrationName, runId, stepName));
			return Task.CompletedTask;
		}
	}
}
