namespace Orchestra.ProcessHost;

/// <summary>
/// Restart policy for managed processes.
/// </summary>
public enum RestartPolicy
{
	/// <summary>
	/// Do not restart the process if it exits.
	/// </summary>
	Never,

	/// <summary>
	/// Restart the process only if it exits with a non-zero exit code.
	/// </summary>
	OnFailure,

	/// <summary>
	/// Always restart the process if it exits, regardless of exit code.
	/// </summary>
	Always,
}

/// <summary>
/// Lifecycle phase at which a command hook runs.
/// </summary>
public enum HookPhase
{
	/// <summary>
	/// Run before Orchestra starts (before MCP proxy, orchestrations, triggers).
	/// </summary>
	BeforeStart,

	/// <summary>
	/// Run after Orchestra has fully stopped (after all processes and MCP proxy are stopped).
	/// </summary>
	AfterStop,
}

/// <summary>
/// Runtime state of a managed process.
/// </summary>
public enum ProcessState
{
	/// <summary>
	/// The process has not been started yet.
	/// </summary>
	Pending,

	/// <summary>
	/// The process is starting up and waiting for readiness.
	/// </summary>
	Starting,

	/// <summary>
	/// The process passed its readiness check and is running.
	/// </summary>
	Ready,

	/// <summary>
	/// The process is running (no readiness check configured).
	/// </summary>
	Running,

	/// <summary>
	/// The process is being stopped gracefully.
	/// </summary>
	Stopping,

	/// <summary>
	/// The process has stopped normally.
	/// </summary>
	Stopped,

	/// <summary>
	/// The process exited with an error or crashed.
	/// </summary>
	Failed,
}
