using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Orchestra.Engine;

/// <summary>
/// Executes command steps by launching external processes.
/// Captures stdout (and optionally stderr) as the step output.
/// Supports template resolution in command, arguments, working directory, and environment variables.
/// </summary>
public sealed partial class CommandStepExecutor : IStepExecutor
{
	private readonly IOrchestrationReporter _reporter;
	private readonly ILogger<CommandStepExecutor> _logger;

	public CommandStepExecutor(
		IOrchestrationReporter reporter,
		ILogger<CommandStepExecutor> logger)
	{
		_reporter = reporter;
		_logger = logger;
	}

	public OrchestrationStepType StepType => OrchestrationStepType.Command;

	/// <summary>
	/// UTF-8 without a byte-order mark, used for every redirected stream of the child process.
	/// </summary>
	private static readonly Encoding s_utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

	/// <summary>
	/// Prefix inserted after <c>cmd.exe /c</c> on Windows so the child's (hidden) console runs
	/// with the UTF-8 code page before the user's command starts. <c>chcp</c>'s own
	/// "Active code page" line is discarded; a plain <c>&amp;</c> (not <c>&amp;&amp;</c>) keeps
	/// the user's command running — with its own exit code — even if <c>chcp</c> were to fail.
	/// </summary>
	/// <remarks>
	/// Every process started afterwards (pwsh, dnx/dotnet tools, node, python, a nested cmd)
	/// reads the new code page. The one exception is cmd.exe's own builtins in the same
	/// instance (<c>echo</c>, <c>set</c>, …), which keep the code page cmd cached at startup —
	/// a non-ASCII literal echoed directly by the wrapping cmd still degrades. Real tools are
	/// unaffected, and that edge case never round-tripped before either.
	/// </remarks>
	internal const string WindowsUtf8CodePagePrefix = "chcp 65001>nul & ";

	/// <summary>
	/// Characters cmd.exe treats specially outside quote mode. Every one of them is
	/// caret-escaped by <see cref="EscapeArgumentForCmd"/>, including the quote itself, so
	/// cmd.exe never enters quote mode and passes the argument through verbatim.
	/// </summary>
	private static readonly System.Buffers.SearchValues<char> s_cmdMetaCharacters =
		System.Buffers.SearchValues.Create("()%!^\"<>&|");

	/// <summary>
	/// Escapes one argument for a command line that runs under <c>cmd.exe /c</c>, so the child
	/// program receives it byte-for-byte. Two parsers see the text — cmd.exe first, then the
	/// child's C runtime (<c>CommandLineToArgvW</c> rules) — and both have to be satisfied:
	/// <list type="number">
	///   <item>C-runtime quoting: every run of backslashes that precedes a <c>"</c> (or the
	///   end of the value, where the closing quote will go) is doubled and the embedded quote
	///   is escaped as <c>\"</c>; the whole value is wrapped in <c>"…"</c>. Backslashes anywhere
	///   else are literal, so Windows paths survive untouched.</item>
	///   <item>cmd.exe escaping: every metacharacter in the quoted result — <c>( ) % ! ^ " &lt; &gt; &amp; |</c>,
	///   <em>including</em> the surrounding quotes — is prefixed with <c>^</c>. Because the quotes
	///   are escaped too, cmd.exe never toggles quote mode, so its parser sees every character
	///   plainly and strips exactly the carets, handing the child the C-runtime-quoted value.</item>
	/// </list>
	/// This is the algorithm documented at https://qntm.org/cmd and used by Node's cross-spawn.
	/// A consequence worth knowing: <c>%NAME%</c> inside an argument is now a literal for the
	/// child instead of being expanded by cmd.exe — use the step's <c>environment</c> block or
	/// <c>{{env.NAME}}</c> when expansion is wanted.
	/// </summary>
	internal static string EscapeArgumentForCmd(string arg)
	{
		// Step 1: C-runtime quoting.
		var quoted = new StringBuilder(arg.Length + 8);
		quoted.Append('"');

		var backslashes = 0;
		foreach (var c in arg)
		{
			if (c == '\\')
			{
				backslashes++;
				continue;
			}

			if (c == '"')
			{
				// 2n+1 backslashes: the run is preserved and the quote is escaped.
				quoted.Append('\\', backslashes * 2 + 1);
				quoted.Append('"');
			}
			else
			{
				// Backslashes not followed by a quote are literal.
				quoted.Append('\\', backslashes);
				quoted.Append(c);
			}

			backslashes = 0;
		}

		// A trailing run precedes the closing quote we are about to add: double it.
		quoted.Append('\\', backslashes * 2);
		quoted.Append('"');

		// Step 2: caret-escape every cmd.exe metacharacter, quotes included.
		var span = quoted.ToString().AsSpan();
		if (span.IndexOfAny(s_cmdMetaCharacters) < 0)
			return quoted.ToString();

		var escaped = new StringBuilder(span.Length + 8);
		foreach (var c in span)
		{
			if (s_cmdMetaCharacters.Contains(c))
				escaped.Append('^');
			escaped.Append(c);
		}

		return escaped.ToString();
	}

