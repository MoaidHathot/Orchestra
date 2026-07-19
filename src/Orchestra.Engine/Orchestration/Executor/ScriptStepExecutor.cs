using System.Collections.Frozen;
using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Orchestra.Engine;

/// <summary>
/// Executes script steps by writing inline scripts to temporary files and launching
/// the appropriate shell interpreter. Supports multiple shells (pwsh, bash, python, etc.)
/// via a dispatch table with best-effort fallback for unknown shells.
/// </summary>
public sealed partial class ScriptStepExecutor : IStepExecutor
{
	private readonly IOrchestrationReporter _reporter;
	private readonly ILogger<ScriptStepExecutor> _logger;

	/// <summary>
	/// Configuration for a known shell interpreter.
	/// </summary>
	private sealed record ShellConfig(string Executable, string FileExtension, string[] RunFileArgs);

	/// <summary>
	/// Dispatch table mapping shell names to their configuration.
	/// Unknown shells fall back to best-effort execution.
	/// </summary>
	private static readonly FrozenDictionary<string, ShellConfig> s_shellConfigs = new Dictionary<string, ShellConfig>(StringComparer.OrdinalIgnoreCase)
	{
		["pwsh"] = new("pwsh", ".ps1", ["-NoProfile", "-File"]),
		["powershell"] = new("powershell", ".ps1", ["-NoProfile", "-File"]),
		["bash"] = new("bash", ".sh", []),
		["sh"] = new("sh", ".sh", []),
		["python"] = new("python", ".py", []),
		["python3"] = new("python3", ".py", []),
		["node"] = new("node", ".js", []),
	}.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

	/// <summary>
	/// Shells that opt in to strict-mode prologue injection by default.
	/// </summary>
	private static readonly FrozenSet<string> s_strictByDefaultShells = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
	{
		"pwsh",
		"powershell",
	}.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

	/// <summary>
	/// UTF-8 without a byte-order mark, used to decode child-process stdout/stderr. A shared
	/// instance because <see cref="System.Text.Encoding"/> is immutable and thread-safe. Emitting
	/// no BOM keeps the captured text byte-clean so downstream JSON parsing does not trip over a
	/// leading U+FEFF.
	/// </summary>
	private static readonly Encoding s_utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

	/// <summary>
	/// Marker prefix used to indicate that an argument has been spilled to a file
	/// due to size constraints (Windows CreateProcess ~32K limit). The value after
	/// the prefix is the absolute path to a file containing the original argument
	/// text (UTF-8, no BOM). PowerShell scripts have this resolved transparently
	/// via the injected prologue.
	/// </summary>
	internal const string OrchestraFileMarker = "@orchestra-file:";

	/// <summary>
	/// Conservative per-process command-line length budget. Windows
	/// <c>CreateProcessW</c> caps the command line at 32,767 chars; we leave
	/// headroom for the executable path, run-file args, env block, and quoting.
	/// When the total resolved-args length exceeds this budget, any single arg
	/// larger than <see cref="ArgSpillSingleArgThreshold"/> is written to a
	/// temp file and replaced with an <see cref="OrchestraFileMarker"/> token.
	/// </summary>
	internal const int ArgSpillTotalThreshold = 8_000;

	/// <summary>
	/// Per-argument spill threshold. An individual arg larger than this value is
	/// eligible to be replaced with an <see cref="OrchestraFileMarker"/> token
	/// once the total length exceeds <see cref="ArgSpillTotalThreshold"/>.
	/// </summary>
	internal const int ArgSpillSingleArgThreshold = 2_000;

	/// <summary>
	/// PowerShell snippet that rewrites <c>$args</c> by resolving any
	/// <see cref="OrchestraFileMarker"/> tokens to the original file contents.
	/// Kept on a single line to avoid disturbing user-script line numbers.
	/// </summary>
	private const string PowerShellArgSpillResolver =
		"if ($args) { $args = @($args | ForEach-Object { if ($_ -is [string] -and $_.StartsWith('" + OrchestraFileMarker + "')) { [System.IO.File]::ReadAllText($_.Substring(" + "16" + ")) } else { $_ } }) };";

	/// <summary>
	/// PowerShell prologue injected at the top of the resolved script for the default
	/// (auto) <see cref="ScriptOrchestrationStep.StrictMode"/> setting.
	/// </summary>
	/// <remarks>
	/// <para>Kept to a single physical line so the user's script preserves its original line numbers
	/// in runtime error messages.</para>
	/// <para>This prologue promotes non-terminating PowerShell errors to terminating ones via
	/// <c>$ErrorActionPreference='Stop'</c> and ensures any unhandled error causes pwsh to exit
	/// with a non-zero code. It deliberately does NOT include <c>Set-StrictMode -Version Latest</c>
	/// because most production scripts read optional properties off <c>ConvertFrom-Json</c> output
	/// (a pattern that throws under strict mode v3+). Authors who want the additional strict-mode
	/// checks set <c>strictMode: true</c> on the step.</para>
	/// <para><b>Diagnostic output.</b> When the trap fires the prologue writes a structured,
	/// machine-parseable line to stderr in the form
	/// <c>ORCHESTRA-PWSH-ERROR: &lt;script&gt;:&lt;line&gt;:&lt;col&gt;: &lt;message&gt;</c> followed
	/// by the source line and the PowerShell script stack trace. This is intentionally written
	/// directly to <c>[Console]::Error</c> rather than via <c>Write-Error -ErrorRecord</c> so that:
	/// (a) the location pointer reflects the actual failing line in the user's script, not the trap
	/// statement on line 1; (b) the standard pwsh error renderer does not truncate long source lines
	/// with <c>U+2026</c> ellipses that captured stderr cannot represent reliably; and (c) downstream
	/// log consumers can grep for <c>ORCHESTRA-PWSH-ERROR:</c> to extract structured failure data.</para>
	/// </remarks>
	internal const string PowerShellDefaultPrologue =
		"$ErrorActionPreference='Stop'; " + PowerShellArgSpillResolver + " trap { $r=$_; [Console]::Error.WriteLine(\"ORCHESTRA-PWSH-ERROR: $($r.InvocationInfo.ScriptName):$($r.InvocationInfo.ScriptLineNumber):$($r.InvocationInfo.OffsetInLine): $($r.Exception.Message)\"); if ($r.InvocationInfo.Line) { [Console]::Error.WriteLine('  | ' + $r.InvocationInfo.Line.TrimEnd()) }; if ($r.ScriptStackTrace) { [Console]::Error.WriteLine($r.ScriptStackTrace) }; exit 1 };";

