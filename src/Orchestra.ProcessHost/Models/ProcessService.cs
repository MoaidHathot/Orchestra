namespace Orchestra.ProcessHost;

/// <summary>
/// A long-running process that is started with Orchestra and kept alive until shutdown.
/// Supports configurable restart policies and readiness detection.
/// </summary>
public class ProcessService : ServiceEntry
{
	/// <summary>
	/// What to do when the process exits unexpectedly.
	/// </summary>
	public RestartPolicy RestartPolicy { get; init; } = RestartPolicy.Never;

	/// <summary>
	/// Optional readiness check configuration. If set, the <see cref="ServiceManager"/>
	/// will wait for the process to signal readiness before continuing startup.
	/// </summary>
	public ReadinessCheck? Readiness { get; init; }

	/// <summary>
	/// Grace period in seconds before force-killing the process during shutdown.
	/// </summary>
	public int ShutdownTimeoutSeconds { get; init; } = 10;

	/// <summary>
	/// If true, a readiness timeout will cause Orchestra startup to fail.
	/// If false (default), a warning is logged and startup continues.
	/// </summary>
	public bool Required { get; init; }
}
