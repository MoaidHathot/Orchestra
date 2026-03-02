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

		var hasFailure = terminalResults.Values.Any(
			r => r.Status is ExecutionStatus.Failed or ExecutionStatus.Skipped);

		return new OrchestrationResult
		{
			Status = hasFailure ? ExecutionStatus.Failed : ExecutionStatus.Succeeded,
			Results = terminalResults,
			StepResults = stepResults,
		};
	}
}
