namespace Orchestra.Engine;

/// <summary>
/// Coordinates in-process waits for human-in-the-loop responses. The Approval step
/// executor and the <c>orchestra_request_user_input</c> engine tool both call
/// <see cref="WaitAsync"/> after persisting a <see cref="PendingInputRecord"/>; the
/// host's HumanInput API completes them via <see cref="TryComplete"/> when a response
/// arrives. Implementations MUST be thread-safe and MUST honor cancellation tokens
/// linked to the step's effective token chain.
/// </summary>
public interface IHumanInputWaiter
{
	/// <summary>
	/// Awaits the user's response for a specific run + step. The wait completes when
	/// <see cref="TryComplete"/> is called or throws <see cref="OperationCanceledException"/>
	/// if <paramref name="cancellationToken"/> fires (step timeout, orchestration timeout,
	/// caller cancel, etc.). Multiple concurrent <c>WaitAsync</c> calls for the same key
	/// are not supported — the engine ensures only one wait per (run, step) is outstanding.
	/// </summary>
	Task<UserInputResponse> WaitAsync(
		string orchestrationName,
		string runId,
		string stepName,
		CancellationToken cancellationToken);

	/// <summary>
	/// Completes an outstanding wait with the supplied <paramref name="response"/>.
	/// Returns <c>true</c> when a wait was registered for the key and was completed;
	/// <c>false</c> when no in-process wait was found (the run may have moved on, the
	/// host may have restarted, or the wait may not have started yet).
	/// </summary>
	bool TryComplete(string orchestrationName, string runId, string stepName, UserInputResponse response);

	/// <summary>
	/// Cancels an outstanding wait, completing the awaiter's task with cancellation.
	/// Used by the host when explicitly aborting a pending input (e.g. abandoning a run).
	/// Returns <c>true</c> when a wait was found and cancelled.
	/// </summary>
	bool TryCancel(string orchestrationName, string runId, string stepName);

	/// <summary>
	/// Notifies the waiter that an orchestration run is currently blocked on user input.
	/// Implementations may use this for accounting (e.g. surfacing waits in the API).
	/// </summary>
	void BeginWait(string runId, string stepName);

	/// <summary>
	/// Pairs with <see cref="BeginWait"/>; called when the wait completes (response,
	/// timeout, or cancellation).
	/// </summary>
	void EndWait(string runId, string stepName);
}
