using Microsoft.Extensions.Logging;

namespace Orchestra.Engine;

/// <summary>
/// Executes <see cref="ApprovalOrchestrationStep"/> by registering a wait for human input.
/// Persists a <see cref="PendingInputRecord"/>, emits the <c>step.awaitingInput</c> hook
/// payload (via the executor that calls us), then awaits the user's response.
/// <para>
/// The step's effective <see cref="ExecutionStatus"/> while waiting is
/// <see cref="ExecutionStatus.AwaitingInput"/>; on response it becomes
/// <see cref="ExecutionStatus.Succeeded"/> with the resolved content. On timeout the
/// behavior is governed by <see cref="ApprovalOrchestrationStep.OnTimeout"/>.
/// </para>
/// </summary>
public sealed partial class ApprovalStepExecutor : IStepExecutor
{
	private readonly IPendingInputStore _pendingInputStore;
	private readonly IHumanInputWaiter _waiter;
	private readonly IOrchestrationReporter _reporter;
	private readonly ILogger<ApprovalStepExecutor> _logger;

	public ApprovalStepExecutor(
		IPendingInputStore pendingInputStore,
		IHumanInputWaiter waiter,
		IOrchestrationReporter reporter,
		ILogger<ApprovalStepExecutor> logger)
	{
		_pendingInputStore = pendingInputStore;
		_waiter = waiter;
		_reporter = reporter;
		_logger = logger;
	}

	public OrchestrationStepType StepType => OrchestrationStepType.Approval;

