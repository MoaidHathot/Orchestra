namespace Orchestra.Engine;

public class OrchestrationExecutionContext
{
	public required Mcp[] Mcps { get; init; }

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

	public string GetDependencyOutputs(string[] dependsOn)
	{
		if (dependsOn.Length == 0)
			return string.Empty;

		if (dependsOn.Length == 1)
			return _results[dependsOn[0]].Content;

		return string.Join("\n\n---\n\n",
			dependsOn.Select(dep => $"## Output from '{dep}':\n{_results[dep].Content}"));
	}
}