	/// <summary>
	/// PowerShell prologue injected when the step opts in via <c>strictMode: true</c>.
	/// Adds <c>Set-StrictMode -Version Latest</c> on top of the default prologue.
	/// </summary>
	/// <remarks>
	/// <para>Set-StrictMode-Version-Latest enforces (in addition to the default prologue):</para>
	/// <list type="bullet">
	///   <item>Uninitialized variable references throw.</item>
	///   <item>Reading a property that does not exist on an object throws.</item>
	///   <item>Out-of-bounds array indexing throws.</item>
	///   <item>Calling a function as if it were a method (with parentheses) throws.</item>
	/// </list>
	/// <para>Useful for new scripts written with strict-mode discipline in mind. Existing scripts
	/// that rely on <c>$obj.MissingProperty</c> returning <c>$null</c> must use a strict-safe accessor
	/// (e.g., <c>$obj.PSObject.Properties['Name']?.Value</c>) before enabling this.</para>
	/// </remarks>
	internal const string PowerShellStrictPrologue =
		"$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest; " + PowerShellArgSpillResolver + " trap { $r=$_; [Console]::Error.WriteLine(\"ORCHESTRA-PWSH-ERROR: $($r.InvocationInfo.ScriptName):$($r.InvocationInfo.ScriptLineNumber):$($r.InvocationInfo.OffsetInLine): $($r.Exception.Message)\"); if ($r.InvocationInfo.Line) { [Console]::Error.WriteLine('  | ' + $r.InvocationInfo.Line.TrimEnd()) }; if ($r.ScriptStackTrace) { [Console]::Error.WriteLine($r.ScriptStackTrace) }; exit 1 };";

	/// <summary>
	/// Environment variable naming the engine-owned control file a Script step may write to
	/// request early orchestration completion or a terminal status (the non-LLM equivalent of
	/// the <c>orchestra_complete</c> / <c>orchestra_set_status</c> engine tools).
	/// </summary>
	internal const string ControlFileEnvVar = "ORCHESTRA_CONTROL_FILE";

	/// <summary>
	/// Upper bound on the control file we will read back. The payload is a tiny JSON object; a
	/// larger file indicates a misdirected write, so we refuse it rather than read unbounded data.
	/// </summary>
	internal const int ControlFileMaxBytes = 64 * 1024;

	/// <summary>
	/// PowerShell helper functions injected for every pwsh/powershell Script step (independent of
	/// <see cref="ScriptOrchestrationStep.StrictMode"/>) so authors can signal control without
	/// hand-writing JSON: <c>Orchestra-Complete -Status success|failed [-Reason ...]</c> and
	/// <c>Orchestra-SetStatus -Status success|failed|no_action [-Reason ...]</c>. Each writes the
	/// validated payload to <see cref="ControlFileEnvVar"/>. Kept on a single physical line so the
	/// user's script preserves its original line numbers in runtime error messages.
	/// </summary>
	internal const string PowerShellControlHelpers =
		"function Orchestra-Complete { param([Parameter(Mandatory)][ValidateSet('success','failed')][string]$Status,[string]$Reason='') if (-not $env:ORCHESTRA_CONTROL_FILE) { throw 'ORCHESTRA_CONTROL_FILE is not set; Orchestra control helpers only work inside an Orchestra Script step.' }; (@{ action='complete'; status=$Status; reason=$Reason } | ConvertTo-Json -Compress) | Set-Content -LiteralPath $env:ORCHESTRA_CONTROL_FILE -NoNewline -Encoding utf8 }; function Orchestra-SetStatus { param([Parameter(Mandatory)][ValidateSet('success','failed','no_action')][string]$Status,[string]$Reason='') if (-not $env:ORCHESTRA_CONTROL_FILE) { throw 'ORCHESTRA_CONTROL_FILE is not set; Orchestra control helpers only work inside an Orchestra Script step.' }; (@{ action='set_status'; status=$Status; reason=$Reason } | ConvertTo-Json -Compress) | Set-Content -LiteralPath $env:ORCHESTRA_CONTROL_FILE -NoNewline -Encoding utf8 };";

	/// <summary>
	/// PowerShell statement injected ahead of every pwsh/powershell prologue (independent of
	/// <see cref="ScriptOrchestrationStep.StrictMode"/>) that forces the child's output streams to
	/// UTF-8 without a BOM.
	/// </summary>
	/// <remarks>
	/// <para>When a pwsh child's stdout is redirected, PowerShell encodes success-stream text using
	/// <c>[Console]::OutputEncoding</c>, which on Windows defaults to the OEM console code page
	/// (e.g. CP437/CP850). That code page cannot represent most non-ASCII characters, so its
	/// best-fit fallback transliterates them — turning curly quotes <c>U+201C</c>/<c>U+201D</c>
	/// into a plain ASCII <c>"</c>. Because a JSON serializer only escapes ASCII quotes, those
	/// smart quotes were emitted unescaped and any downstream <c>ConvertFrom-Json</c> failed with
	/// an "unexpected character" error. Pinning both <c>[Console]::OutputEncoding</c> (stdout/stderr
	/// writers) and <c>$OutputEncoding</c> (native-command pipe) to UTF-8 keeps the bytes lossless;
	/// the host-side <c>StandardOutputEncoding</c>/<c>StandardErrorEncoding</c> read them back the
	/// same way.</para>
	/// <para>Wrapped in <c>try/catch</c> and placed before <c>$ErrorActionPreference='Stop'</c> so a
	/// rare headless environment that rejects the assignment degrades gracefully instead of failing
	/// the step. The assignment itself emits nothing to the success stream. Kept on a single
	/// physical line so the user's script preserves its original line numbers.</para>
	/// </remarks>
	internal const string PowerShellOutputEncodingPrologue =
		"try { [Console]::OutputEncoding = $OutputEncoding = [System.Text.UTF8Encoding]::new($false) } catch { };";

	public ScriptStepExecutor(
		IOrchestrationReporter reporter,
		ILogger<ScriptStepExecutor> logger)
	{
		_reporter = reporter;
		_logger = logger;
	}

	public OrchestrationStepType StepType => OrchestrationStepType.Script;