	public async Task<ExecutionResult> ExecuteAsync(
		OrchestrationStep step,
		OrchestrationExecutionContext context,
		CancellationToken cancellationToken = default)
	{
		if (step is not ApprovalOrchestrationStep approval)
			throw new InvalidOperationException(
				$"ApprovalStepExecutor received a step of type '{step.GetType().Name}' but expected '{nameof(ApprovalOrchestrationStep)}'.");

		var runId = context.OrchestrationInfo.RunId;
		var orchestrationName = context.OrchestrationInfo.Name;

		// Resolve template expressions in the prompt for runtime values like {{param.x}} or {{vars.x}}.
		var resolvedPrompt = TemplateResolver.Resolve(
			approval.Prompt,
			context.Parameters,
			context,
			step.DependsOn,
			step);

		var rawDependencyOutputs = context.GetRawDependencyOutputs(step.DependsOn);

		var record = new PendingInputRecord
		{
			OrchestrationName = orchestrationName,
			RunId = runId,
			StepName = step.Name,
			Kind = PendingInputKind.Approval,
			Prompt = resolvedPrompt,
			Choices = approval.Choices,
			CreatedAt = DateTimeOffset.UtcNow,
			ExpiresAt = approval.TimeoutSeconds is > 0
				? DateTimeOffset.UtcNow.AddSeconds(approval.TimeoutSeconds.Value)
				: null,
		};

		// If a previous attempt for this same step already persisted a record (e.g., the
		// host restarted and we're resuming), reuse it so the routing key stays stable.
		var existing = await _pendingInputStore.GetAsync(orchestrationName, runId, step.Name, cancellationToken).ConfigureAwait(false);
		if (existing is null)
		{
			await _pendingInputStore.SaveAsync(record, cancellationToken).ConfigureAwait(false);
		}
		else
		{
			record = existing;
		}

		LogApprovalAwaiting(step.Name, orchestrationName, runId, approval.Choices.Length);
		_reporter.ReportAwaitingInput(record);
		context.OnAwaitingInput?.Invoke(record);

		_waiter.BeginWait(runId, step.Name);
		var endedClock = false;
		var shouldCleanupRecord = false;
		try
		{
			var response = await _waiter.WaitAsync(orchestrationName, runId, step.Name, cancellationToken).ConfigureAwait(false);
			LogApprovalReceived(step.Name, orchestrationName, runId, response.RespondedBy ?? "(unknown)");
			context.OnInputResolved?.Invoke(runId, step.Name);
			endedClock = true;
			shouldCleanupRecord = true;
			_reporter.ReportInputReceived(orchestrationName, runId, step.Name, response);

			var content = response.ResolveContent();
			return ExecutionResult.Succeeded(content, rawDependencyOutputs: ToMutable(rawDependencyOutputs));
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			// Timeout / external cancellation. Apply the configured onTimeout behavior.
			LogApprovalCancelled(step.Name, orchestrationName, runId, approval.OnTimeout.ToString());
			context.OnInputResolved?.Invoke(runId, step.Name);
			endedClock = true;

			// We can't tell here if the cancellation was a host shutdown vs a step timeout
			// vs an external cancel — that's resolved later by OrchestrationExecutor. To
			// preserve the record across host restarts (for resume), we only clean up when
			// the cancellation matches an explicit user-requested timeout behavior that
			// produces a terminal step result (DefaultResponse / Cancel re-throw).
			shouldCleanupRecord = approval.OnTimeout != ApprovalTimeoutBehavior.Fail || approval.TimeoutSeconds is > 0;

			switch (approval.OnTimeout)
			{
				case ApprovalTimeoutBehavior.DefaultResponse when !string.IsNullOrEmpty(approval.DefaultResponse):
					_reporter.ReportInputTimeout(orchestrationName, runId, step.Name, approval.OnTimeout);
					return ExecutionResult.Succeeded(approval.DefaultResponse!, rawDependencyOutputs: ToMutable(rawDependencyOutputs));

				case ApprovalTimeoutBehavior.Cancel:
					_reporter.ReportInputTimeout(orchestrationName, runId, step.Name, approval.OnTimeout);
					// Re-throw so the executor's standard timeout/cancellation handling fires
					// — in particular, we want the run to be cancelled, not just the step.
					throw;

				default:
					_reporter.ReportInputTimeout(orchestrationName, runId, step.Name, approval.OnTimeout);
					var timeoutMsg = approval.TimeoutSeconds is > 0
						? $"Approval step timed out after {approval.TimeoutSeconds}s without a response."
						: "Approval step was cancelled while awaiting input.";
					return ExecutionResult.Failed(
						timeoutMsg,
						rawDependencyOutputs: ToMutable(rawDependencyOutputs),
						errorCategory: StepErrorCategory.Timeout);
			}
		}
		finally
		{
			_waiter.EndWait(runId, step.Name);
			if (!endedClock)
			{
				context.OnInputResolved?.Invoke(runId, step.Name);
			}

			// Only delete the persisted record when we have a definitive outcome.
			// On host-shutdown cancellation we skip cleanup so the record survives for resume.
			if (shouldCleanupRecord)
			{
				try
				{
					await _pendingInputStore.DeleteAsync(orchestrationName, runId, step.Name, CancellationToken.None).ConfigureAwait(false);
				}
				catch (Exception ex)
				{
					LogApprovalCleanupFailed(step.Name, ex);
				}
			}
		}
	}

	private static Dictionary<string, string> ToMutable(IReadOnlyDictionary<string, string> source)
	{
		return source is Dictionary<string, string> dict
			? dict
			: new Dictionary<string, string>(source);
	}

	[LoggerMessage(EventId = 1, Level = LogLevel.Information,
		Message = "Approval step '{StepName}' (orchestration '{OrchestrationName}', run '{RunId}') awaiting input ({ChoiceCount} choice(s) declared).")]
	private partial void LogApprovalAwaiting(string stepName, string orchestrationName, string runId, int choiceCount);

	[LoggerMessage(EventId = 2, Level = LogLevel.Information,
		Message = "Approval step '{StepName}' (orchestration '{OrchestrationName}', run '{RunId}') received response from {RespondedBy}.")]
	private partial void LogApprovalReceived(string stepName, string orchestrationName, string runId, string respondedBy);

	[LoggerMessage(EventId = 3, Level = LogLevel.Warning,
		Message = "Approval step '{StepName}' (orchestration '{OrchestrationName}', run '{RunId}') wait cancelled — applying onTimeout behavior '{OnTimeout}'.")]
	private partial void LogApprovalCancelled(string stepName, string orchestrationName, string runId, string onTimeout);

	[LoggerMessage(EventId = 4, Level = LogLevel.Warning,
		Message = "Approval step '{StepName}' failed to clean up its pending-input record.")]
	private partial void LogApprovalCleanupFailed(string stepName, Exception ex);
}
