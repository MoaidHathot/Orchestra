namespace Orchestra.Engine;

public class OrchestrationExecutionContext
{
	public Dictionary<string, string> Parameters { get; init; } = [];

	private readonly Dictionary<string, ExecutionResult> _results = new();

	public void AddResult(string stepName, ExecutionResult result)
	{
		_results[stepName] = result;
	}

	public ExecutionResult GetResult(string stepName)
	{
		return _results.TryGetValue(stepName, out var result)
			? result
			: throw new InvalidOperationException($"No result found for step '{stepName}'.");
	}

	public IReadOnlyDictionary<string, ExecutionResult> Results => _results;

	public bool HasAnyDependencyFailed(string[] dependsOn)
	{
		return dependsOn.Any(dep =>
			_results.TryGetValue(dep, out var result) &&
			result.Status is ExecutionStatus.Failed or ExecutionStatus.Skipped);
	}

	public string GetDependencyOutputs(string[] dependsOn)
	{
		if (dependsOn.Length == 0)
			return string.Empty;

		// Only include outputs from succeeded dependencies
		var succeeded = dependsOn
			.Where(dep => _results.TryGetValue(dep, out var r) && r.Status == ExecutionStatus.Succeeded)
			.ToArray();

		if (succeeded.Length == 0)
			return string.Empty;

		if (succeeded.Length == 1)
			return _results[succeeded[0]].Content;

		return string.Join("\n\n---\n\n",
			succeeded.Select(dep => $"## Output from '{dep}':\n{_results[dep].Content}"));
	}
}
