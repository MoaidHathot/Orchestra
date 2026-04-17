using Orchestra.Engine;
using Orchestra.Host.Triggers;

namespace Orchestra.Host.Api;

/// <summary>
/// Default implementation of ITriggerExecutionCallback that creates reporters via the DI-registered factory.
/// This is automatically registered by AddOrchestraHost when no custom callback is provided.
/// </summary>
public class DefaultExecutionCallback : ITriggerExecutionCallback
{
	private readonly IOrchestrationReporterFactory _reporterFactory;
	private readonly DashboardEventBroadcaster? _dashboardBroadcaster;

	public DefaultExecutionCallback(IOrchestrationReporterFactory reporterFactory, DashboardEventBroadcaster? dashboardBroadcaster = null)
	{
		_reporterFactory = reporterFactory;
		_dashboardBroadcaster = dashboardBroadcaster;
	}

	/// <summary>
	/// Creates a reporter for streaming events to clients.
	/// </summary>
	public IOrchestrationReporter CreateReporter() => _reporterFactory.Create();

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
				info.IncrementCompletedSteps();
				info.CurrentStep = null;
			};
		}

		// Notify Portal clients so "Recent Executions" / "Active Orchestrations" update
		// without polling.
		_dashboardBroadcaster?.BroadcastExecutionStarted(
			info.ExecutionId,
			info.OrchestrationId,
			info.OrchestrationName,
			info.TriggeredBy);
	}

	/// <summary>
	/// Called when an execution completes.
	/// </summary>
	public void OnExecutionCompleted(ActiveExecutionInfo info)
	{
		_dashboardBroadcaster?.BroadcastExecutionCompleted(
			info.ExecutionId,
			info.OrchestrationId,
			info.OrchestrationName,
			info.Status.ToString());
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
		info.IncrementCompletedSteps();
		info.CurrentStep = null;
		info.OnStepCompleted?.Invoke(stepName);
	}
}
