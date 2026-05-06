namespace Orchestra.Engine;

/// <summary>
/// Provider-neutral settings that describe the agent execution capacity requested
/// for a single orchestration run.
/// </summary>
public sealed class AgentPoolConfig
{
	/// <summary>
	/// Minimum number of provider workers to keep ready for the run. Providers may use
	/// this to pre-start workers before the first prompt step needs them.
	/// </summary>
	public int? MinInstances { get; set; }

	/// <summary>
	/// Maximum number of provider workers that may be created for one orchestration run.
	/// </summary>
	public int? MaxInstances { get; set; }

	/// <summary>
	/// Maximum number of active prompt sessions each provider worker may host at once.
	/// </summary>
	public int? MaxSessionsPerInstance { get; set; }

	/// <summary>
	/// Number of seconds an idle provider worker above <see cref="MinInstances"/> may
	/// remain alive before the provider shrinks the run pool.
	/// </summary>
	public int? IdleTimeoutSeconds { get; set; }
}
