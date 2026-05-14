namespace Orchestra.Engine;

/// <summary>
/// Structured snapshot of a child orchestration's terminal (or in-flight, for async-dispatched)
/// state, attached to the parent step's <see cref="ExecutionResult.ChildOrchestrationInfo"/>.
/// This is what powers the template bindings
/// <c>{{stepName.executionId|status|errorMessage|completionReason|childResult}}</c> and
/// <c>{{stepName.steps.&lt;childStepName&gt;.output|rawOutput|error|status|files}}</c>.
/// </summary>
/// <remarks>
/// Persisted only as the minimal projection (<see cref="ExecutionId"/>,
/// <see cref="OrchestrationName"/>, <see cref="Status"/>, <see cref="ErrorMessage"/>) on
/// <see cref="StepRunRecord"/>. The full per-step content lives on the child's own
/// <c>run.json</c>; binding into it via templates uses the in-memory copy populated
/// during the parent's run.
/// </remarks>
public sealed class ChildOrchestrationInfo
{
	/// <summary>
	/// Execution ID of the child run. Always present, even when the child failed to launch
	/// — in which case other fields may be defaults.
	/// </summary>
	public required string ExecutionId { get; init; }

	/// <summary>
	/// Display name of the child orchestration.
	/// </summary>
	public required string OrchestrationName { get; init; }

	/// <summary>
	/// Registry ID of the child orchestration. Null when not resolved (e.g. launch failure).
	/// </summary>
	public string? OrchestrationId { get; init; }

	/// <summary>
	/// Terminal status of the child run. For async-dispatch mode this is set to
	/// <see cref="ExecutionStatus.Pending"/> to signal "dispatched but not yet known".
	/// </summary>
	public required ExecutionStatus Status { get; init; }

	/// <summary>
	/// Diagnostic error message from the child run (non-null on failure or cancellation).
	/// </summary>
	public string? ErrorMessage { get; init; }

	/// <summary>
	/// Summary of the child run's terminal step outputs, suitable as the parent step's
	/// content. Same value as <c>{{stepName.output}}</c> on the parent step.
	/// </summary>
	public string? FinalContent { get; init; }

	/// <summary>
	/// When set, the child orchestration was completed early via <c>orchestra_complete</c>.
	/// </summary>
	public string? CompletionReason { get; init; }

	/// <summary>
	/// Structured cancellation cause when the child ran to a Cancelled terminal state.
	/// </summary>
	public CancellationDetails? Cancellation { get; init; }

	/// <summary>
	/// Per-step results of the child run keyed by step name. Empty for async-dispatch mode
	/// and for launch failures. For sync mode with a partial (failed) child run, this
	/// contains the entries that completed before the failure — which is the whole point
	/// of exposing this data to the parent (self-healing controllers can inspect what
	/// succeeded vs. what didn't).
	/// </summary>
	public IReadOnlyDictionary<string, ChildStepInfo> StepResults { get; init; } =
		new Dictionary<string, ChildStepInfo>(StringComparer.OrdinalIgnoreCase);

	/// <summary>
	/// Wall-clock time at which the child run was registered.
	/// </summary>
	public DateTimeOffset StartedAt { get; init; }

	/// <summary>
	/// Wall-clock time at which the child run reached a terminal state. Null for async-dispatch.
	/// </summary>
	public DateTimeOffset? CompletedAt { get; init; }
}

/// <summary>
/// Per-step projection of a child orchestration's step result. Carries the data the
/// parent's templates and downstream steps can drill into via
/// <c>{{stepName.steps.&lt;childStepName&gt;.&lt;property&gt;}}</c>.
/// </summary>
public sealed class ChildStepInfo
{
	/// <summary>
	/// Terminal status of this child step (e.g. Succeeded, Failed, Cancelled, Skipped,
	/// NoAction).
	/// </summary>
	public required ExecutionStatus Status { get; init; }

	/// <summary>
	/// Final content of this child step (after any output handler).
	/// </summary>
	public string Content { get; init; } = string.Empty;

	/// <summary>
	/// Raw content of this child step (before any output handler). Null when no output
	/// handler ran or for non-succeeded steps.
	/// </summary>
	public string? RawContent { get; init; }

	/// <summary>
	/// Error message for this child step when it did not succeed.
	/// </summary>
	public string? ErrorMessage { get; init; }

	/// <summary>
	/// Full paths of files saved by this child step via <c>orchestra_save_file</c>.
	/// </summary>
	public IReadOnlyList<string> SavedFiles { get; init; } = Array.Empty<string>();
}
