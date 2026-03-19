using System.Collections.Concurrent;

namespace Orchestra.Engine;

public class OrchestrationExecutionContext
{
	public Dictionary<string, string> Parameters { get; init; } = [];

	/// <summary>
	/// Default system prompt mode from the orchestration.
	/// Steps can override this with their own SystemPromptMode.
	/// When null, the SDK's default behavior is used.
	/// </summary>
	public SystemPromptMode? DefaultSystemPromptMode { get; init; }

	/// <summary>
	/// Default retry policy from the orchestration.
	/// Applied to steps that don't define their own retry policy.
	/// When null, no retries are performed.
	/// </summary>
	public RetryPolicy? DefaultRetryPolicy { get; init; }

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

	/// <summary>
	/// Attempts to get the result for a step by name.
	/// Returns null if no result has been recorded for the step.
	/// </summary>
	public ExecutionResult? TryGetResult(string stepName)
	{
		return _results.TryGetValue(stepName, out var result) ? result : null;
	}

	public IReadOnlyDictionary<string, ExecutionResult> Results => _results;

	public bool HasAnyDependencyFailed(string[] dependsOn)
	{
		return dependsOn.Any(dep =>
			_results.TryGetValue(dep, out var result) &&
			result.Status is ExecutionStatus.Failed or ExecutionStatus.Skipped);
	}

	/// <summary>
	/// Gets the processed content from succeeded dependencies as a dictionary.
	/// </summary>
	public IReadOnlyDictionary<string, string> GetDependencyOutputs(string[] dependsOn)
	{
		var outputs = new Dictionary<string, string>();
		foreach (var dep in dependsOn)
		{
			if (_results.TryGetValue(dep, out var result) && result.Status == ExecutionStatus.Succeeded)
			{
				outputs[dep] = result.Content;
			}
		}
		return outputs;
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