	public async Task<ExecutionResult> ExecuteAsync(
		OrchestrationStep step,
		OrchestrationExecutionContext context,
		CancellationToken cancellationToken = default)
	{
		if (step is not ScriptOrchestrationStep scriptStep)
			throw new InvalidOperationException(
				$"ScriptStepExecutor received a step of type '{step.GetType().Name}' " +
				$"but expected '{nameof(ScriptOrchestrationStep)}'.");

		var rawDependencyOutputs = context.GetRawDependencyOutputs(step.DependsOn);
		string? tempScriptPath = null;
		string? controlFilePath = null;

		try
		{
			// Look up shell configuration
			var shell = scriptStep.Shell;
			var config = s_shellConfigs.GetValueOrDefault(shell);

			// For unknown shells, use best-effort: executable = shell name, extension = .tmp, no special args
			var executable = config?.Executable ?? shell;
			var fileExtension = config?.FileExtension ?? ".tmp";
			var runFileArgs = config?.RunFileArgs ?? [];

			// Resolve template expressions in script content or script file path
			string scriptFilePath;

			if (scriptStep.Script is not null)
			{
				// Inline script: resolve templates, optionally prepend a strict-mode prologue,
				// then write to a temp file.
				var resolvedScript = TemplateResolver.Resolve(scriptStep.Script, context.Parameters, context, step.DependsOn, step);

				if (BuildPowerShellPreamble(shell, scriptStep.StrictMode) is { } preamble)
				{
					resolvedScript = InjectPowerShellPrologue(resolvedScript, preamble);
					LogStrictPrologueInjected(step.Name, shell);
				}

				tempScriptPath = Path.Combine(Path.GetTempPath(), $"orchestra-{Guid.NewGuid():N}{fileExtension}");
				await File.WriteAllTextAsync(tempScriptPath, resolvedScript, cancellationToken);
				scriptFilePath = tempScriptPath;
			}
			else if (scriptStep.ScriptFile is not null)
			{
				// External script file: resolve templates in the path
				scriptFilePath = TemplateResolver.Resolve(scriptStep.ScriptFile, context.Parameters, context, step.DependsOn, step);

				if (!File.Exists(scriptFilePath))
				{
					var errorMessage = $"Script file not found: '{scriptFilePath}'";
					LogScriptFileNotFound(step.Name, scriptFilePath);
					_reporter.ReportStepError(step.Name, errorMessage);
					return ExecutionResult.Failed(errorMessage, rawDependencyOutputs);
				}
			}
			else
			{
				// Should not happen — parser validates this
				var errorMessage = "Script step requires either 'script' (inline) or 'scriptFile' (path).";
				_reporter.ReportStepError(step.Name, errorMessage);
				return ExecutionResult.Failed(errorMessage, rawDependencyOutputs);
			}

			// Resolve template expressions in arguments
			var resolvedArguments = scriptStep.Arguments
				.Select(arg => TemplateResolver.Resolve(arg, context.Parameters, context, step.DependsOn, step))
				.ToArray();

			// Spill oversize arguments to files to avoid the Windows CreateProcess
			// command-line length limit (~32,767 chars). When an upstream step's
			// dependency output is large (e.g., aggregated meeting data), passing it
			// inline as a single positional argument would fail with
			// "The filename or extension is too long" (ERROR_FILENAME_EXCED_RANGE).
			// We replace each oversize arg with the marker token "@orchestra-file:<path>".
			// For pwsh/powershell, the injected prologue auto-resolves the marker so
			// $args[N] retains the original string value. For other shells, authors
			// must read the file themselves (or use the ORCHESTRA_ARGS_FILE env var
			// which points to a JSON manifest with the fully-resolved args array).
			var (arguments, argSpillEnv) = SpillOversizeArguments(
				resolvedArguments,
				context.TempFileStore,
				step.Name);

			var processArguments = runFileArgs
				.Append(scriptFilePath)
				.Concat(arguments)
				.ToArray();

			// Resolve template expressions in working directory
			string? workingDirectory = null;
			if (scriptStep.WorkingDirectory is not null)
			{
				workingDirectory = TemplateResolver.Resolve(scriptStep.WorkingDirectory, context.Parameters, context, step.DependsOn, step);
			}

			// Resolve template expressions in stdin content
			string? resolvedStdin = null;
			if (scriptStep.Stdin is not null)
			{
				resolvedStdin = TemplateResolver.Resolve(scriptStep.Stdin, context.Parameters, context, step.DependsOn, step);
			}

			// Build process start info — invoke the shell directly (no cmd.exe /c wrapper)
			var startInfo = new ProcessStartInfo
			{
				FileName = executable,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				RedirectStandardInput = resolvedStdin is not null,
				UseShellExecute = false,
				CreateNoWindow = true,
				// Decode the child's stdout/stderr as UTF-8 (no BOM). Without this .NET falls
				// back to Console.OutputEncoding, which on Windows is the OEM console code page
				// (e.g. CP437/CP850). That best-fit mapping silently transliterates non-ASCII
				// characters — most destructively, curly double-quotes U+201C/U+201D collapse to
				// an unescaped ASCII '"', corrupting any JSON a step emits (see the paired
				// child-side PowerShellOutputEncodingPrologue that makes pwsh WRITE UTF-8).
				StandardOutputEncoding = s_utf8NoBom,
				StandardErrorEncoding = s_utf8NoBom,
			};

			// Add run-file args (e.g., -NoProfile -File for pwsh)
			foreach (var arg in runFileArgs)
				startInfo.ArgumentList.Add(arg);

			// Add the script file path
			startInfo.ArgumentList.Add(scriptFilePath);

			// Add user-provided arguments
			foreach (var arg in arguments)
				startInfo.ArgumentList.Add(arg);

			// Set working directory
			if (workingDirectory is not null)
			{
				startInfo.WorkingDirectory = workingDirectory;
			}

			// Suppress ANSI color escapes by default. Many shells/tools (PowerShell 7's
			// ConciseView error formatter, git, gh, npm) honor NO_COLOR=1; TERM=dumb covers
			// the few that don't. These are set BEFORE the user's environment loop so an
			// orchestration author can still override them by declaring NO_COLOR / TERM in
			// the step's Environment section if they truly want raw ANSI bytes.
			startInfo.Environment["NO_COLOR"] = "1";
			startInfo.Environment["TERM"] = "dumb";

			// Surface arg-spill env vars before user-provided env so authors can override
			// them if they really want to. ORCHESTRA_ARGS_FILE points to a JSON manifest
			// containing the fully-resolved arguments array (after spill resolution).
			foreach (var (key, value) in argSpillEnv)
			{
				startInfo.Environment[key] = value;
			}

			// Set environment variables (resolve templates in values)
			var resolvedEnvironment = new Dictionary<string, string>(StringComparer.Ordinal);
			foreach (var (key, value) in scriptStep.Environment)
			{
				var resolvedValue = TemplateResolver.Resolve(value, context.Parameters, context, step.DependsOn, step);
				resolvedEnvironment[key] = resolvedValue;
				startInfo.Environment[key] = resolvedValue;
			}

			// Control channel: hand the script a private file it can write to request early
			// orchestration completion or a terminal status — the non-LLM equivalent of the
			// orchestra_complete / orchestra_set_status engine tools. The file is engine-owned
			// (created here, read after exit, deleted in finally). Set AFTER the user environment
			// loop so an author cannot accidentally redirect the engine's control path. The
			// injected pwsh helpers (Orchestra-Complete / Orchestra-SetStatus) and the
			// `orchestra step` CLI both write to this path; any shell can also write the JSON
			// directly. ORCHESTRA_RUN_ID / ORCHESTRA_STEP_NAME are provided for diagnostics and
			// future transports.
			controlFilePath = Path.Combine(Path.GetTempPath(), $"orchestra-control-{Guid.NewGuid():N}.json");
			startInfo.Environment[ControlFileEnvVar] = controlFilePath;
			startInfo.Environment["ORCHESTRA_RUN_ID"] = context.OrchestrationInfo.RunId;
			startInfo.Environment["ORCHESTRA_STEP_NAME"] = step.Name;

			var displayArgs = arguments.Length > 0
				? " " + string.Join(' ', arguments.Select(a => a.Contains(' ') ? $"\"{a}\"" : a))
				: string.Empty;

			LogScriptStart(step.Name, shell, scriptStep.Script is not null ? "(inline)" : scriptFilePath, displayArgs);

			// Start the process
			using var process = new Process { StartInfo = startInfo };

			var stdoutBuilder = new StringBuilder();
			var stderrBuilder = new StringBuilder();

			process.OutputDataReceived += (_, e) =>
			{
				if (e.Data is not null)
				{
					// Defensive: strip ANSI escape sequences for tools that ignore NO_COLOR.
					// AnsiSanitizer.Strip is a no-op fast-path when no ESC byte is present.
					var line = AnsiSanitizer.Strip(e.Data) ?? string.Empty;
					stdoutBuilder.AppendLine(line);
					_reporter.ReportContentDelta(step.Name, line + Environment.NewLine);
				}
			};

			process.ErrorDataReceived += (_, e) =>
			{
				if (e.Data is not null)
				{
					var line = AnsiSanitizer.Strip(e.Data) ?? string.Empty;
					stderrBuilder.AppendLine(line);
					if (scriptStep.IncludeStdErr)
						_reporter.ReportContentDelta(step.Name, line + Environment.NewLine);
				}
			};

			if (!process.Start())
			{
				var errorMessage = $"Failed to start shell '{executable}'";
				LogScriptStartFailed(step.Name, executable);
				_reporter.ReportStepError(step.Name, errorMessage);
				return ExecutionResult.Failed(errorMessage, rawDependencyOutputs);
			}

			process.BeginOutputReadLine();
			process.BeginErrorReadLine();

			// Write stdin content if provided, then close the stream
			if (resolvedStdin is not null)
			{
				await process.StandardInput.WriteAsync(resolvedStdin);
				process.StandardInput.Close();
			}

			// Wait for process to exit with cancellation support.
			//
			// On cancellation (orchestration cancel, step timeout, host shutdown,
			// orchestration timeout) the child process is still alive and (a) holds
			// the temp script file open so the `File.Delete` in `finally` would
			// fail with a sharing violation, and (b) would continue running as an
			// orphan after this method returns — re-creating the very leak this
			// fix exists to prevent. Force-kill the entire descendant tree
			// (e.g., pwsh -> cmd.exe -> az.cmd) and wait briefly for the OS to
			// settle the file handles before re-throwing the cancellation.
			try
			{
				await process.WaitForExitAsync(cancellationToken);
			}
			catch (OperationCanceledException)
			{
				TerminateProcessTreeOnCancel(process, step.Name);
				throw;
			}
			process.WaitForExit();

			var stdout = stdoutBuilder.ToString().TrimEnd();
			var stderr = stderrBuilder.ToString().TrimEnd();

			if (process.ExitCode == 0)
			{
				var output = scriptStep.IncludeStdErr && stderr.Length > 0
					? $"{stdout}\n{stderr}"
					: stdout;

				LogScriptSuccess(step.Name, process.ExitCode);

				// Build trace data for viewer visibility
				var trace = BuildTrace(shell, scriptStep.Script is not null ? "(inline)" : scriptFilePath, processArguments, arguments, workingDirectory, resolvedEnvironment, resolvedStdin, stdout, stderr);
				_reporter.ReportStepTrace(step.Name, trace);

				// Control channel: if the script wrote ORCHESTRA_CONTROL_FILE, map the requested
				// action (set_status / complete) onto the result. Only honoured on exit 0 — a
				// failing script cannot request "complete success".
				if (controlFilePath is not null && File.Exists(controlFilePath))
				{
					var controlResult = ApplyControlSignal(controlFilePath, step, context, output, rawDependencyOutputs, trace);
					if (controlResult is not null)
						return controlResult;
				}

				return ExecutionResult.Succeeded(
					output,
					rawDependencyOutputs: rawDependencyOutputs,
					trace: trace);
			}
			else
			{
				var errorMessage = $"Script ({shell}) exited with code {process.ExitCode}";
				if (stderr.Length > 0)
					errorMessage += $": {stderr}";

				LogScriptFailed(step.Name, process.ExitCode, errorMessage);
				_reporter.ReportStepError(step.Name, errorMessage);

				// Build trace data even on failure
				var trace = BuildTrace(shell, scriptStep.Script is not null ? "(inline)" : scriptFilePath, processArguments, arguments, workingDirectory, resolvedEnvironment, resolvedStdin, stdout, stderr);
				_reporter.ReportStepTrace(step.Name, trace);

				return ExecutionResult.Failed(errorMessage, rawDependencyOutputs, trace: trace);
			}
		}
		catch (OperationCanceledException)
		{
			throw; // Let cancellation propagate for timeout handling
		}
		catch (Exception ex)
		{
			var errorMessage = $"Script execution failed: {ex.Message}";
			LogScriptException(step.Name, ex);
			_reporter.ReportStepError(step.Name, errorMessage);
			return ExecutionResult.Failed(errorMessage, rawDependencyOutputs);
		}
		finally
		{
			// Clean up temporary script file
			if (tempScriptPath is not null)
			{
				try
				{
					File.Delete(tempScriptPath);
				}
				catch
				{
					// Best effort cleanup — don't fail the step for this
				}
			}

			// Clean up the engine-owned control file. The signal it carried (if any) has already
			// been read and applied above and, when present, persisted to the run's temp store for
			// history — so the working file itself is safe to delete.
			if (controlFilePath is not null)
			{
				try
				{
					File.Delete(controlFilePath);
				}
				catch
				{
					// Best effort cleanup — don't fail the step for this
				}
			}
		}
	}

