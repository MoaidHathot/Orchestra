using Orchestra.Engine;
using Orchestra.Host.Triggers;

namespace Orchestra.Playground.Copilot.Terminal;

/// <summary>
/// Callback for trigger execution events in the Terminal TUI.
/// </summary>
public class TerminalExecutionCallback : ITriggerExecutionCallback
{
	private readonly TerminalOrchestrationReporter _reporter;

	/// <summary>
	/// Event raised when any execution event occurs.
	/// </summary>
	public event Action? OnUpdate;

	public TerminalExecutionCallback(TerminalOrchestrationReporter reporter)
	{
		_reporter = reporter;
	}

	public IOrchestrationReporter CreateReporter()
	{
		return _reporter;
	}

	public void OnExecutionStarted(ActiveExecutionInfo info)
	{
		// Wire up the reporter's step callbacks to update the execution info
		_reporter.OnStepStarted = (stepName) =>
		{
			info.CurrentStep = stepName;
			OnUpdate?.Invoke();
		};
		_reporter.OnStepCompleted = (stepName) =>
		{
			info.CompletedSteps++;
			info.CurrentStep = null;
			OnUpdate?.Invoke();
		};

		OnUpdate?.Invoke();
	}

	public void OnExecutionCompleted(ActiveExecutionInfo info)
	{
		// Clear the callbacks
		_reporter.OnStepStarted = null;
		_reporter.OnStepCompleted = null;
		OnUpdate?.Invoke();
	}

	public void OnStepStarted(ActiveExecutionInfo info, string stepName)
	{
		info.CurrentStep = stepName;
		OnUpdate?.Invoke();
	}

	public void OnStepCompleted(ActiveExecutionInfo info, string stepName)
	{
		info.CompletedSteps++;
		info.CurrentStep = null;
		OnUpdate?.Invoke();
	}
}
