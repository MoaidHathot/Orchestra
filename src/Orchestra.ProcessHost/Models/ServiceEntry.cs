namespace Orchestra.ProcessHost;

/// <summary>
/// Base class for all service entries managed by the <see cref="ServiceManager"/>.
/// A service entry represents either a long-running process or a one-shot command hook.
/// </summary>
public abstract class ServiceEntry
{
	/// <summary>
	/// Unique name identifying this service.
	/// </summary>
	public required string Name { get; init; }

	/// <summary>
	/// The executable or command to run.
	/// </summary>
	public required string Command { get; init; }

	/// <summary>
	/// Command-line arguments for the process.
	/// </summary>
	public string[] Arguments { get; init; } = [];

	/// <summary>
	/// Working directory for the process. Defaults to the current directory if not set.
	/// </summary>
	public string? WorkingDirectory { get; init; }

	/// <summary>
	/// Additional environment variables to set for the process.
	/// </summary>
	public Dictionary<string, string>? Env { get; init; }
}