	/// <summary>
	/// Returns the PowerShell prologue to inject for the given shell and explicit
	/// <see cref="ScriptOrchestrationStep.StrictMode"/> setting, or <c>null</c> if no prologue should be injected.
	/// </summary>
	/// <remarks>
	/// <para>Decision table (for <c>pwsh</c>/<c>powershell</c> only — every other shell returns <c>null</c>):</para>
	/// <list type="table">
	///   <listheader><term><c>strictMode</c></term><description>Prologue</description></listheader>
	///   <item><term><c>null</c> (auto)</term><description><see cref="PowerShellDefaultPrologue"/> — <c>$ErrorActionPreference='Stop'</c> + <c>trap</c>. Catches the silent-terminating-error class without breaking idiomatic JSON shape handling.</description></item>
	///   <item><term><c>true</c></term><description><see cref="PowerShellStrictPrologue"/> — adds <c>Set-StrictMode -Version Latest</c>.</description></item>
	///   <item><term><c>false</c></term><description><c>null</c> — the user script runs verbatim, with no engine-injected error-handling guardrails.</description></item>
	/// </list>
	/// </remarks>
	internal static string? GetPowerShellPrologue(string shell, bool? explicitOptIn)
	{
		if (!s_strictByDefaultShells.Contains(shell))
			return null;

		return explicitOptIn switch
		{
			true => PowerShellStrictPrologue,
			false => null,
			null => PowerShellDefaultPrologue,
		};
	}

