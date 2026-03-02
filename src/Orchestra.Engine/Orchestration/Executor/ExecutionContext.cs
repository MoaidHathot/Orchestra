using System.Collections.Concurrent;

namespace Orchestra.Engine;

public class OrchestrationExecutionContext
{
	public Dictionary<string, string> Parameters { get; init; } = [];

	private readonly ConcurrentDictionary<string, ExecutionResult> _results = new();
	private readonly ConcurrentDictionary<string, string> _loopFeedback = new();

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

	/// <summary>
	/// Gets raw dependency outputs as a dictionary (step name -> raw content before any handlers).
	/// </summary>
	public Dictionary<string, string> GetRawDependencyOutputs(string[] dependsOn)
	{
		var outputs = new Dictionary<string, string>();
		foreach (var dep in dependsOn)
		{
			if (_results.TryGetValue(dep, out var result) && result.Status == ExecutionStatus.Succeeded)
			{
				// Use RawContent if available (content before output handler), otherwise use Content
				outputs[dep] = result.RawContent ?? result.Content;
			}
		}
		return outputs;
	}

	/// <summary>
	/// Removes a step's result so it can be re-executed during a loop iteration.
	/// </summary>
	public void ClearResult(string stepName)
	{
		_results.TryRemove(stepName, out _);
	}

	/// <summary>
	/// Stores feedback from a checker step to be injected into the target step's prompt
	/// during a loop re-execution.
	/// </summary>
	public void SetLoopFeedback(string stepName, string feedback)
	{
		_loopFeedback[stepName] = feedback;
	}

	/// <summary>
	/// Retrieves and clears loop feedback for a step, if any.
	/// Returns null if no feedback is pending.
	/// </summary>
	public string? ConsumeLoopFeedback(string stepName)
	{
		if (_loopFeedback.TryRemove(stepName, out var feedback))
			return feedback;
		return null;
	}
}
