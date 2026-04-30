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
}
