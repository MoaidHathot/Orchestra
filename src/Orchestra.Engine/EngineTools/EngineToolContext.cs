namespace Orchestra.Engine;

/// <summary>
/// Shared mutable context passed to engine tools during a prompt step execution.
/// Engine tools record side effects here, which the executor inspects after completion.
/// </summary>
public sealed class EngineToolContext
{
	/// <summary>
	/// The temp file store for the current orchestration run.
	/// Provides file I/O operations scoped to a run-specific temp directory.
	/// May be null when no data path is configured (e.g., in-memory mode).
	/// </summary>
	public OrchestrationTempFileStore? TempFileStore { get; init; }

	/// <summary>
	/// The reporter for emitting live events (e.g., step-status-set).
	/// May be null when no reporter is configured.
	/// </summary>
	public IOrchestrationReporter? Reporter { get; init; }

	/// <summary>
	/// The name of the step this context belongs to.
	/// Used by engine tools to register artifacts (e.g., saved files) against the correct step.
	/// </summary>
	public string? StepName { get; init; }

	/// <summary>
	/// The orchestration name that owns this run. Required by HITL engine tools so they
	/// can write a <see cref="PendingInputRecord"/> with the correct routing key.
	/// May be null when the engine tool registry is invoked outside a run (e.g. tests).
	/// </summary>
	public string? OrchestrationName { get; init; }

	/// <summary>
	/// The unique identifier for the current run. Required by HITL engine tools.
	/// </summary>
	public string? RunId { get; init; }

	/// <summary>
	/// The waiter used to block on a human-in-the-loop response. Wired up by the host.
	/// When null, HITL engine tools fall back to <see cref="NullHumanInputWaiter.Instance"/>
	/// which blocks until cancellation (no-op safe).
	/// </summary>
	public IHumanInputWaiter? HumanInputWaiter { get; init; }

	/// <summary>
	/// The store used to persist <see cref="PendingInputRecord"/> entries so the response
	/// endpoint can route a reply back to the right run/step. When null, HITL engine tools
	/// still register an in-memory wait but the record is not durable.
	/// </summary>
	public IPendingInputStore? PendingInputStore { get; init; }

	/// <summary>
	/// Optional builder that produces the public URL the user can POST a response to.
	/// Surfaced through the <c>step.awaitingInput</c> hook payload as <c>respondUrl</c>.
	/// May be null when no host URL is configured.
	/// </summary>
	public Func<string, string, string, string?>? RespondUrlBuilder { get; init; }

	/// <summary>
	/// Set of engine tool names this step has explicitly opted in to (e.g.,
	/// <c>request_user_input</c>). Always-on tools are not listed here.
	/// </summary>
	public IReadOnlyCollection<string>? EnabledOptInTools { get; init; }

	/// <summary>
	/// Notifies the HITL hook system that a step has begun awaiting input. Wired by the
	/// executor; null when no hooks are configured. Engine tools call this before
	/// awaiting on the waiter so notifications fire promptly.
	/// </summary>
	public Action<PendingInputRecord>? OnAwaitingInput { get; init; }

	/// <summary>
	/// Notifies the executor that the wait has resolved (response received, timeout, or
	/// cancellation). Wired by the executor to drive clock-pause accounting. Null when
	/// clock-pause is disabled.
	/// </summary>
	public Action<string, string>? OnInputResolved { get; init; }

	/// <summary>
	/// When set, the prompt step result will be overridden to the specified status
	/// regardless of the LLM's output content.
	/// </summary>
	public ExecutionStatus? StatusOverride { get; private set; }

	/// <summary>
	/// The reason provided by the LLM when signaling the execution status via the set_status tool.
	/// </summary>
	public string? StatusReason { get; private set; }

	/// <summary>
	/// Whether the status has been explicitly set by an engine tool.
	/// </summary>
	public bool HasStatusOverride => StatusOverride is not null;

	/// <summary>
	/// When set, signals that the entire orchestration should complete immediately.
	/// All pending and running steps will be cancelled.
	/// </summary>
	public bool OrchestrationCompleteRequested { get; private set; }

	/// <summary>
	/// The status to use for the orchestration completion (success or failed).
	/// </summary>
	public ExecutionStatus? OrchestrationCompleteStatus { get; private set; }

	/// <summary>
	/// The reason for orchestration completion.
	/// </summary>
	public string? OrchestrationCompleteReason { get; private set; }

	/// <summary>
	/// Whether an engine tool has signaled that the step should stop immediately
	/// (e.g., after calling <see cref="SetStatusTool"/> with a terminal status).
	/// </summary>
	public bool StepCompletionRequested { get; private set; }

	/// <summary>
	/// Cancellation token source that the executor sets before running the agent.
	/// When <see cref="RequestStepCompletion"/> is called, this is cancelled to
	/// interrupt the agent session so the status override takes effect immediately.
	/// </summary>
	internal CancellationTokenSource? StepCompletionCts { get; set; }