	/// <summary>
	/// Builds the full PowerShell preamble to inject at the top of the user's script: the
	/// always-on <see cref="PowerShellControlHelpers"/> (so <c>Orchestra-Complete</c> /
	/// <c>Orchestra-SetStatus</c> exist regardless of <see cref="ScriptOrchestrationStep.StrictMode"/>),
	/// followed by the strict/default error-handling prologue when one applies. Returns
	/// <c>null</c> for non-PowerShell shells (they get no injection).
	/// </summary>
	internal static string? BuildPowerShellPreamble(string shell, bool? explicitOptIn)
	{
		if (!s_strictByDefaultShells.Contains(shell))
			return null;

		var prologue = GetPowerShellPrologue(shell, explicitOptIn);
		var body = prologue is null
			? PowerShellControlHelpers
			: PowerShellControlHelpers + " " + prologue;

		// Force UTF-8 output first so it applies regardless of the strict-mode setting (including
		// strictMode:false, where GetPowerShellPrologue returns null) and before any user output.
		return PowerShellOutputEncodingPrologue + " " + body;
	}

	/// <summary>
	/// Reads and applies the Script step's control file (<see cref="ControlFileEnvVar"/>) after a
	/// successful (exit 0) run. Returns the mapped <see cref="ExecutionResult"/> when the script
	/// requested a terminal status or early orchestration completion, a failed result when the
	/// file is malformed/oversized, or <c>null</c> when there is nothing to apply (empty file) so
	/// the caller falls back to a normal success. The raw payload is persisted to the run's temp
	/// store so the signal survives in run history.
	/// </summary>
	private ExecutionResult? ApplyControlSignal(
		string controlFilePath,
		OrchestrationStep step,
		OrchestrationExecutionContext context,
		string stdout,
		Dictionary<string, string> rawDependencyOutputs,
		StepExecutionTrace trace)
	{
		string raw;
		try
		{
			var info = new FileInfo(controlFilePath);
			if (info.Length == 0)
				return null;

			if (info.Length > ControlFileMaxBytes)
			{
				var tooBig = $"{ControlFileEnvVar} is {info.Length} bytes (max {ControlFileMaxBytes}); refusing to read an oversized control payload.";
				LogControlFileInvalid(step.Name, tooBig);
				_reporter.ReportStepError(step.Name, tooBig);
				return ExecutionResult.Failed(tooBig, rawDependencyOutputs, trace: trace);
			}

			raw = File.ReadAllText(controlFilePath);
		}
		catch (Exception ex)
		{
			// If we cannot read the file at all, don't punish an otherwise-successful script;
			// log and fall through to a normal success.
			LogControlFileInvalid(step.Name, ex.Message);
			return null;
		}

		if (string.IsNullOrWhiteSpace(raw))
			return null;

		if (!ScriptControlSignal.TryParse(raw, out var signal, out var parseError))
		{
			var msg = $"Invalid {ControlFileEnvVar} contents: {parseError}";
			LogControlFileInvalid(step.Name, msg);
			_reporter.ReportStepError(step.Name, msg);
			return ExecutionResult.Failed(msg, rawDependencyOutputs, trace: trace);
		}

		// Persist the raw payload to run history (best-effort) so the signal is auditable, then
		// collect the step's saved files (which now includes that copy).
		context.TempFileStore?.SaveFile(raw, step.Name, "json");
		var savedFiles = context.TempFileStore?.GetFilesForStep(step.Name);

		var reason = string.IsNullOrWhiteSpace(signal!.Reason)
			? (signal.Action == ScriptControlAction.Complete
				? "Script requested orchestration completion."
				: "Script set step status.")
			: signal.Reason!;

		var statusText = signal.Status switch
		{
			ExecutionStatus.Succeeded => "success",
			ExecutionStatus.Failed => "failed",
			ExecutionStatus.NoAction => "no_action",
			_ => signal.Status.ToString().ToLowerInvariant(),
		};
		_reporter.ReportStepStatusSet(step.Name, statusText, reason);

		if (signal.Action == ScriptControlAction.Complete)
		{
			LogControlComplete(step.Name, statusText, reason);
			return ExecutionResult.Complete(signal.Status, stdout, reason, step.Name, rawDependencyOutputs, trace, savedFiles);
		}

		LogControlSetStatus(step.Name, statusText, reason);
		return signal.Status switch
		{
			ExecutionStatus.Failed => ExecutionResult.Failed(reason, rawDependencyOutputs, trace: trace, savedFiles: savedFiles),
			ExecutionStatus.NoAction => ExecutionResult.NoAction(reason, rawDependencyOutputs, trace: trace, savedFiles: savedFiles),
			// success: preserve the script's stdout as the step output.
			_ => ExecutionResult.Succeeded(stdout, rawDependencyOutputs: rawDependencyOutputs, trace: trace, savedFiles: savedFiles),
		};
	}

