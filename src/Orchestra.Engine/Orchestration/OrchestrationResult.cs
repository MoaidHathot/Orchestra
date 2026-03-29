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

	public static OrchestrationResult From(Orchestration orchestration, IReadOnlyDictionary<string, ExecutionResult> stepResults)
	{
		// Terminal steps are those that no other step depends on
		var dependedOn = new HashSet<string>(
			orchestration.Steps.SelectMany(s => s.DependsOn),
			StringComparer.OrdinalIgnoreCase);

		var terminalResults = stepResults
			.Where(kv => !dependedOn.Contains(kv.Key))
			.ToDictionary(kv => kv.Key, kv => kv.Value);

		// Determine overall status from ALL step results (not just terminal).
		// A failed step may be non-terminal (with dependents that got skipped),
		// so checking only terminal results could miss it.
		var hasAnyFailed = stepResults.Values.Any(r => r.Status == ExecutionStatus.Failed);
		var hasAnyCancelledOrSkipped = terminalResults.Values.Any(
			r => r.Status is ExecutionStatus.Cancelled or ExecutionStatus.Skipped);

		// Failed takes priority over Cancelled; Cancelled over Succeeded.
		var status = hasAnyFailed
			? ExecutionStatus.Failed
			: hasAnyCancelledOrSkipped
				? ExecutionStatus.Cancelled
				: ExecutionStatus.Succeeded;

		return new OrchestrationResult
		{
			Status = status,
			Results = terminalResults,
			StepResults = stepResults,
		};
	}
}
