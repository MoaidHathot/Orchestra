using System.Collections.Concurrent;

namespace Orchestra.Engine;

/// <summary>
/// Captures the resolved runtime context of an orchestration run.
/// Includes orchestration metadata, resolved variables, parameters,
/// accessed environment variables, and storage location.
/// </summary>
public class RunContext
{
	/// <summary>
	/// Unique identifier for this run.
	/// </summary>
	public required string RunId { get; init; }

	/// <summary>
	/// Name of the orchestration that was executed.
	/// </summary>
	public required string OrchestrationName { get; init; }

	/// <summary>
	/// Version of the orchestration at execution time.
	/// </summary>
	public required string OrchestrationVersion { get; init; }

	/// <summary>
	/// When the run started.
	/// </summary>
	public required DateTimeOffset StartedAt { get; init; }

	/// <summary>
	/// How the run was triggered (e.g., "manual", "scheduler", "webhook", "loop").
	/// </summary>
	public string TriggeredBy { get; init; } = "manual";

	/// <summary>
	/// Optional trigger ID that initiated this run.
	/// </summary>
	public string? TriggerId { get; init; }

	/// <summary>
	/// Parameters provided for this run (the raw input values).
	/// </summary>
	public IReadOnlyDictionary<string, string> Parameters { get; init; } = new Dictionary<string, string>();

	/// <summary>
	/// User-defined orchestration variables (raw, before template expansion).
	/// These are the values declared in the orchestration JSON <c>variables</c> section.
	/// </summary>
	public IReadOnlyDictionary<string, string> Variables { get; init; } = new Dictionary<string, string>();

	/// <summary>
	/// Variables after template resolution. Only populated for variables that
	/// contain template expressions (i.e., whose resolved value differs from the raw value).
	/// </summary>
	public IReadOnlyDictionary<string, string> ResolvedVariables { get; init; } = new Dictionary<string, string>();

	/// <summary>
	/// Environment variables that were accessed during template resolution
	/// via <c>{{env.VAR_NAME}}</c> expressions. Key is the variable name,
	/// value is the resolved value (or null if the env var was not set).
	/// </summary>
	public IReadOnlyDictionary<string, string?> AccessedEnvironmentVariables { get; init; } = new Dictionary<string, string?>();

	/// <summary>
	/// The directory path where the run data is stored on disk.
	/// Only populated when using file-system storage.
	/// </summary>
	public string? DataDirectory { get; init; }
}

/// <summary>
/// Collector that tracks which template expressions are resolved during execution.
/// Thread-safe for concurrent step execution.
/// </summary>
public class TemplateResolutionTracker
{
	private readonly ConcurrentDictionary<string, string?> _accessedEnvVars = new();
	private readonly ConcurrentDictionary<string, string> _resolvedVariables = new();

	/// <summary>
	/// Records that an environment variable was accessed during template resolution.
	/// </summary>
	public void TrackEnvironmentVariable(string name, string? value)
	{
		_accessedEnvVars.TryAdd(name, value);
	}

	/// <summary>
	/// Records a resolved variable value (after template expansion).
	/// </summary>
	public void TrackResolvedVariable(string name, string resolvedValue)
	{
		_resolvedVariables[name] = resolvedValue;
	}

	/// <summary>
	/// Gets all environment variables that were accessed during resolution.
	/// </summary>
	public IReadOnlyDictionary<string, string?> AccessedEnvironmentVariables => _accessedEnvVars;

	/// <summary>
	/// Gets all variables that were resolved (with their final values).
	/// </summary>
	public IReadOnlyDictionary<string, string> ResolvedVariables => _resolvedVariables;
}