	/// <summary>
	/// Injects <paramref name="prologue"/> at the first valid statement-level position in the
	/// user's PowerShell script.
	/// </summary>
	/// <remarks>
	/// <para>PowerShell requires that <c>#requires</c>, <c>using</c>, attribute declarations,
	/// and the <c>param(...)</c> block come before any other statements. Injecting the
	/// prologue at the literal top of the file would invalidate such scripts, so this
	/// method scans past any leading prologue-incompatible elements and inserts the prologue
	/// at the first safe position.</para>
	/// <para>Scanning rules:</para>
	/// <list type="bullet">
	///   <item>Whitespace and line comments are skipped.</item>
	///   <item><c>&lt;# ... #&gt;</c> block comments are skipped.</item>
	///   <item><c>#requires</c> lines are skipped (whole line).</item>
	///   <item><c>using namespace|module|assembly|type ...</c> lines are skipped (whole line).</item>
	///   <item>Attribute declarations like <c>[CmdletBinding(...)]</c> / <c>[Alias(...)]</c> are skipped (matching brackets).</item>
	///   <item><c>param(...)</c> blocks are skipped past their matching close paren.</item>
	/// </list>
	/// <para>If none of these are present at the top, the prologue is injected at offset 0.</para>
	/// </remarks>
	internal static string InjectPowerShellPrologue(string userScript, string prologue)
	{
		var insertOffset = FindPowerShellPrologueInsertOffset(userScript);

		if (insertOffset == 0)
			return prologue + Environment.NewLine + userScript;

		// Place the prologue on its own line. If we landed mid-line (rare), prefix with a newline
		// so we don't fuse with the previous token.
		var needsLeadingNewline = insertOffset > 0 && userScript[insertOffset - 1] != '\n';
		var prefix = needsLeadingNewline ? Environment.NewLine : string.Empty;
		var suffix = Environment.NewLine;

		return userScript[..insertOffset] + prefix + prologue + suffix + userScript[insertOffset..];
	}

	/// <summary>
	/// Scans the script and returns the character offset at which the strict-mode
	/// prologue can be safely inserted (i.e., after any leading <c>#requires</c>,
	/// <c>using</c>, attribute, or <c>param(...)</c> block).
	/// </summary>
	private static int FindPowerShellPrologueInsertOffset(string script)
	{
		var i = 0;
		var len = script.Length;
		var lastSafeOffset = 0;

		while (i < len)
		{
			// Skip whitespace and CRLF.
			while (i < len && char.IsWhiteSpace(script[i]))
				i++;

			if (i >= len)
				break;

			// Line comment: # ... \n  (but not #requires, which is handled below).
			if (script[i] == '#' && !IsRequiresDirective(script, i))
			{
				// Skip to end of line.
				while (i < len && script[i] != '\n')
					i++;

				lastSafeOffset = i;
				continue;
			}

			// Block comment: <# ... #>.
			if (i + 1 < len && script[i] == '<' && script[i + 1] == '#')
			{
				i += 2;
				while (i + 1 < len && !(script[i] == '#' && script[i + 1] == '>'))
					i++;

				if (i + 1 < len)
					i += 2; // skip past '#>'

				lastSafeOffset = i;
				continue;
			}

			// #requires directive: skip the whole line.
			if (IsRequiresDirective(script, i))
			{
				while (i < len && script[i] != '\n')
					i++;

				lastSafeOffset = i;
				continue;
			}

			// using statement: 'using <kind> ...' — skip the whole line.
			if (MatchesKeywordAt(script, i, "using"))
			{
				while (i < len && script[i] != '\n')
					i++;

				lastSafeOffset = i;
				continue;
			}

			// Attribute or attribute-decorated param: '[...]'.
			//
			// PowerShell uses `[...]` for TWO unrelated syntactic constructs at
			// the top of a script:
			//
			//   (a) Attribute decoration:  [CmdletBinding()] param($x)
			//                              [Alias('foo')] function Bar { ... }
			//
			//   (b) Type-literal expression: [System.IO.File]::WriteAllText(...)
			//                                [int]$x = 5
			//                                [datetime]::Now
			//
			// Only (a) precedes a `param`/`function`/`class`/`filter` (or another
			// attribute) — i.e., the `[...]` must be followed by something the
			// attribute can decorate. Case (b) is the START of the script body
			// and the prologue MUST be inserted BEFORE the `[`, not after the
			// matching `]` (doing the latter splits `[Type]` from `::Method` and
			// breaks the static call with `'::WriteAllText' is not recognized`).
			//
			// We disambiguate by peeking past the `]` for the next non-trivial
			// token via `IsAttributeFollower`.
			if (script[i] == '[')
			{
				var closeIndex = FindMatchingBracket(script, i, '[', ']');
				if (closeIndex < 0)
					break;

				if (!IsAttributeFollower(script, closeIndex + 1))
				{
					// Type-literal expression — body starts at this `[`.
					break;
				}

				i = closeIndex + 1;
				lastSafeOffset = i;
				continue;
			}

			// param(...) block.
			if (MatchesKeywordAt(script, i, "param"))
			{
				// Walk to the opening paren.
				var parenStart = i + "param".Length;
				while (parenStart < len && char.IsWhiteSpace(script[parenStart]))
					parenStart++;

				if (parenStart < len && script[parenStart] == '(')
				{
					var parenEnd = FindMatchingBracket(script, parenStart, '(', ')');
					if (parenEnd < 0)
						break;

					i = parenEnd + 1;

					// Consume the rest of the line that holds the closing paren so the
					// injected prologue lands on its own physical line.
					while (i < len && script[i] != '\n')
						i++;

					lastSafeOffset = i;
					continue;
				}

				// 'param' keyword without an open paren — bail out and treat current
				// position as the body start.
				break;
			}

			// Anything else: this is the start of the script body. Stop here.
			break;
		}

		return lastSafeOffset;
	}

	/// <summary>
	/// Returns true if the script at the given offset begins the <c>#requires</c> directive.
	/// </summary>
	private static bool IsRequiresDirective(string script, int offset)
	{
		const string requires = "#requires";
		if (offset + requires.Length > script.Length)
			return false;

		return string.Compare(script, offset, requires, 0, requires.Length, StringComparison.OrdinalIgnoreCase) == 0
			&& (offset + requires.Length == script.Length
				|| char.IsWhiteSpace(script[offset + requires.Length]));
	}

	/// <summary>
	/// Returns true if the script matches <paramref name="keyword"/> at the given offset
	/// followed by a whitespace, paren, or end of input.
	/// </summary>
	private static bool MatchesKeywordAt(string script, int offset, string keyword)
	{
		if (offset + keyword.Length > script.Length)
			return false;

		if (string.Compare(script, offset, keyword, 0, keyword.Length, StringComparison.OrdinalIgnoreCase) != 0)
			return false;

		if (offset + keyword.Length == script.Length)
			return true;

		var nextChar = script[offset + keyword.Length];
		return char.IsWhiteSpace(nextChar) || nextChar == '(';
	}

