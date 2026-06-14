namespace Orchestra.Client.Sse;

/// <summary>
/// String constants for every Server-Sent Events <c>event:</c> name the CLI knows how to render.
/// Mirrors the events emitted by <c>Orchestra.Host.Api.SseReporter</c>.
/// </summary>
/// <remarks>
/// Unknown event names are not an error — the consumer simply ignores them (or surfaces them
/// in <c>--verbose</c> mode). Centralizing the names here prevents typo-driven silent drops.
/// </remarks>
public static class SseEventTypes
{
	// Lifecycle
	public const string ExecutionStarted = "execution-started";
	public const string ExecutionInfo = "execution-info";
	public const string RunContext = "run-context";
	public const string Heartbeat = "heartbeat";
	public const string StatusChanged = "status-changed";

	// Step lifecycle
	public const string StepStarted = "step-started";
	public const string StepCompleted = "step-completed";
	public const string StepError = "step-error";
	public const string StepCancelled = "step-cancelled";
	public const string StepSkipped = "step-skipped";
	public const string StepStatusSet = "step-status-set";
	public const string StepRetry = "step-retry";

	// HITL
	public const string AwaitingInput = "awaiting-input";
	public const string InputReceived = "input-received";
	public const string InputTimeout = "input-timeout";

	// Streaming content
	public const string ContentDelta = "content-delta";
	public const string ReasoningDelta = "reasoning-delta";
	public const string Usage = "usage";

	// Terminal
	public const string OrchestrationDone = "orchestration-done";
	public const string OrchestrationCancelled = "orchestration-cancelled";
	public const string OrchestrationError = "orchestration-error";

	/// <summary>
	/// Returns true when <paramref name="eventName"/> is one of the three terminal events
	/// (the consumer loop should exit after rendering it).
	/// </summary>
	public static bool IsTerminal(string eventName) =>
		eventName is OrchestrationDone or OrchestrationCancelled or OrchestrationError;
}
