namespace Orchestra.Copilot;

/// <summary>
/// Thrown when a Copilot CLI session fails during execution (e.g. JSON-RPC ConnectionLost,
/// fatal SessionErrorEvent, abnormal SessionShutdownEvent with an error reason).
/// This exception MUST propagate up to the orchestration step so the run is marked as
/// Failed with a clear error category instead of silently succeeding with empty content.
/// </summary>
public sealed class CopilotSessionFailedException : Exception
{
	/// <summary>
	/// The kind of failure that occurred (error event, abnormal shutdown, etc.).
	/// </summary>
	public CopilotSessionFailureKind Kind { get; }

	/// <summary>
	/// The model that the failed session was running.
	/// </summary>
	public string Model { get; }

	/// <summary>
	/// Optional reason string from the SDK (e.g. SessionShutdownEvent.ErrorReason).
	/// </summary>
	public string? Reason { get; }

	public CopilotSessionFailedException(CopilotSessionFailureKind kind, string model, string message, string? reason = null)
		: base(message)
	{
		Kind = kind;
		Model = model;
		Reason = reason;
	}
}

/// <summary>
/// Categorises why a Copilot CLI session failed.
/// </summary>
public enum CopilotSessionFailureKind
{
	/// <summary>SDK emitted a SessionErrorEvent (fatal session-level error from the CLI).</summary>
	SessionError,

	/// <summary>SDK emitted a SessionShutdownEvent with a non-null ErrorReason (CLI shutting down due to error).</summary>
	AbnormalShutdown,
}
