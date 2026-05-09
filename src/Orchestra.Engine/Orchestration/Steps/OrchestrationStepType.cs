namespace Orchestra.Engine;

public enum OrchestrationStepType
{
	Prompt,
	Http,
	Transform,
	Command,
	Script,

	/// <summary>
	/// Invokes another orchestration as a step. Supports both synchronous and asynchronous
	/// modes. The step's output is the child orchestration's terminal content (sync) or a
	/// dispatch JSON containing the child execution ID (async).
	/// </summary>
	Orchestration,

	/// <summary>
	/// Pauses the orchestration and waits for human input. The step persists a pending
	/// input record, registers a wait, and emits the <c>step.awaitingInput</c> hook event.
	/// When the user responds via the host's HumanInput API, the step succeeds with the
	/// reply (or chosen choice) as its output content. Survives host restarts via the
	/// existing checkpoint/resume mechanism.
	/// </summary>
	Approval,
}
