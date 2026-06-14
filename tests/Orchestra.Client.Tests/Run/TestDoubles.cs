using System.Text.Json;
using Orchestra.Client.Run;

namespace Orchestra.Client.Tests.Run;

/// <summary>
/// Test double that records every observer callback for verification, with a tap so tests
/// can wait for the terminal event without sleeping.
/// </summary>
internal sealed class RecordingRunObserver : IRunObserver
{
	public List<string> StepStarted { get; } = new();
	public List<string> StepCompleted { get; } = new();
	public List<(string Step, string Error)> StepErrored { get; } = new();
	public List<string> StepCancelled { get; } = new();
	public List<(string Step, string Reason)> StepSkipped { get; } = new();
	public List<AwaitingInputInfo> AwaitingInput { get; } = new();
	public List<(string Step, string? Choice, string? Reply, string? RespondedBy)> InputReceived { get; } = new();
	public List<(string Step, string OnTimeout)> InputTimeouts { get; } = new();
	public string? OrchestrationName { get; private set; }
	public string? RunId { get; private set; }
	public string? ExecutionId { get; private set; }
	public string? FinalStatus { get; private set; }
	public string? CancellationReason { get; private set; }
	public string? FinalError { get; private set; }
	public string? StreamInterruptReason { get; private set; }
	public List<(string EventType, string Json)> Unknown { get; } = new();

	public void OnExecutionStarted(string executionId) => ExecutionId = executionId;
	public void OnRunContext(string orchestrationName, string runId)
	{
		OrchestrationName = orchestrationName;
		RunId = runId;
	}
	public void OnStepStarted(string stepName) => StepStarted.Add(stepName);
	public void OnStepCompleted(string stepName) => StepCompleted.Add(stepName);
	public void OnStepError(string stepName, string error) => StepErrored.Add((stepName, error));
	public void OnStepCancelled(string stepName) => StepCancelled.Add(stepName);
	public void OnStepSkipped(string stepName, string reason) => StepSkipped.Add((stepName, reason));
	public void OnAwaitingInput(AwaitingInputInfo info) => AwaitingInput.Add(info);
	public void OnInputReceived(string stepName, string? choice, string? reply, string? respondedBy)
		=> InputReceived.Add((stepName, choice, reply, respondedBy));
	public void OnInputTimeout(string stepName, string onTimeout) => InputTimeouts.Add((stepName, onTimeout));
	public void OnOrchestrationDone(string status) => FinalStatus = status;
	public void OnOrchestrationCancelled(string? reason) => CancellationReason = reason;
	public void OnOrchestrationError(string error) => FinalError = error;
	public void OnUnknownEvent(string eventType, JsonElement payload)
		=> Unknown.Add((eventType, payload.GetRawText()));
	public void OnStreamInterrupted(string? reason) => StreamInterruptReason = reason;
}

/// <summary>
/// Programmable prompter used by tests: records inputs received and returns a pre-canned
/// response. Optionally throws on the Nth call to simulate non-interactive abort.
/// </summary>
internal sealed class StubHumanInputPrompter : IHumanInputPrompter
{
	private readonly Func<AwaitingInputInfo, HumanInputResponse> _factory;
	public List<AwaitingInputInfo> Calls { get; } = new();

	public StubHumanInputPrompter(Func<AwaitingInputInfo, HumanInputResponse> factory)
	{
		_factory = factory;
	}

	public Task<HumanInputResponse> PromptAsync(AwaitingInputInfo info, CancellationToken cancellationToken)
	{
		Calls.Add(info);
		return Task.FromResult(_factory(info));
	}
}

/// <summary>
/// Aborting stub: throws <see cref="NonInteractiveAbortException"/> on every call.
/// </summary>
internal sealed class AbortingHumanInputPrompter : IHumanInputPrompter
{
	public List<AwaitingInputInfo> Calls { get; } = new();

	public Task<HumanInputResponse> PromptAsync(AwaitingInputInfo info, CancellationToken cancellationToken)
	{
		Calls.Add(info);
		throw new NonInteractiveAbortException("non-interactive (test)");
	}
}
