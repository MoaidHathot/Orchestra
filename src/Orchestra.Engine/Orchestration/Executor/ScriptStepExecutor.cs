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
				// Inline script: resolve templates, write to temp file
				var resolvedScript = TemplateResolver.Resolve(scriptStep.Script, context.Parameters, context, step.DependsOn, step);
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
			var arguments = scriptStep.Arguments
				.Select(arg => TemplateResolver.Resolve(arg, context.Parameters, context, step.DependsOn, step))
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

			// Set environment variables (resolve templates in values)
			foreach (var (key, value) in scriptStep.Environment)
			{
				var resolvedValue = TemplateResolver.Resolve(value, context.Parameters, context, step.DependsOn, step);
				startInfo.Environment[key] = resolvedValue;
			}

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
					stdoutBuilder.AppendLine(e.Data);
			};

			process.ErrorDataReceived += (_, e) =>
			{
				if (e.Data is not null)
					stderrBuilder.AppendLine(e.Data);
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

			// Wait for process to exit with cancellation support
			await process.WaitForExitAsync(cancellationToken);

			var stdout = stdoutBuilder.ToString().TrimEnd();
			var stderr = stderrBuilder.ToString().TrimEnd();

			if (process.ExitCode == 0)
			{
				var output = scriptStep.IncludeStdErr && stderr.Length > 0
					? $"{stdout}\n{stderr}"
					: stdout;

				LogScriptSuccess(step.Name, process.ExitCode);

				// Build trace data for viewer visibility
				var trace = BuildTrace(shell, scriptStep.Script is not null ? "(inline)" : scriptFilePath, arguments, workingDirectory, scriptStep.Environment, output, stderr);
				_reporter.ReportStepTrace(step.Name, trace);

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
				var trace = BuildTrace(shell, scriptStep.Script is not null ? "(inline)" : scriptFilePath, arguments, workingDirectory, scriptStep.Environment, stdout, stderr);
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
		}
	}

	/// <summary>
	/// Builds a trace record for the script step so the viewer can display
	/// the shell, script source, arguments, and output.
	/// </summary>
	private static StepExecutionTrace BuildTrace(
		string shell,
		string scriptSource,
		string[] arguments,
		string? workingDirectory,
		IReadOnlyDictionary<string, string> environment,
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

	#endregion
}
