namespace Orchestra.Engine;

/// <summary>
/// Describes the operating-system process a Script or Command step spawned, reported the moment
/// the process starts so live viewers can tell WHAT a long-running step is actually executing
/// (and where), instead of seeing only "step started" for the whole duration.
/// </summary>
/// <remarks>
/// <para>Everything here is observability data about the run's own child process. The resolved
/// command line is the same information the step trace exposes after completion; surfacing it
/// while the step is still running lets an operator correlate the step with the process tree
/// (e.g. a shell whose child <c>az</c> call is stuck) and locate the inline script file the
/// engine materialised, which is deleted again when the step ends.</para>
/// </remarks>
public sealed record StepProcessInfo
{
	/// <summary>Operating-system process id of the spawned process.</summary>
	public required int ProcessId { get; init; }

	/// <summary>Executable that was started (e.g. <c>pwsh</c>, <c>cmd.exe</c>, <c>/bin/sh</c>).</summary>
	public required string FileName { get; init; }

	/// <summary>
	/// Arguments as passed to the executable, joined for display. For Script steps this includes the
	/// interpreter flags and the script path; for Command steps it is the shell wrapper plus the
	/// resolved command line.
	/// </summary>
	public required string Arguments { get; init; }

	/// <summary>Working directory the process was started in, or <c>null</c> for the engine's current directory.</summary>
	public string? WorkingDirectory { get; init; }

	/// <summary>
	/// Script file the interpreter is executing (Script steps only). For inline scripts this is the
	/// temporary <c>orchestra-&lt;guid&gt;.&lt;ext&gt;</c> file the engine wrote, which exists only while
	/// the step runs; for <c>scriptFile</c> steps it is the resolved user path.
	/// </summary>
	public string? ScriptPath { get; init; }

	/// <summary>When the process was started.</summary>
	public required DateTimeOffset StartedAt { get; init; }
}
