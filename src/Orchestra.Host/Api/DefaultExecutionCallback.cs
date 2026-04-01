using Orchestra.Engine;
using Orchestra.Host.Triggers;

namespace Orchestra.Host.Api;

/// <summary>
/// Default implementation of ITriggerExecutionCallback that creates SseReporter instances.
/// This is automatically registered by AddOrchestraHost when no custom callback is provided.
/// </summary>
public class DefaultExecutionCallback : ITriggerExecutionCallback
{
	/// <summary>
	/// Creates an SseReporter for streaming events to clients.
	/// </summary>
	public IOrchestrationReporter CreateReporter() => new SseReporter();

	/// <summary>
	/// Called when an execution starts. Wires reporter callbacks for step progress tracking.
	/// </summary>
	public void OnExecutionStarted(ActiveExecutionInfo info)
	{
		// Wire reporter callbacks so progress is tracked on the ActiveExecutionInfo.
		// This mirrors what ExecutionApi does for manual executions.
		if (info.Reporter is SseReporter sseReporter)
		{
			sseReporter.OnStepStarted = stepName =>
			{
				info.CurrentStep = stepName;
			};
			sseReporter.OnStepCompleted = stepName =>
			{
				info.CompletedSteps++;
				info.CurrentStep = null;
			};
		}
	}

	/// <summary>
	/// Called when an execution completes.
	/// </summary>
	public void OnExecutionCompleted(ActiveExecutionInfo info)
	{
		// Default implementation - no action needed
	}

	/// <summary>
	/// Called when a step starts. Updates the execution info.
	/// </summary>
	public void OnStepStarted(ActiveExecutionInfo info, string stepName)
	{
		info.CurrentStep = stepName;
		info.OnStepStarted?.Invoke(stepName);
	}

	/// <summary>
	/// Called when a step completes. Updates the execution info.
	/// </summary>
	public void OnStepCompleted(ActiveExecutionInfo info, string stepName)
	{
		info.CompletedSteps++;
		info.CurrentStep = null;
		info.OnStepCompleted?.Invoke(stepName);
	}
}