	/// <summary>
	/// Escapes one argument for <c>/bin/sh -c</c>: single quotes make every character literal,
	/// and an embedded single quote is expressed as <c>'\''</c> (close, escaped quote, reopen).
	/// </summary>
	internal static string EscapeArgumentForSh(string arg)
		=> $"'{arg.Replace("'", "'\\''")}'";

	public async Task<ExecutionResult> ExecuteAsync(
		OrchestrationStep step,
		OrchestrationExecutionContext context,
		CancellationToken cancellationToken = default)
	{
		if (step is not CommandOrchestrationStep commandStep)
			throw new InvalidOperationException(
				$"CommandStepExecutor received a step of type '{step.GetType().Name}' " +
				$"but expected '{nameof(CommandOrchestrationStep)}'.");

		var rawDependencyOutputs = context.GetRawDependencyOutputs(step.DependsOn);

		try
		{
			// Resolve template expressions in command
			var command = TemplateResolver.Resolve(commandStep.Command, context.Parameters, context, step.DependsOn, step);

			// Resolve template expressions in arguments
			var arguments = commandStep.Arguments
				.Select(arg => TemplateResolver.Resolve(arg, context.Parameters, context, step.DependsOn, step))
				.ToArray();

			// Resolve template expressions in working directory
			string? workingDirectory = null;
			if (commandStep.WorkingDirectory is not null)
			{
				workingDirectory = TemplateResolver.Resolve(commandStep.WorkingDirectory, context.Parameters, context, step.DependsOn, step);
			}

			// Resolve template expressions in stdin content
			string? resolvedStdin = null;
			if (commandStep.Stdin is not null)
			{
				resolvedStdin = TemplateResolver.Resolve(commandStep.Stdin, context.Parameters, context, step.DependsOn, step);
			}

			// Build process start info.
			// Route through the platform shell so that .cmd shims (Windows) and
			// PATH-resolved scripts (Linux/macOS) are found correctly.
			// Without shell wrapping, commands like 'dnx' (a .NET global tool installed
			// as a .cmd shim) fail with "The system cannot find the file specified"
			// because Process.Start with UseShellExecute=false calls CreateProcess
			// directly, which doesn't resolve .cmd files.
			var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

			// Build the shell-escaped argument string. Arguments are frequently template-resolved
			// from step outputs or parameters, so every metacharacter must reach the child program
			// as a literal — otherwise "{{review.output}}" containing "& del /q *" is a command.
			var escapedArgs = arguments.Select(arg => isWindows ? EscapeArgumentForCmd(arg) : EscapeArgumentForSh(arg));

			var fullCommandLine = arguments.Length > 0
				? $"{command} {string.Join(' ', escapedArgs)}"
				: command;

			// Pin UTF-8 on BOTH ends of the pipe. The host side decodes stdout/stderr as UTF-8
			// (and encodes stdin as UTF-8). On Windows the child side is handled by switching
			// the hidden console's code page to 65001 before the command runs: cmd.exe, .NET
			// tools (dnx/dotnet), PowerShell and most console programs pick their output
			// encoding from the console code page, so without this they emit the OEM code
			// page (e.g. CP437), which cannot represent most non-Latin text and silently turns
			// it into '?' before the bytes ever reach us. Unix shells already run UTF-8.
			var startInfo = new ProcessStartInfo
			{
				FileName = isWindows ? "cmd.exe" : "/bin/sh",
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				RedirectStandardInput = resolvedStdin is not null,
				UseShellExecute = false,
				CreateNoWindow = true,
				StandardOutputEncoding = s_utf8NoBom,
				StandardErrorEncoding = s_utf8NoBom,
				StandardInputEncoding = resolvedStdin is not null ? s_utf8NoBom : null,
			};

			if (isWindows)
			{
				// Use the Arguments string property so that cmd.exe /c receives
				// the command line verbatim without extra quoting. The chcp prefix also
				// guarantees the first character after /c is not a quote, which sidesteps
				// cmd.exe's quote-stripping heuristics for quoted program paths.
				startInfo.Arguments = $"/c {WindowsUtf8CodePagePrefix}{fullCommandLine}";
			}
			else
			{
				// Use ArgumentList on Linux to ensure the full command line is passed
				// as a single argument to /bin/sh -c, preventing word splitting.
				startInfo.ArgumentList.Add("-c");
				startInfo.ArgumentList.Add(fullCommandLine);
			}

			// Set working directory
			if (workingDirectory is not null)
			{
				startInfo.WorkingDirectory = workingDirectory;
			}

			// Python decides its redirected-stdout encoding from the locale unless told
			// otherwise; mirror ScriptStepExecutor so Python-based tools also emit UTF-8.
			// A user-supplied value below wins.
			startInfo.Environment["PYTHONIOENCODING"] = "utf-8";

			// Set environment variables (resolve templates in values)
			var resolvedEnvironment = new Dictionary<string, string>(StringComparer.Ordinal);
			foreach (var (key, value) in commandStep.Environment)
			{
				var resolvedValue = TemplateResolver.Resolve(value, context.Parameters, context, step.DependsOn, step);
				resolvedEnvironment[key] = resolvedValue;
				startInfo.Environment[key] = resolvedValue;
			}

			var argumentsDisplay = arguments.Length > 0
				? string.Join(' ', arguments.Select(a => a.Contains(' ') ? $"\"{a}\"" : a))
				: string.Empty;

			LogCommandStart(step.Name, command, argumentsDisplay);

			// Start the process
			using var process = new Process { StartInfo = startInfo };

			var stdoutBuilder = new StringBuilder();
			var stderrBuilder = new StringBuilder();

			process.OutputDataReceived += (_, e) =>
			{
				if (e.Data is not null)
				{
					stdoutBuilder.AppendLine(e.Data);
					_reporter.ReportContentDelta(step.Name, e.Data + Environment.NewLine);
				}
			};

			process.ErrorDataReceived += (_, e) =>
			{
				if (e.Data is not null)
				{
					stderrBuilder.AppendLine(e.Data);
					if (commandStep.IncludeStdErr)
						_reporter.ReportContentDelta(step.Name, e.Data + Environment.NewLine);
				}
			};

			if (!process.Start())
			{
				var errorMessage = $"Failed to start process '{command}'";
				LogCommandStartFailed(step.Name, command);
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
			// orchestration timeout) the child process is still alive and would
			// continue running as an orphan after this method returns. Because
			// CommandStepExecutor always wraps in `cmd.exe /c` on Windows
			// (line 94), the descendant tree typically includes a cmd.exe
			// grandchild that, if signalled rather than terminated, will produce
			// the "Terminate batch job (Y/N)?" prompt — a long-standing source
			// of unkillable strays. Force-kill the entire tree synchronously and
			// drain briefly before re-throwing.
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
				var output = commandStep.IncludeStdErr && stderr.Length > 0
					? $"{stdout}\n{stderr}"
					: stdout;

				LogCommandSuccess(step.Name, process.ExitCode);

				// Build trace data for viewer visibility
				var trace = BuildTrace(command, arguments, workingDirectory, resolvedEnvironment, resolvedStdin, stdout, stderr);
				_reporter.ReportStepTrace(step.Name, trace);

				return ExecutionResult.Succeeded(
					output,
					rawDependencyOutputs: rawDependencyOutputs,
					trace: trace);
			}
			else
			{
				var errorMessage = $"Command '{command}' exited with code {process.ExitCode}";
				if (stderr.Length > 0)
					errorMessage += $": {stderr}";

				LogCommandFailed(step.Name, process.ExitCode, errorMessage);
				_reporter.ReportStepError(step.Name, errorMessage);

				// Build trace data even on failure
				var trace = BuildTrace(command, arguments, workingDirectory, resolvedEnvironment, resolvedStdin, stdout, stderr);
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
			var errorMessage = $"Command execution failed: {ex.Message}";
			LogCommandException(step.Name, ex);
			_reporter.ReportStepError(step.Name, errorMessage);
			return ExecutionResult.Failed(errorMessage, rawDependencyOutputs);
		}
	}

	/// <summary>
	/// Grace window granted to a force-killed command process to release
	/// any held resources (and let any pending stdout/stderr callbacks
	/// drain) before <see cref="ExecuteAsync"/> returns. Mirrors the
	/// equivalent constant in <see cref="ScriptStepExecutor"/>.
	/// </summary>
	private const int CancelKillGraceSeconds = 5;

	/// <summary>
	/// Force-kills <paramref name="process"/> and its entire descendant tree
	/// (which on Windows always includes the <c>cmd.exe</c> wrapper this
	/// executor spawns) and waits briefly for the OS to release process
	/// handles. Used on the cancellation path so the descendant tree does
	/// not outlive the orchestration as an orphan — and so any <c>cmd.exe</c>
	/// running a <c>.cmd</c>/<c>.bat</c> shim is terminated outright rather
	/// than signalled (which would otherwise produce the
	/// "Terminate batch job (Y/N)?" prompt on a stranded console).
	/// </summary>
	/// <remarks>
	/// All failures (kill races, drain timeout) are logged and swallowed —
	/// the cancellation outcome must be preserved regardless.
	/// </remarks>
	private void TerminateProcessTreeOnCancel(Process process, string stepName)
	{
		int processId;
		try { processId = process.Id; }
		catch { processId = -1; }

		LogCommandCancelled(stepName, processId);

		try
		{
			process.Kill(entireProcessTree: true);
		}
		catch (Exception killEx)
		{
			// Most common cause: the process exited naturally between our
			// last poll and the kill — not worth surfacing as an error.
			LogCommandKillFailed(stepName, killEx);
		}

		var grace = TimeSpan.FromSeconds(CancelKillGraceSeconds);
		var drained = false;
		try
		{
			drained = process.WaitForExit((int)grace.TotalMilliseconds);
		}
		catch
		{
			drained = true;
		}

		if (!drained)
		{
			LogCommandKillDrainTimeout(stepName, CancelKillGraceSeconds);
		}
	}

	/// <summary>
	/// Builds a trace record for the command step so the viewer can display
	/// the resolved command, arguments, working directory, and output.
	/// </summary>
	private static StepExecutionTrace BuildTrace(
		string command,
		string[] arguments,
		string? workingDirectory,
		IReadOnlyDictionary<string, string> environment,
		string? stdin,
		string stdout,
		string stderr)
	{
		// Build a human-readable command line for display
		var commandLineParts = new List<string> { command };
		commandLineParts.AddRange(arguments);
		var commandLine = string.Join(' ', commandLineParts.Select(p => p.Contains(' ') ? $"\"{p}\"" : p));

		// Include working directory and env vars in the "system prompt" area if present
		var contextInfo = new StringBuilder();
		if (workingDirectory is not null)
			contextInfo.AppendLine($"Working Directory: {workingDirectory}");
		if (environment.Count > 0)
		{
			contextInfo.AppendLine("Environment Variables:");
			foreach (var (key, value) in environment)
				contextInfo.AppendLine($"  {key}={value}");
		}

		return new StepExecutionTrace
		{
			Command = command,
			CommandArguments = [.. arguments],
			WorkingDirectory = workingDirectory,
			Environment = new Dictionary<string, string>(environment),
			Stdin = stdin,
			SystemPrompt = contextInfo.Length > 0 ? contextInfo.ToString().TrimEnd() : null,
			UserPromptRaw = commandLine,
			FinalResponse = stdout,
			ResponseSegments = stderr.Length > 0 ? [stderr] : [],
		};
	}

	#region Source-Generated Logging

	[LoggerMessage(
		EventId = 1,
		Level = LogLevel.Information,
		Message = "Step '{StepName}' executing command '{Command}' with arguments '{Arguments}'")]
	private partial void LogCommandStart(string stepName, string command, string arguments);

	[LoggerMessage(
		EventId = 2,
		Level = LogLevel.Error,
		Message = "Step '{StepName}' failed to start process '{Command}'")]
	private partial void LogCommandStartFailed(string stepName, string command);

	[LoggerMessage(
		EventId = 3,
		Level = LogLevel.Information,
		Message = "Step '{StepName}' command completed with exit code {ExitCode}")]
	private partial void LogCommandSuccess(string stepName, int exitCode);

	[LoggerMessage(
		EventId = 4,
		Level = LogLevel.Warning,
		Message = "Step '{StepName}' command failed with exit code {ExitCode}: {Error}")]
	private partial void LogCommandFailed(string stepName, int exitCode, string error);

	[LoggerMessage(
		EventId = 5,
		Level = LogLevel.Error,
		Message = "Step '{StepName}' command threw an exception")]
	private partial void LogCommandException(string stepName, Exception ex);

	[LoggerMessage(
		EventId = 6,
		Level = LogLevel.Information,
		Message = "Step '{StepName}' cancelled; terminating command process tree (PID {ProcessId})")]
	private partial void LogCommandCancelled(string stepName, int processId);

	[LoggerMessage(
		EventId = 7,
		Level = LogLevel.Warning,
		Message = "Step '{StepName}' failed to kill command process tree on cancel (process may have already exited)")]
	private partial void LogCommandKillFailed(string stepName, Exception ex);

	[LoggerMessage(
		EventId = 8,
		Level = LogLevel.Warning,
		Message = "Step '{StepName}' command process did not exit within {GraceSeconds}s after kill")]
	private partial void LogCommandKillDrainTimeout(string stepName, int graceSeconds);

	#endregion
}