	/// <summary>
	/// Returns true if the character at <paramref name="offset"/> (after
	/// skipping whitespace) begins a token that can legally follow a
	/// PowerShell attribute decoration at the top of a script — namely:
	/// another attribute (<c>[</c>), a comment (which the outer scanner
	/// will skip on its next iteration), or one of the attribute-target
	/// keywords <c>param</c>, <c>function</c>, <c>class</c>, <c>filter</c>.
	/// </summary>
	/// <remarks>
	/// <para>Returns false for <c>::</c> (static member access), <c>$</c>
	/// (type cast onto a variable), <c>.</c> (instance member access),
	/// <c>(</c> (type-literal invocation), operators, or any other token
	/// — all of which indicate the preceding <c>[...]</c> is a
	/// type-literal expression (<c>[Type]::Method</c>, <c>[int]$x</c>,
	/// <c>[datetime]::Now</c>, etc.), NOT an attribute decoration.</para>
	/// <para>This is what protects scripts like <c>[System.IO.File]::WriteAllText(...)</c>
	/// from having the prologue inserted between <c>]</c> and <c>::</c>,
	/// which would break the static call with
	/// <c>The term '::WriteAllText' is not recognized</c>.</para>
	/// </remarks>
	private static bool IsAttributeFollower(string script, int offset)
	{
		var i = offset;
		var len = script.Length;

		// Skip whitespace and newlines (PowerShell allows line breaks
		// between attributes and the param/function keyword they decorate).
		while (i < len && char.IsWhiteSpace(script[i]))
			i++;

		if (i >= len)
			return false;

		// Another attribute decoration — the outer scanner will recurse.
		if (script[i] == '[')
			return true;

		// Comments between attribute and target are unusual but legal.
		// Returning true lets the outer scanner consume the comment on its
		// next iteration and then re-evaluate the follow-up token.
		if (script[i] == '#')
			return true;
		if (i + 1 < len && script[i] == '<' && script[i + 1] == '#')
			return true;

		// Canonical attribute targets at script top level.
		if (MatchesKeywordAt(script, i, "param"))
			return true;
		if (MatchesKeywordAt(script, i, "function"))
			return true;
		if (MatchesKeywordAt(script, i, "class"))
			return true;
		if (MatchesKeywordAt(script, i, "filter"))
			return true;

		// Everything else — `::`, `$`, `.`, `(`, operators, unrecognized
		// identifiers — means the bracketed token was a type-literal
		// expression, not an attribute. The script body starts at the `[`.
		return false;
	}

	/// <summary>
	/// Finds the offset of the bracket that closes the bracket at <paramref name="openIndex"/>.
	/// Tracks string literals (both <c>"..."</c> and <c>'...'</c>) so brackets inside strings are
	/// ignored. Returns -1 if no match is found.
	/// </summary>
	private static int FindMatchingBracket(string script, int openIndex, char open, char close)
	{
		var depth = 0;
		var i = openIndex;
		var len = script.Length;

		while (i < len)
		{
			var c = script[i];

			// Skip strings to avoid counting brackets inside them.
			if (c == '"' || c == '\'')
			{
				var quote = c;
				i++;
				while (i < len)
				{
					if (script[i] == '`' && i + 1 < len)
					{
						i += 2; // backtick-escaped char
						continue;
					}
					if (script[i] == quote)
					{
						i++;
						break;
					}
					i++;
				}
				continue;
			}

			if (c == open)
				depth++;
			else if (c == close)
			{
				depth--;
				if (depth == 0)
					return i;
			}

			i++;
		}

		return -1;
	}

	/// <summary>
	/// Spills oversize arguments to files under the orchestration's temp directory
	/// to keep the launched process's command line within Windows'
	/// <c>CreateProcessW</c> limit (~32,767 chars).
	/// </summary>
	/// <remarks>
	/// <para>When the combined length of resolved arguments exceeds
	/// <see cref="ArgSpillTotalThreshold"/>, every individual argument larger
	/// than <see cref="ArgSpillSingleArgThreshold"/> is written to a temp file
	/// (tagged to the current step so it appears in the run's saved-files
	/// trace) and replaced in the argument list with a marker of the form
	/// <c>@orchestra-file:&lt;absolute-path&gt;</c>. Small arguments are passed
	/// through verbatim.</para>
	/// <para>In addition, a JSON manifest containing the fully-resolved arguments
	/// (post-spill values reflecting the original strings) is written and its
	/// path exposed via the <c>ORCHESTRA_ARGS_FILE</c> environment variable, so
	/// scripts in any shell can reconstruct the full argument list without
	/// relying on the PowerShell-specific prologue.</para>
	/// <para>If <paramref name="tempFileStore"/> is <c>null</c> (e.g., in tests
	/// that construct an <c>OrchestrationExecutionContext</c> directly without a
	/// temp store), spilling is skipped and arguments are returned unchanged —
	/// preserving prior behavior.</para>
	/// </remarks>
	internal static (string[] Arguments, IReadOnlyDictionary<string, string> Environment) SpillOversizeArguments(
		string[] arguments,
		OrchestrationTempFileStore? tempFileStore,
		string stepName)
	{
		var emptyEnv = (IReadOnlyDictionary<string, string>)new Dictionary<string, string>(StringComparer.Ordinal);

		if (arguments.Length == 0 || tempFileStore is null)
			return (arguments, emptyEnv);

		var totalLength = 0;
		for (var i = 0; i < arguments.Length; i++)
			totalLength += arguments[i]?.Length ?? 0;

		if (totalLength <= ArgSpillTotalThreshold)
			return (arguments, emptyEnv);

		var rewritten = new string[arguments.Length];
		for (var i = 0; i < arguments.Length; i++)
		{
			var arg = arguments[i] ?? string.Empty;
			if (arg.Length > ArgSpillSingleArgThreshold)
			{
				var spilledPath = tempFileStore.SaveFile(arg, stepName, "arg");
				rewritten[i] = OrchestraFileMarker + spilledPath;
			}
			else
			{
				rewritten[i] = arg;
			}
		}

		// Write a JSON manifest with the *original* (fully-resolved) argument
		// values so non-PowerShell scripts can reconstruct the full argv via
		// $env:ORCHESTRA_ARGS_FILE without parsing markers themselves.
		var manifestJson = System.Text.Json.JsonSerializer.Serialize(arguments);
		var manifestPath = tempFileStore.SaveFile(manifestJson, stepName, "json");

		var env = new Dictionary<string, string>(StringComparer.Ordinal)
		{
			["ORCHESTRA_ARGS_FILE"] = manifestPath,
		};

		return (rewritten, env);
	}

	/// <summary>
	/// Grace window granted to a force-killed script process to release its
	/// file handles (and let any pending stdout/stderr callbacks drain)
	/// before <see cref="ExecuteAsync"/>'s <c>finally</c> block attempts to
	/// delete the temp script file. Five seconds is comfortably larger than
	/// the &lt;100ms typical kernel-side termination latency on Windows / Linux
	/// while still bounding the user-visible cancellation delay.
	/// </summary>
	private const int CancelKillGraceSeconds = 5;

