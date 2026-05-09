using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Orchestra.Engine.Tests.Executor;

/// <summary>
/// Unit tests for <see cref="ApprovalStepExecutor"/> verifying the wait-then-resolve flow,
/// timeout behaviors, and pending-input record lifecycle.
/// </summary>
public class ApprovalStepExecutorTests
{
	[Fact]
	public async Task ExecuteAsync_ReturnsSucceededWithReply_WhenWaiterResolves()
	{
		var store = new TestPendingInputStore();
		var waiter = new TestHumanInputWaiter();
		var executor = new ApprovalStepExecutor(store, waiter, NullOrchestrationReporter.Instance, NullLogger<ApprovalStepExecutor>.Instance);
		var step = new ApprovalOrchestrationStep
		{
			Name = "review",
			Type = OrchestrationStepType.Approval,
			Prompt = "Approve deploy?",
			Choices = ["approve", "reject"],
		};
		var context = BuildContext("test-orch", "run-1");

		// Schedule the response after a short delay so the executor has time to register.
		_ = Task.Run(async () =>
		{
			await waiter.WaitForRegistration();
			waiter.Complete(new UserInputResponse { Choice = "approve", RespondedAt = DateTimeOffset.UtcNow });
		});

		var result = await executor.ExecuteAsync(step, context);

		result.Status.Should().Be(ExecutionStatus.Succeeded);
		result.Content.Should().Be("approve");
	}

	[Fact]
	public async Task ExecuteAsync_PrefersReplyOverChoice()
	{
		var store = new TestPendingInputStore();
		var waiter = new TestHumanInputWaiter();
		var executor = new ApprovalStepExecutor(store, waiter, NullOrchestrationReporter.Instance, NullLogger<ApprovalStepExecutor>.Instance);
		var step = new ApprovalOrchestrationStep
		{
			Name = "review",
			Type = OrchestrationStepType.Approval,
			Prompt = "Notes?",
		};
		var context = BuildContext("test", "r");

		_ = Task.Run(async () =>
		{
			await waiter.WaitForRegistration();
			waiter.Complete(new UserInputResponse
			{
				Choice = "approve",
				Reply = "go but with caveats",
				RespondedAt = DateTimeOffset.UtcNow,
			});
		});

		var result = await executor.ExecuteAsync(step, context);

		result.Content.Should().Be("go but with caveats");
	}

	[Fact]
	public async Task ExecuteAsync_PersistsRecord_WithApprovalKind()
	{
		var store = new TestPendingInputStore();
		var waiter = new TestHumanInputWaiter();
		var executor = new ApprovalStepExecutor(store, waiter, NullOrchestrationReporter.Instance, NullLogger<ApprovalStepExecutor>.Instance);
		var step = new ApprovalOrchestrationStep
		{
			Name = "review",
			Type = OrchestrationStepType.Approval,
			Prompt = "OK?",
		};

		_ = Task.Run(async () =>
		{
			await waiter.WaitForRegistration();
			store.GetAll().Should().ContainSingle()
				.Which.Kind.Should().Be(PendingInputKind.Approval);
			waiter.Complete(new UserInputResponse { Reply = "yes", RespondedAt = DateTimeOffset.UtcNow });
		});

		await executor.ExecuteAsync(step, BuildContext("orch", "r1"));

		// Cleaned up after.
		store.GetAll().Should().BeEmpty();
	}

	[Fact]
	public async Task ExecuteAsync_TimeoutFail_ReturnsFailedWithTimeoutCategory()
	{
		var store = new TestPendingInputStore();
		var waiter = new TestHumanInputWaiter();
		var executor = new ApprovalStepExecutor(store, waiter, NullOrchestrationReporter.Instance, NullLogger<ApprovalStepExecutor>.Instance);
		var step = new ApprovalOrchestrationStep
		{
			Name = "review",
			Type = OrchestrationStepType.Approval,
			Prompt = "OK?",
			TimeoutSeconds = 1,
			OnTimeout = ApprovalTimeoutBehavior.Fail,
		};
		using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

		var result = await executor.ExecuteAsync(step, BuildContext("orch", "r2"), cts.Token);

		result.Status.Should().Be(ExecutionStatus.Failed);
		result.ErrorCategory.Should().Be(StepErrorCategory.Timeout);
	}

	[Fact]
	public async Task ExecuteAsync_TimeoutDefaultResponse_ReturnsSucceededWithDefault()
	{
		var store = new TestPendingInputStore();
		var waiter = new TestHumanInputWaiter();
		var executor = new ApprovalStepExecutor(store, waiter, NullOrchestrationReporter.Instance, NullLogger<ApprovalStepExecutor>.Instance);
		var step = new ApprovalOrchestrationStep
		{
			Name = "review",
			Type = OrchestrationStepType.Approval,
			Prompt = "OK?",
			OnTimeout = ApprovalTimeoutBehavior.DefaultResponse,
			DefaultResponse = "rejected-by-default",
		};
		using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

		var result = await executor.ExecuteAsync(step, BuildContext("orch", "r3"), cts.Token);

		result.Status.Should().Be(ExecutionStatus.Succeeded);
		result.Content.Should().Be("rejected-by-default");
	}

