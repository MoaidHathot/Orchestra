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

			// Build the shell-escaped argument string
			var escapedArgs = arguments.Select(arg =>
			{
				if (isWindows)
				{
					// On Windows cmd.exe: wrap args containing spaces in double quotes
					return arg.Contains(' ') ? $"\"{arg}\"" : arg;
				}
				else
				{
					// On Unix sh: use single quotes to prevent interpretation, escaping embedded single quotes
					return $"'{arg.Replace("'", "'\\''")}'";
				}
			});

			var fullCommandLine = arguments.Length > 0
				? $"{command} {string.Join(' ', escapedArgs)}"
				: command;

			var startInfo = new ProcessStartInfo
			{
				FileName = isWindows ? "cmd.exe" : "/bin/sh",
				// Use the Arguments string property (not ArgumentList) so that
				// cmd.exe /c receives the command line verbatim without extra quoting.
				Arguments = isWindows ? $"/c {fullCommandLine}" : $"-c {fullCommandLine}",
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				RedirectStandardInput = resolvedStdin is not null,
				UseShellExecute = false,
				CreateNoWindow = true,
			};

			// Set working directory
			if (workingDirectory is not null)
			{
				startInfo.WorkingDirectory = workingDirectory;
			}

			// Set environment variables (resolve templates in values)
			foreach (var (key, value) in commandStep.Environment)
			{
				var resolvedValue = TemplateResolver.Resolve(value, context.Parameters, context, step.DependsOn, step);
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
					stdoutBuilder.AppendLine(e.Data);
			};

			process.ErrorDataReceived += (_, e) =>
			{
				if (e.Data is not null)
					stderrBuilder.AppendLine(e.Data);
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

			// Wait for process to exit with cancellation support
			await process.WaitForExitAsync(cancellationToken);

			var stdout = stdoutBuilder.ToString().TrimEnd();
			var stderr = stderrBuilder.ToString().TrimEnd();

			if (process.ExitCode == 0)
			{
				var output = commandStep.IncludeStdErr && stderr.Length > 0
					? $"{stdout}\n{stderr}"
					: stdout;

				LogCommandSuccess(step.Name, process.ExitCode);

				// Build trace data for viewer visibility
				var trace = BuildTrace(command, arguments, workingDirectory, commandStep.Environment, output, stderr);
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
				var trace = BuildTrace(command, arguments, workingDirectory, commandStep.Environment, stdout, stderr);
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
	/// Builds a trace record for the command step so the viewer can display
	/// the resolved command, arguments, working directory, and output.
	/// </summary>
	private static StepExecutionTrace BuildTrace(
		string command,
		string[] arguments,
		string? workingDirectory,
		IReadOnlyDictionary<string, string> environment,
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

	#endregion
}
