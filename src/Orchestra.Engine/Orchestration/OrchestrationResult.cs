namespace Orchestra.Engine;

public class OrchestrationResult
{
	public required ExecutionStatus Status { get; init; }
	public required IReadOnlyDictionary<string, ExecutionResult> StepResults { get; init; }

	public static OrchestrationResult From(Dictionary<string, ExecutionResult> stepResults)
	{
		var hasFailure = stepResults.Values.Any(r => r.Status is ExecutionStatus.Failed or ExecutionStatus.Skipped);

		return new OrchestrationResult
		{
			Status = hasFailure ? ExecutionStatus.Failed : ExecutionStatus.Succeeded,
			StepResults = stepResults,
		};
	}
}
