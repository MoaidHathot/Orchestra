using System.Diagnostics;
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
			var command = TemplateResolver.Resolve(commandStep.Command, context.Parameters, context, step.DependsOn);

			// Resolve template expressions in arguments
			var arguments = commandStep.Arguments
				.Select(arg => TemplateResolver.Resolve(arg, context.Parameters, context, step.DependsOn))
				.ToArray();

			// Resolve template expressions in working directory
			string? workingDirectory = null;
			if (commandStep.WorkingDirectory is not null)
			{
				workingDirectory = TemplateResolver.Resolve(commandStep.WorkingDirectory, context.Parameters, context, step.DependsOn);
			}

			// Build process start info
			var startInfo = new ProcessStartInfo
			{
				FileName = command,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				UseShellExecute = false,
				CreateNoWindow = true,
			};

			// Add arguments individually
			foreach (var arg in arguments)
			{
				startInfo.ArgumentList.Add(arg);
			}

			// Set working directory
			if (workingDirectory is not null)
			{
				startInfo.WorkingDirectory = workingDirectory;
			}

			// Set environment variables (resolve templates in values)
			foreach (var (key, value) in commandStep.Environment)
			{
				var resolvedValue = TemplateResolver.Resolve(value, context.Parameters, context, step.DependsOn);
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
				return ExecutionResult.Succeeded(
					output,
					rawDependencyOutputs: rawDependencyOutputs);
			}
			else
			{
				var errorMessage = $"Command '{command}' exited with code {process.ExitCode}";
				if (stderr.Length > 0)
					errorMessage += $": {stderr}";

				LogCommandFailed(step.Name, process.ExitCode, errorMessage);
				_reporter.ReportStepError(step.Name, errorMessage);
				return ExecutionResult.Failed(errorMessage, rawDependencyOutputs);
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
