namespace Orchestra.Copilot;

/// <summary>
/// Diagnostic snapshot of a <see cref="CopilotClientPool"/>'s current state. Read by
/// <c>CopilotAgentBuilder.GetRuntimeStatus()</c> for status reporting. Counters are
/// monotonic for the pool's lifetime: <see cref="TotalSwapsTriggered"/> and
/// <see cref="WorkersSwappedOut"/> never decrement.
/// </summary>
internal sealed record CopilotClientPoolSnapshot(
	int CliInstances,
	int ActiveSessions,
	int TotalSwapsTriggered = 0,
	int WorkersSwappedOut = 0);
