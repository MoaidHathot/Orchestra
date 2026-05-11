using System.Text.Json;

namespace Orchestra.Cli.Run;

/// <summary>
/// Renders SSE events from a live run to a user-facing surface (typically the console).
/// Implementations decide how chatty to be (compact / quiet / verbose).
/// </summary>
public interface IRunObserver
{
	/// <summary>Called once with the <c>execution-started</c> payload.</summary>
	void OnExecutionStarted(string executionId);

	/// <summary>Called when <c>run-context</c> arrives (always after execution-started).</summary>
	void OnRunContext(string orchestrationName, string runId);

	/// <summary>Called for <c>step-started</c>.</summary>
	void OnStepStarted(string stepName);

	/// <summary>Called for <c>step-completed</c>.</summary>
	void OnStepCompleted(string stepName);

	/// <summary>Called for <c>step-error</c>.</summary>
	void OnStepError(string stepName, string error);

	/// <summary>Called for <c>step-cancelled</c>.</summary>
	void OnStepCancelled(string stepName);

	/// <summary>Called for <c>step-skipped</c>.</summary>
	void OnStepSkipped(string stepName, string reason);

	/// <summary>Called when the run begins waiting for human input.</summary>
	void OnAwaitingInput(AwaitingInputInfo info);

	/// <summary>Called after the user's response has been accepted server-side.</summary>
	void OnInputReceived(string stepName, string? choice, string? reply, string? respondedBy);

	/// <summary>Called when an awaiting-input wait timed out (non-terminal for the run).</summary>
	void OnInputTimeout(string stepName, string onTimeout);

	/// <summary>Called when the run completes successfully (terminal event).</summary>
	void OnOrchestrationDone(string status);

	/// <summary>Called when the run is cancelled (terminal event).</summary>
	void OnOrchestrationCancelled(string? reason);

	/// <summary>Called when the run errors out (terminal event).</summary>
	void OnOrchestrationError(string error);

	/// <summary>
	/// Called for any event the observer doesn't recognize. Default behavior: ignore.
	/// </summary>
	void OnUnknownEvent(string eventType, JsonElement payload);

	/// <summary>
	/// Called when the SSE stream is interrupted before any terminal event arrived.
	/// </summary>
	void OnStreamInterrupted(string? reason);
}
