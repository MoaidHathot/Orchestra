using System.Collections.Concurrent;

namespace Orchestra.Engine;

public class OrchestrationExecutionContext
{
	public Dictionary<string, string> Parameters { get; init; } = [];

	/// <summary>
	/// Runtime metadata for the orchestration execution, providing built-in variables
	/// accessible via <c>{{orchestration.name}}</c>, <c>{{orchestration.runId}}</c>, etc.
	/// </summary>
	public required OrchestrationInfo OrchestrationInfo { get; init; }

	/// <summary>
	/// User-defined variables declared in the orchestration JSON.
	/// Accessible via <c>{{vars.name}}</c> template expressions.
	/// Values may contain template expressions that are resolved lazily.
	/// </summary>
	public Dictionary<string, string> Variables { get; init; } = [];

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

	/// <summary>
	/// Default model from the orchestration.
	/// Applied to Prompt steps that don't define their own Model.
	/// When null, each Prompt step must specify its own model.
	/// </summary>
	public string? DefaultModel { get; init; }

	/// <summary>
	/// Default per-step timeout from the orchestration.
	/// Applied to steps that don't define their own TimeoutSeconds.
	/// When null, no default per-step timeout is applied.
	/// </summary>
	public int? DefaultStepTimeoutSeconds { get; init; }

	/// <summary>
	/// The temp file store for this orchestration run, providing file I/O operations
	/// scoped to a run-specific temp directory. Available via <c>{{orchestration.tempDir}}</c>
	/// template expressions.
	/// May be null when no data path is configured.
	/// </summary>
	public OrchestrationTempFileStore? TempFileStore { get; init; }

	/// <summary>
	/// Tracks which template expressions (env vars, variables) were resolved during execution.
	/// Thread-safe for concurrent step execution.
	/// </summary>
	public TemplateResolutionTracker ResolutionTracker { get; } = new();

	/// <summary>
	/// The base URL of the Orchestra server, accessible via <c>{{server.url}}</c>.
	/// Set by the host layer. When null, <c>{{server.url}}</c> falls back to
	/// the <c>ORCHESTRA_SERVER_URL</c> environment variable.
	/// </summary>
	public string? ServerUrl { get; init; }

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
			result.Status is ExecutionStatus.Failed or ExecutionStatus.Skipped or ExecutionStatus.Cancelled or ExecutionStatus.NoAction);
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
