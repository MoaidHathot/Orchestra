namespace Orchestra.Engine;

/// <summary>
/// A step that executes an inline or file-based script using a specified shell interpreter.
/// The standard output is captured as the step output.
/// Supports templated script content, arguments, working directory, and environment variables
/// with {{stepName.output}} and {{param.name}} syntax.
/// </summary>
public class ScriptOrchestrationStep : OrchestrationStep
{
	/// <summary>
	/// The shell interpreter to use (e.g., "pwsh", "bash", "python", "node").
	/// This is required and determines both the executable used to run the script
	/// and the file extension for temporary script files.
	/// </summary>
	public required string Shell { get; init; }

	/// <summary>
	/// Inline script content to execute.
	/// Mutually exclusive with <see cref="ScriptFile"/>.
	/// Supports template expressions.
	/// </summary>
	public string? Script { get; init; }

	/// <summary>
	/// Path to an external script file to execute.
	/// Relative paths are resolved from the orchestration file's directory.
	/// Mutually exclusive with <see cref="Script"/>.
	/// Supports template expressions.
	/// </summary>
	public string? ScriptFile { get; init; }

	/// <summary>
	/// Optional arguments to pass to the script.
	/// Each argument supports template expressions and is passed after the script file path.
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

	/// <summary>
	/// Optional content to pipe to the process's standard input.
	/// Supports template expressions (e.g., {{stepName.output}}).
	/// Use this instead of passing large outputs as command-line arguments,
	/// which can exceed OS command-line length limits.
	/// </summary>
	public string? Stdin { get; init; }

	/// <summary>
	/// Controls whether the executor injects a strict-mode prologue at the top
	/// of the resolved script before launching the interpreter.
	/// </summary>
	/// <remarks>
	/// <para>When <c>null</c> (the default), the executor opts in automatically for
	/// PowerShell shells (<c>pwsh</c>, <c>powershell</c>) and opts out for every
	/// other shell.</para>
	/// <para>For PowerShell the injected prologue is a single line equivalent to:
	/// <c>$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest; trap { Write-Error -ErrorRecord $_; exit 1 };</c>
	/// This converts non-terminating runtime errors and unbound-variable / missing-property
	/// access into terminating errors that propagate as a non-zero exit code, so the
	/// step is reported <see cref="ExecutionStatus.Failed"/> instead of silently
	/// succeeding with empty stdout.</para>
	/// <para>Set this explicitly to <c>false</c> to opt a single step out of the
	/// prologue (for example, when the script intentionally writes to stderr while
	/// expecting a zero exit code, or when <see cref="Set-StrictMode"/> would break
	/// existing logic). Set to <c>true</c> to force injection on non-PowerShell shells
	/// (currently a no-op — only PowerShell has a defined prologue).</para>
	/// </remarks>
	public bool? StrictMode { get; init; }
}
