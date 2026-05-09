namespace Orchestra.Engine;

public enum ExecutionStatus
{
	Pending,
	Running,
	Succeeded,
	Failed,
	Skipped,
	Cancelled,

	/// <summary>
	/// The step completed successfully but determined that no further action is needed.
	/// Downstream dependent steps will be skipped.
	/// </summary>
	NoAction,

	/// <summary>
	/// The step is paused waiting for human input. Used by the <c>Approval</c> step type
	/// while a <see cref="PendingInputRecord"/> is outstanding for the run/step.
	/// Once the user responds via the host's HumanInput API, the wait completes and the
	/// step transitions to <see cref="Succeeded"/> with the user's reply as content.
	/// </summary>
	AwaitingInput,
}