	[Fact]
	public async Task ExecuteAsync_TimeoutCancel_RethrowsCancellation()
	{
		var store = new TestPendingInputStore();
		var waiter = new TestHumanInputWaiter();
		var executor = new ApprovalStepExecutor(store, waiter, NullOrchestrationReporter.Instance, NullLogger<ApprovalStepExecutor>.Instance);
		var step = new ApprovalOrchestrationStep
		{
			Name = "review",
			Type = OrchestrationStepType.Approval,
			Prompt = "OK?",
			OnTimeout = ApprovalTimeoutBehavior.Cancel,
		};
		using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

		var act = async () => await executor.ExecuteAsync(step, BuildContext("orch", "r4"), cts.Token);

		await act.Should().ThrowAsync<OperationCanceledException>();
	}

	[Fact]
	public async Task ExecuteAsync_FiresOnAwaitingInputCallback()
	{
		var store = new TestPendingInputStore();
		var waiter = new TestHumanInputWaiter();
		var executor = new ApprovalStepExecutor(store, waiter, NullOrchestrationReporter.Instance, NullLogger<ApprovalStepExecutor>.Instance);
		var step = new ApprovalOrchestrationStep
		{
			Name = "review",
			Type = OrchestrationStepType.Approval,
			Prompt = "OK?",
		};
		PendingInputRecord? captured = null;
		var context = BuildContext("orch", "r5");
		context = new OrchestrationExecutionContext
		{
			OrchestrationInfo = context.OrchestrationInfo,
			OnAwaitingInput = r => captured = r,
		};

		_ = Task.Run(async () =>
		{
			await waiter.WaitForRegistration();
			waiter.Complete(new UserInputResponse { Reply = "ok", RespondedAt = DateTimeOffset.UtcNow });
		});

		await executor.ExecuteAsync(step, context);

		captured.Should().NotBeNull();
		captured!.Kind.Should().Be(PendingInputKind.Approval);
	}

	[Fact]
	public async Task ExecuteAsync_ResolvesTemplateExpressions_InPrompt()
	{
		var store = new TestPendingInputStore();
		var waiter = new TestHumanInputWaiter();
		var executor = new ApprovalStepExecutor(store, waiter, NullOrchestrationReporter.Instance, NullLogger<ApprovalStepExecutor>.Instance);
		var step = new ApprovalOrchestrationStep
		{
			Name = "review",
			Type = OrchestrationStepType.Approval,
			Prompt = "Approve deploy of {{param.service}} to {{param.env}}?",
			Parameters = ["service", "env"],
		};
		var context = new OrchestrationExecutionContext
		{
			Parameters = new Dictionary<string, string>
			{
				["service"] = "api",
				["env"] = "prod",
			},
			OrchestrationInfo = new OrchestrationInfo("test", "1.0.0", "r-tmpl", DateTimeOffset.UtcNow, null, null),
		};

		_ = Task.Run(async () =>
		{
			await waiter.WaitForRegistration();
			var saved = store.GetAll().Single();
			saved.Prompt.Should().Be("Approve deploy of api to prod?");
			waiter.Complete(new UserInputResponse { Reply = "yes", RespondedAt = DateTimeOffset.UtcNow });
		});

		await executor.ExecuteAsync(step, context);
	}

	private static OrchestrationExecutionContext BuildContext(string orchestrationName, string runId)
	{
		return new OrchestrationExecutionContext
		{
			OrchestrationInfo = new OrchestrationInfo(orchestrationName, "1.0.0", runId, DateTimeOffset.UtcNow, null, null),
		};
	}

	private sealed class TestHumanInputWaiter : IHumanInputWaiter
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

	private sealed class TestPendingInputStore : IPendingInputStore
	{
		private readonly Dictionary<string, PendingInputRecord> _records = new();

		public IReadOnlyList<PendingInputRecord> GetAll() => _records.Values.ToList();

		private static string Key(string o, string r, string s) => $"{o}|{r}|{s}";

		public Task SaveAsync(PendingInputRecord record, CancellationToken cancellationToken = default)
		{
			_records[Key(record.OrchestrationName, record.RunId, record.StepName)] = record;
			return Task.CompletedTask;
		}

		public Task<PendingInputRecord?> GetAsync(string orchestrationName, string runId, string stepName, CancellationToken cancellationToken = default)
		{
			_records.TryGetValue(Key(orchestrationName, runId, stepName), out var record);
			return Task.FromResult(record);
		}

		public Task<IReadOnlyList<PendingInputRecord>> ListAsync(string? orchestrationName = null, CancellationToken cancellationToken = default)
		{
			IReadOnlyList<PendingInputRecord> all = orchestrationName is null
				? _records.Values.ToList()
				: _records.Values.Where(r => r.OrchestrationName == orchestrationName).ToList();
			return Task.FromResult(all);
		}

		public Task DeleteAsync(string orchestrationName, string runId, string stepName, CancellationToken cancellationToken = default)
		{
			_records.Remove(Key(orchestrationName, runId, stepName));
			return Task.CompletedTask;
		}
	}
}
