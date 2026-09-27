using System.Text.Json.Nodes;

namespace Orchestra.Watchers.Models;

/// <summary>
/// Settings shared by both fetcher kinds.
/// </summary>
public abstract class WatcherFetchConfigBase
{
	/// <summary>Arguments passed to the command, or appended after the script path.</summary>
	public string[] Arguments { get; init; } = [];

	/// <summary>
	/// Working directory for the child process. Relative paths resolve against
	/// <see cref="WatcherEntry.BaseDirectory"/>; <see langword="null"/> uses that directory itself.
	/// </summary>
	public string? WorkingDirectory { get; init; }

	/// <summary>Environment variables merged on top of the host environment.</summary>
	public Dictionary<string, string>? Env { get; init; }

	/// <summary>Maximum wall-clock time for one fetch. The process is killed on expiry.</summary>
	public int TimeoutSeconds { get; init; } = 60;

	/// <summary>
	/// When <see langword="true"/>, stderr is merged into stdout before JSON parsing. Stderr is
	/// always captured and logged on failure regardless of this flag.
	/// </summary>
	public bool IncludeStdErr { get; init; }

	/// <summary>Free-form object passed verbatim to the fetcher as the stdin <c>config</c> field.</summary>
	public JsonObject? Config { get; init; }
}

/// <summary>
/// Fetch settings for <see cref="PollCommandWatcher"/>: run an executable.
/// </summary>
public sealed class WatcherFetchCommandConfig : WatcherFetchConfigBase
{
	/// <summary>Executable name (resolved via PATH) or absolute path.</summary>
	public required string Command { get; init; }
}

/// <summary>
/// Fetch settings for <see cref="PollScriptWatcher"/>: run a script through an interpreter.
/// Exactly one of <see cref="ScriptFile"/> or <see cref="Script"/> must be set.
/// </summary>
public sealed class WatcherFetchScriptConfig : WatcherFetchConfigBase
{
	/// <summary>Interpreter: pwsh, powershell, bash, sh, python, python3, node, or any executable taking a script path first.</summary>
	public required string Shell { get; init; }

	/// <summary>Script path. Relative paths resolve against <see cref="WatcherEntry.BaseDirectory"/>.</summary>
	public string? ScriptFile { get; init; }

	/// <summary>Inline script body, written to a temporary file for the interpreter.</summary>
	public string? Script { get; init; }
}
