namespace Orchestra.Engine;

/// <summary>
/// A persisted record describing a step that is currently waiting for human input.
/// Records are written when an Approval step or a <c>orchestra_request_user_input</c>
/// engine tool call begins waiting and deleted when the wait is satisfied (response
/// received) or abandoned (run cancelled / host shutdown for engine-tool path).
/// Stored per orchestration name + run + step name so a single run can have
/// multiple concurrent waits when the DAG fans out.
/// </summary>
public sealed class PendingInputRecord
{
	/// <summary>Unique run identifier this wait belongs to.</summary>
	public required string RunId { get; init; }

	/// <summary>The orchestration name (used for routing the response back).</summary>
	public required string OrchestrationName { get; init; }

	/// <summary>The step that is awaiting input (used as the response-routing key).</summary>
	public required string StepName { get; init; }

	/// <summary>Whether this wait was produced by an Approval step or an engine tool call.</summary>
	public required PendingInputKind Kind { get; init; }

	/// <summary>The human-readable prompt to display to the user.</summary>
	public required string Prompt { get; init; }

	/// <summary>
	/// Optional set of allowed choices when the wait constrains the response. Empty when
	/// the wait accepts any free-form reply.
	/// </summary>
	public string[] Choices { get; init; } = [];

	/// <summary>
	/// When this wait began (server time). Used to surface "waiting for X" durations and
	/// to compute the orchestration timeout offset when <c>pauseTimeoutDuringWait</c> is on.
	/// </summary>
	public required DateTimeOffset CreatedAt { get; init; }

	/// <summary>
	/// Optional expiration timestamp derived from the step's <c>timeoutSeconds</c>. When
	/// <c>null</c>, the wait runs indefinitely (subject only to the orchestration-level
	/// cancellation token).
	/// </summary>
	public DateTimeOffset? ExpiresAt { get; init; }
}
