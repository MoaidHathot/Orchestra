namespace Orchestra.Engine;

public class OrchestrationResult
{
	public required ExecutionStatus Status { get; init; }

	/// <summary>
	/// Results of the terminal steps (steps that no other step depends on).
	/// </summary>
	public required IReadOnlyDictionary<string, ExecutionResult> Results { get; init; }

	/// <summary>
	/// Results of all steps in the orchestration.
	/// </summary>
	public required IReadOnlyDictionary<string, ExecutionResult> StepResults { get; init; }

	/// <summary>
	/// Full paths of files saved during this orchestration run.
	/// </summary>
	public string[] SavedFiles { get; init; } = [];

	/// <summary>
	/// When set, indicates the orchestration was completed early by the orchestra_complete tool.
	/// Contains the reason provided by the LLM.
	/// </summary>
	public string? CompletionReason { get; init; }

	/// <summary>
	/// The name of the step that triggered early completion via orchestra_complete.
	/// </summary>
	public string? CompletedByStep { get; init; }

	/// <summary>
	/// When true, indicates the orchestration did not fully complete.
	/// This covers cases where all terminal steps had NoAction/Skipped status,
	/// or the orchestration was completed early via orchestra_complete.
	/// The orchestration may still have a <see cref="ExecutionStatus.Succeeded"/> status
	/// because it did not fail — it simply didn't do any meaningful work or was cut short.
	/// </summary>
	public bool IsIncomplete { get; init; }

	/// <summary>
	/// Structured cancellation cause when <see cref="Status"/> is <see cref="ExecutionStatus.Cancelled"/>.
	/// Distinguishes external cancel, the orchestration's own <c>timeoutSeconds</c>,
	/// a sync-invoke wrapper timeout, and early completion via <c>orchestra_complete</c>.
	/// Null when the run was not cancelled.
	/// </summary>
	public CancellationDetails? Cancellation { get; init; }

	public static OrchestrationResult From(
		Orchestration orchestration,
		IReadOnlyDictionary<string, ExecutionResult> stepResults,
		ExecutionStatus? orchestrationCompleteStatus = null,
		string? orchestrationCompleteReason = null,
		string? orchestrationCompleteStepName = null,
		CancellationDetails? cancellation = null,
		string[]? savedFiles = null)
	{
		// Terminal steps are those that no other step depends on
		var dependedOn = new HashSet<string>(
			orchestration.Steps.SelectMany(s => s.DependsOn),
			StringComparer.OrdinalIgnoreCase);

		var terminalResults = stepResults
			.Where(kv => !dependedOn.Contains(kv.Key))
			.ToDictionary(kv => kv.Key, kv => kv.Value);

		// NoAction is a valid terminal state — it means "nothing to do" rather than failure.
		// Check if ALL terminal steps are NoAction/Skipped (orchestration did nothing).
		var allTerminalNoActionOrSkipped = terminalResults.Count > 0
			&& terminalResults.Values.All(r => r.Status is ExecutionStatus.NoAction or ExecutionStatus.Skipped);

		ExecutionStatus status;

		// If orchestration was completed early via orchestra_complete, use the requested status
		if (orchestrationCompleteStatus is not null)
		{
			status = orchestrationCompleteStatus.Value;
		}
		else
		{
			// Determine overall status from ALL step results (not just terminal).
			// A failed step may be non-terminal (with dependents that got skipped),
			// so checking only terminal results could miss it. The same is true for a
			// Cancelled step mid-DAG whose dependents end up Skipped.
			var hasAnyFailed = stepResults.Values.Any(r => r.Status == ExecutionStatus.Failed);
			var hasAnyCancelled = stepResults.Values.Any(r => r.Status == ExecutionStatus.Cancelled);

			// Precedence:
			//   1. Failed    — any step failure dominates the entire run.
			//   2. Cancelled — any step Cancelled (terminal or not) marks the run as
			//                  Cancelled. A Skipped step alone is NOT enough to call
			//                  the run Cancelled because Skipped is also the cascade
			//                  status produced by a benign NoAction gate.
			//   3. Succeeded — everything else. If every terminal step ended in
			//                  NoAction/Skipped the run is additionally marked as
			//                  IsIncomplete (a "nothing to do" run).
			//
			// NOTE: a Skipped step only exists as a consequence of one of
			// {Failed, Cancelled, Skipped, NoAction} dependencies (see
			// OrchestrationExecutor.ExecuteOrSkipStepAsync). When neither Failed nor
			// Cancelled appears anywhere in stepResults, every Skipped is rooted in a
			// NoAction step — so it is correct to treat the run as Succeeded.
			status = hasAnyFailed
				? ExecutionStatus.Failed
				: hasAnyCancelled
					? ExecutionStatus.Cancelled
					: ExecutionStatus.Succeeded;
		}

		// An orchestration is considered "incomplete" when it succeeded technically
		// but did not fully execute: either all terminal steps had nothing to do
		// (NoAction/Skipped), or it was completed early via orchestra_complete.
		var isIncomplete = orchestrationCompleteStatus is not null
			|| (status == ExecutionStatus.Succeeded && allTerminalNoActionOrSkipped);

		var runSavedFiles = savedFiles ?? stepResults.Values
			.SelectMany(result => result.SavedFiles)
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.ToArray();

		return new OrchestrationResult
		{
			Status = status,
			Results = terminalResults,
			StepResults = stepResults,
			CompletionReason = orchestrationCompleteReason,
			CompletedByStep = orchestrationCompleteStepName,
			IsIncomplete = isIncomplete,
			SavedFiles = runSavedFiles,
			// Only attach cancellation details when the run actually ended Cancelled.
			// (orchestra_complete may upgrade Cancelled → Succeeded/Failed, in which case
			// the cancellation cause is irrelevant and would mislead consumers.)
			Cancellation = status == ExecutionStatus.Cancelled ? cancellation : null,
		};
	}
}
