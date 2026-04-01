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

	public static OrchestrationResult From(
		Orchestration orchestration,
		IReadOnlyDictionary<string, ExecutionResult> stepResults,
		ExecutionStatus? orchestrationCompleteStatus = null,
		string? orchestrationCompleteReason = null,
		string? orchestrationCompleteStepName = null)
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
			// so checking only terminal results could miss it.
			var hasAnyFailed = stepResults.Values.Any(r => r.Status == ExecutionStatus.Failed);
			var hasAnyCancelledOrSkipped = terminalResults.Values.Any(
				r => r.Status is ExecutionStatus.Cancelled or ExecutionStatus.Skipped);

			// Failed takes priority over Cancelled; Cancelled over Succeeded.
			// NoAction steps are not failures — they just mean the step found nothing to do.
			status = hasAnyFailed
				? ExecutionStatus.Failed
				: hasAnyCancelledOrSkipped
					? ExecutionStatus.Cancelled
					: allTerminalNoActionOrSkipped
						? ExecutionStatus.Succeeded // All terminal steps had nothing to do — that's a valid success
						: ExecutionStatus.Succeeded;
		}

		// An orchestration is considered "incomplete" when it succeeded technically
		// but did not fully execute: either all terminal steps had nothing to do
		// (NoAction/Skipped), or it was completed early via orchestra_complete.
		var isIncomplete = orchestrationCompleteStatus is not null
			|| (status == ExecutionStatus.Succeeded && allTerminalNoActionOrSkipped);

		return new OrchestrationResult
		{
			Status = status,
			Results = terminalResults,
			StepResults = stepResults,
			CompletionReason = orchestrationCompleteReason,
			CompletedByStep = orchestrationCompleteStepName,
			IsIncomplete = isIncomplete,
		};
	}
}
