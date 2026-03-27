namespace Orchestra.Engine;

/// <summary>
/// A step that executes an external command-line process.
/// The standard output is captured as the step output.
/// Supports templated command, arguments, working directory, and environment variables
/// with {{stepName.output}} and {{param.name}} syntax.
/// </summary>
public class CommandOrchestrationStep : OrchestrationStep
{
	/// <summary>
	/// The command (executable) to run (e.g., "dotnet", "python", "node", "git").
	/// Supports template expressions.
	/// </summary>
	public required string Command { get; init; }

	/// <summary>
	/// Optional arguments to pass to the command.
	/// Each argument supports template expressions and is passed as a separate argument to the process.
	/// </summary>
	public string[] Arguments { get; init; } = [];

	/// <summary>
	/// Optional working directory for the process.
	/// Supports template expressions. When null, uses the current directory.
	/// </summary>
	public string? WorkingDirectory { get; init; }

	/// <summary>
	/// Optional environment variables to set for the process.
	/// Values support template expressions.
	/// </summary>
	public Dictionary<string, string> Environment { get; init; } = [];

	/// <summary>
	/// Whether to include stderr in the output when the process succeeds.
	/// When false (default), only stdout is captured as the step output.
	/// When true, stderr is appended after stdout.
	/// </summary>
	public bool IncludeStdErr { get; init; }
}
