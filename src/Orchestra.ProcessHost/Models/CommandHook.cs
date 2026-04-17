namespace Orchestra.ProcessHost;

/// <summary>
/// A one-shot command that runs at a specific lifecycle point (before startup or after shutdown).
/// </summary>
public class CommandHook : ServiceEntry
{
	/// <summary>
	/// When to run this command in the Orchestra lifecycle.
	/// </summary>
	public HookPhase RunAt { get; init; } = HookPhase.BeforeStart;

	/// <summary>
	/// Maximum time in seconds to wait for the command to complete.
	/// </summary>
	public int TimeoutSeconds { get; init; } = 60;

	/// <summary>
	/// If true (default), a non-zero exit code aborts startup (only applies to <see cref="HookPhase.BeforeStart"/>).
	/// <see cref="HookPhase.AfterStop"/> commands never block shutdown regardless of this setting.
	/// </summary>
	public bool Required { get; init; } = true;
}
