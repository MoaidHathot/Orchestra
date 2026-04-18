namespace Orchestra.Engine;

/// <summary>
/// Configuration for infinite sessions (automatic context compaction).
/// When enabled, the Copilot CLI automatically manages context window limits
/// through background compaction and persists state to a workspace directory.
/// </summary>
public class InfiniteSessionConfig
{
	/// <summary>
	/// Whether infinite sessions are enabled. When null, the SDK default is used (enabled).
	/// </summary>
	public bool? Enabled { get; init; }

	/// <summary>
	/// Context utilization ratio (0.0-1.0) at which background compaction begins.
	/// Default: 0.80 (80%).
	/// </summary>
	public double? BackgroundCompactionThreshold { get; init; }

	/// <summary>
	/// Context utilization ratio (0.0-1.0) at which the session blocks until compaction completes.
	/// Default: 0.95 (95%).
	/// </summary>
	public double? BufferExhaustionThreshold { get; init; }
}