	/// <summary>
	/// Force-kills <paramref name="process"/> and its entire descendant tree
	/// (e.g. <c>pwsh</c> -&gt; <c>cmd.exe</c> -&gt; <c>az.cmd</c>) and waits
	/// briefly for the OS to release the process's file handles. Used on the
	/// cancellation path so the temp script file can be deleted in the outer
	/// <c>finally</c> instead of leaking, and so the descendant tree does not
	/// outlive the orchestration as an orphan.
	/// </summary>
	/// <remarks>
	/// All failures (kill races, drain timeout) are logged and swallowed —
	/// the cancellation outcome must be preserved regardless.
	/// </remarks>
	private void TerminateProcessTreeOnCancel(Process process, string stepName)
	{
		// Snapshot the PID up front: once Kill races complete, .NET may
		// throw InvalidOperationException when reading Id from a disposed
		// or already-exited process.
		int processId;
		try { processId = process.Id; }
		catch { processId = -1; }

		LogScriptCancelled(stepName, processId);

		try
		{
			process.Kill(entireProcessTree: true);
		}
		catch (Exception killEx)
		{
			// Most common cause: the process exited naturally between our
			// last poll and the kill (e.g., the script reached its final
			// statement just as cancellation fired). Not worth surfacing
			// as an error, but useful to capture for diagnostic noise.
			LogScriptKillFailed(stepName, killEx);
		}

		// Bounded synchronous drain so the temp file handle is released
		// before File.Delete runs. WaitForExit(milliseconds) is the
		// synchronous form — using the async form here would require
		// awaiting in a catch block which fights the existing flow and
		// risks deadlocking the cancellation token plumbing.
		var grace = TimeSpan.FromSeconds(CancelKillGraceSeconds);
		var drained = false;
		try
		{
			drained = process.WaitForExit((int)grace.TotalMilliseconds);
		}
		catch
		{
			// Process may already be disposed / inaccessible — best effort.
			drained = true;
		}

		if (!drained)
		{
			LogScriptKillDrainTimeout(stepName, CancelKillGraceSeconds);
		}
	}

	/// <summary>
	/// Builds a trace record for the script step so the viewer can display
	/// the shell, script source, arguments, and output.
	/// </summary>
	private static StepExecutionTrace BuildTrace(
		string shell,
		string scriptSource,
		string[] processArguments,
		string[] arguments,
		string? workingDirectory,
		IReadOnlyDictionary<string, string> environment,
		string? stdin,
		string stdout,
		string stderr)
	{
		var contextInfo = new StringBuilder();
		contextInfo.AppendLine($"Shell: {shell}");
		contextInfo.AppendLine($"Script: {scriptSource}");
		if (workingDirectory is not null)
			contextInfo.AppendLine($"Working Directory: {workingDirectory}");
		if (environment.Count > 0)
		{
			contextInfo.AppendLine("Environment Variables:");
			foreach (var (key, value) in environment)
				contextInfo.AppendLine($"  {key}={value}");
		}

		var userPrompt = arguments.Length > 0
			? $"Arguments: {string.Join(' ', arguments.Select(a => a.Contains(' ') ? $"\"{a}\"" : a))}"
			: string.Empty;

		return new StepExecutionTrace
		{
			Shell = shell,
			ScriptSource = scriptSource,
			CommandArguments = [.. processArguments],
			WorkingDirectory = workingDirectory,
			Environment = new Dictionary<string, string>(environment),
			Stdin = stdin,
			SystemPrompt = contextInfo.ToString().TrimEnd(),
			UserPromptRaw = userPrompt,
			FinalResponse = stdout,
			ResponseSegments = stderr.Length > 0 ? [stderr] : [],
		};
	}

	#region Source-Generated Logging

	[LoggerMessage(
		EventId = 1,
		Level = LogLevel.Information,
		Message = "Step '{StepName}' executing script via '{Shell}' (source: {ScriptSource}){Arguments}")]
	private partial void LogScriptStart(string stepName, string shell, string scriptSource, string arguments);

	[LoggerMessage(
		EventId = 2,
		Level = LogLevel.Error,
		Message = "Step '{StepName}' failed to start shell '{Shell}'")]
	private partial void LogScriptStartFailed(string stepName, string shell);

	[LoggerMessage(
		EventId = 3,
		Level = LogLevel.Information,
		Message = "Step '{StepName}' script completed with exit code {ExitCode}")]
	private partial void LogScriptSuccess(string stepName, int exitCode);

	[LoggerMessage(
		EventId = 4,
		Level = LogLevel.Warning,
		Message = "Step '{StepName}' script failed with exit code {ExitCode}: {Error}")]
	private partial void LogScriptFailed(string stepName, int exitCode, string error);

	[LoggerMessage(
		EventId = 5,
		Level = LogLevel.Error,
		Message = "Step '{StepName}' script threw an exception")]
	private partial void LogScriptException(string stepName, Exception ex);

	[LoggerMessage(
		EventId = 6,
		Level = LogLevel.Error,
		Message = "Step '{StepName}' script file not found: '{ScriptFile}'")]
	private partial void LogScriptFileNotFound(string stepName, string scriptFile);

	[LoggerMessage(
		EventId = 7,
		Level = LogLevel.Debug,
		Message = "Step '{StepName}' strict-mode prologue injected for shell '{Shell}'")]
	private partial void LogStrictPrologueInjected(string stepName, string shell);

	[LoggerMessage(
		EventId = 8,
		Level = LogLevel.Information,
		Message = "Step '{StepName}' cancelled; terminating script process tree (PID {ProcessId})")]
	private partial void LogScriptCancelled(string stepName, int processId);

	[LoggerMessage(
		EventId = 9,
		Level = LogLevel.Warning,
		Message = "Step '{StepName}' failed to kill script process tree on cancel (process may have already exited)")]
	private partial void LogScriptKillFailed(string stepName, Exception ex);

	[LoggerMessage(
		EventId = 10,
		Level = LogLevel.Warning,
		Message = "Step '{StepName}' script process did not exit within {GraceSeconds}s after kill; the temp script file may leak")]
	private partial void LogScriptKillDrainTimeout(string stepName, int graceSeconds);

	[LoggerMessage(
		EventId = 11,
		Level = LogLevel.Information,
		Message = "Step '{StepName}' requested orchestration completion via control file (status={Status}, reason={Reason})")]
	private partial void LogControlComplete(string stepName, string status, string reason);

	[LoggerMessage(
		EventId = 12,
		Level = LogLevel.Information,
		Message = "Step '{StepName}' set status via control file (status={Status}, reason={Reason})")]
	private partial void LogControlSetStatus(string stepName, string status, string reason);

	[LoggerMessage(
		EventId = 13,
		Level = LogLevel.Warning,
		Message = "Step '{StepName}' control file was ignored or rejected: {Error}")]
	private partial void LogControlFileInvalid(string stepName, string error);

	#endregion
}
