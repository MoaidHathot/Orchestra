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
	/// Controls how the executor wraps the resolved script with an error-handling prologue
	/// before launching the interpreter.
	/// </summary>
	/// <remarks>
	/// <para>For PowerShell shells (<c>pwsh</c>, <c>powershell</c>), the prologue is injected
	/// at the first valid statement-level position (after any <c>#requires</c>, <c>using</c>,
	/// attribute, or <c>param(...)</c> block). Non-PowerShell shells are unaffected.</para>
	/// <list type="table">
	///   <listheader><term>Value</term><description>Effect on PowerShell scripts</description></listheader>
	///   <item>
	///     <term><c>null</c> (default)</term>
	///     <description>Injects <c>$ErrorActionPreference='Stop'; trap { Write-Error -ErrorRecord $_; exit 1 };</c>.
	///     Promotes non-terminating errors to terminating ones and ensures any unhandled error
	///     causes pwsh to exit non-zero (so the step is reported <see cref="ExecutionStatus.Failed"/>).
	///     Does NOT enable <c>Set-StrictMode</c>, so idiomatic <c>$obj.MaybeMissingProperty</c>
	///     reads on <c>ConvertFrom-Json</c> output continue to return <c>$null</c>.</description>
	///   </item>
	///   <item>
	///     <term><c>true</c></term>
	///     <description>Injects the default prologue PLUS <c>Set-StrictMode -Version Latest</c>.
	///     Use this for scripts written with strict-mode discipline (i.e., that already use
	///     strict-safe property access such as <c>$obj.PSObject.Properties['Name']?.Value</c>
	///     and explicit array bounds checks). Catches uninitialized variables, missing
	///     properties, and out-of-bounds indexing.</description>
	///   </item>
	///   <item>
	///     <term><c>false</c></term>
	///     <description>No prologue is injected. The script runs verbatim with PowerShell's
	///     default <c>$ErrorActionPreference='Continue'</c>. Use this only for scripts that
	///     intentionally write to stderr but expect a zero exit code, or that explicitly
	///     manage their own preference settings.</description>
	///   </item>
	/// </list>
	/// </remarks>
	public bool? StrictMode { get; init; }
}
