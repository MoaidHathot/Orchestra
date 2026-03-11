using Orchestra.Engine;

namespace Orchestra.Host.Triggers;

/// <summary>
/// Callback interface for trigger execution events.
/// Implementations handle UI-specific concerns like creating reporters and tracking progress.
/// </summary>
public interface ITriggerExecutionCallback
{
	/// <summary>
	/// Creates a reporter for a new execution.
	/// </summary>
	IOrchestrationReporter CreateReporter();

	/// <summary>
	/// Called when an execution starts.
	/// </summary>
	void OnExecutionStarted(ActiveExecutionInfo info);

	/// <summary>
	/// Called when an execution completes.
	/// </summary>
	void OnExecutionCompleted(ActiveExecutionInfo info);

	/// <summary>
	/// Called when a step starts.
	/// </summary>
	void OnStepStarted(ActiveExecutionInfo info, string stepName);

	/// <summary>
	/// Called when a step completes.
	/// </summary>
	void OnStepCompleted(ActiveExecutionInfo info, string stepName);
}