	/// <summary>
	/// Sets the execution status override. The FIRST terminal status wins — once any
	/// terminal value (<see cref="ExecutionStatus.Succeeded"/>, <see cref="ExecutionStatus.Failed"/>,
	/// or <see cref="ExecutionStatus.NoAction"/>) has been recorded, subsequent calls are
	/// silently ignored. This guards against the LLM declaring success and then later
	/// declaring failure (or vice versa) inside the same step — for example after a CLI
	/// swap re-runs the prompt and the model on the fresh worker reaches a different
	/// conclusion than the one that already terminated the step.
	/// </summary>
	public void SetStatus(ExecutionStatus status, string? reason = null)
	{
		// First terminal status wins. Cancelled is not a terminal-by-engine-tool state
		// (it can only be entered by the engine itself) so it's not considered here.
		if (StatusOverride is ExecutionStatus.Succeeded
			or ExecutionStatus.Failed
			or ExecutionStatus.NoAction)
		{
			return;
		}

		StatusOverride = status;
		StatusReason = reason;
	}

	/// <summary>
	/// Signals that the current step should complete immediately. The agent session
	/// will be cancelled and the executor will use the <see cref="StatusOverride"/>
	/// to determine the step result.
	/// </summary>
	public void RequestStepCompletion()
	{
		StepCompletionRequested = true;
		try
		{
			StepCompletionCts?.Cancel();
		}
		catch (ObjectDisposedException)
		{
			// CTS may already be disposed if the agent completed naturally
		}
	}

	/// <summary>
	/// Signals that the entire orchestration should complete immediately.
	/// The orchestration will cancel all pending/running steps and finish.
	/// </summary>
	public void CompleteOrchestration(ExecutionStatus status, string? reason = null)
	{
		OrchestrationCompleteRequested = true;
		OrchestrationCompleteStatus = status;
		OrchestrationCompleteReason = reason;
	}

	/// <summary>
	/// Shared human-in-the-loop primitive: persists a <see cref="PendingInputRecord"/>,
	/// fires the awaiting-input notifications, then blocks until the host completes the
	/// matching wait (via the HumanInput API / MCP). Returns the operator's
	/// <see cref="UserInputResponse"/>, or <c>null</c> when this context has no run
	/// identity (e.g. an internal sub-call). Cancellation propagates to the caller.
	/// <para>
	/// Used by both the LLM-decided <c>orchestra_request_user_input</c> engine tool and the
	/// Copilot SDK elicitation / exit-plan-mode handlers so all HITL pause paths share one
	/// persist → notify → wait → resolve → cleanup implementation. The waiter supports a
	/// single outstanding wait per (orchestration, run, step); concurrent waits on the same
	/// step are not supported.
	/// </para>
	/// </summary>
	public async Task<UserInputResponse?> RequestHumanInputAsync(
		string prompt,
		string[]? choices,
		PendingInputKind kind,
		CancellationToken cancellationToken)
	{
		var stepName = StepName;
		var orchestrationName = OrchestrationName;
		var runId = RunId;

		if (string.IsNullOrEmpty(stepName) || string.IsNullOrEmpty(orchestrationName) || string.IsNullOrEmpty(runId))
			return null;

		var waiter = HumanInputWaiter ?? NullHumanInputWaiter.Instance;

		// Persist BEFORE registering the in-memory wait so a host crash between persist and
		// wait still leaves a discoverable record the response endpoint can route to.
		var record = new PendingInputRecord
		{
			OrchestrationName = orchestrationName,
			RunId = runId,
			StepName = stepName,
			Kind = kind,
			Prompt = prompt,
			Choices = choices ?? [],
			CreatedAt = DateTimeOffset.UtcNow,
			ExpiresAt = null,
		};

		if (PendingInputStore is { } store)
		{
			await store.SaveAsync(record, cancellationToken).ConfigureAwait(false);
		}

		OnAwaitingInput?.Invoke(record);
		Reporter?.ReportAwaitingInput(record);

		waiter.BeginWait(runId, stepName);
		var resolved = false;
		try
		{
			var response = await waiter.WaitAsync(orchestrationName, runId, stepName, cancellationToken).ConfigureAwait(false);
			OnInputResolved?.Invoke(runId, stepName);
			resolved = true;
			Reporter?.ReportInputReceived(orchestrationName, runId, stepName, response);
			return response;
		}
		finally
		{
			waiter.EndWait(runId, stepName);
			if (!resolved)
			{
				OnInputResolved?.Invoke(runId, stepName);
			}

			if (PendingInputStore is not null)
			{
				try
				{
					await PendingInputStore.DeleteAsync(orchestrationName, runId, stepName, CancellationToken.None).ConfigureAwait(false);
				}
				catch
				{
					// Persistence cleanup is best-effort; failures here must not surface to callers.
				}
			}
		}
	}
}
