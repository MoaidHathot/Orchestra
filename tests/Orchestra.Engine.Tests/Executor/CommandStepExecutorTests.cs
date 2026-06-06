using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Orchestra.Engine.Tests.Executor;

public class CommandStepExecutorTests
{
	private static readonly OrchestrationInfo s_defaultInfo = new("test-orchestration", "1.0.0", "run123", DateTimeOffset.UtcNow);
	private readonly IOrchestrationReporter _reporter = Substitute.For<IOrchestrationReporter>();
	private readonly ILogger<CommandStepExecutor> _logger = NullLoggerFactory.Instance.CreateLogger<CommandStepExecutor>();

	private CommandStepExecutor CreateExecutor() => new(_reporter, _logger);

	private static CommandOrchestrationStep CreateCommandStep(
		string name = "cmd-step",
		string command = "dotnet",
		string[]? arguments = null,
		string? workingDirectory = null,
		Dictionary<string, string>? environment = null,
		bool includeStdErr = false,
		string[]? dependsOn = null,
		string[]? parameters = null) => new()
	{
		Name = name,
		Type = OrchestrationStepType.Command,
		DependsOn = dependsOn ?? [],
		Parameters = parameters ?? [],
		Command = command,
		Arguments = arguments ?? [],
		WorkingDirectory = workingDirectory,
		Environment = environment ?? [],
		IncludeStdErr = includeStdErr,
	};

	#region Success Scenarios

	[Fact]
	public async Task ExecuteAsync_SimpleCommand_ReturnsSuccessWithStdout()
	{
		// Arrange
		var executor = CreateExecutor();
		var step = CreateCommandStep(command: "echo", arguments: ["hello"]);
		var context = new OrchestrationExecutionContext { OrchestrationInfo = s_defaultInfo, Parameters = new Dictionary<string, string>() };

		// Act
		var result = await executor.ExecuteAsync(step, context);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Succeeded);
		result.Content.Should().NotBeNullOrWhiteSpace();
	}

	[Fact]
	public async Task ExecuteAsync_CommandWithOutput_CapturesStdout()
	{
		// Arrange
		var executor = CreateExecutor();
		// echo is a shell built-in, works on both platforms when passed through the shell
		var (cmd, args) = GetEchoCommand("Hello Orchestra");
		var step = CreateCommandStep(command: cmd, arguments: args);
		var context = new OrchestrationExecutionContext { OrchestrationInfo = s_defaultInfo, Parameters = new Dictionary<string, string>() };

		// Act
		var result = await executor.ExecuteAsync(step, context);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Succeeded);
		result.Content.Should().Contain("Hello Orchestra");
	}

	[Fact]
	public async Task ExecuteAsync_CommandWithOutput_ReportsContentDelta()
	{
		// Arrange
		var executor = CreateExecutor();
		var (cmd, args) = GetEchoCommand("live command output");
		var step = CreateCommandStep(command: cmd, arguments: args);
		var context = new OrchestrationExecutionContext { OrchestrationInfo = s_defaultInfo, Parameters = new Dictionary<string, string>() };

		// Act
		var result = await executor.ExecuteAsync(step, context);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Succeeded);
		_reporter.Received().ReportContentDelta(step.Name, Arg.Is<string>(chunk => chunk.Contains("live command output")));
	}

	#endregion

	#region Failure Scenarios

	[Fact]
	public async Task ExecuteAsync_CommandNotFound_ReturnsFailedResult()
	{
		// Arrange
		var executor = CreateExecutor();
		var step = CreateCommandStep(command: "nonexistent-binary-xyz-123");
		var context = new OrchestrationExecutionContext { OrchestrationInfo = s_defaultInfo, Parameters = new Dictionary<string, string>() };

		// Act
		var result = await executor.ExecuteAsync(step, context);

		// Assert
		// The command is routed through the platform shell (cmd.exe /c on Windows,
		// /bin/sh -c on Linux), so the shell itself starts but returns a non-zero
		// exit code when the command is not found.
		result.Status.Should().Be(ExecutionStatus.Failed);
		result.ErrorMessage.Should().Contain("nonexistent-binary-xyz-123");
	}

	[Fact]
	public async Task ExecuteAsync_NonZeroExitCode_ReturnsFailedResult()
	{
		// Arrange
		var executor = CreateExecutor();
		// Use 'dotnet help nonexistent-command-xyz' which returns non-zero on all platforms
		var step = CreateCommandStep(
			command: "dotnet",
			arguments: ["help", "nonexistent-command-xyz-123"]);
		var context = new OrchestrationExecutionContext { OrchestrationInfo = s_defaultInfo, Parameters = new Dictionary<string, string>() };

		// Act
		var result = await executor.ExecuteAsync(step, context);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Failed);
	}

	#endregion

	#region Template Resolution

	[Fact]
	public async Task ExecuteAsync_TemplateResolution_ResolvesParametersInArguments()
	{
		// Arrange
		var executor = CreateExecutor();
		var (cmd, args) = GetEchoCommand("{{param.message}}");
		var step = CreateCommandStep(
			command: cmd,
			arguments: args,
			parameters: ["message"]);

		var context = new OrchestrationExecutionContext
		{
			OrchestrationInfo = s_defaultInfo,
			Parameters = new Dictionary<string, string>
			{
				["message"] = "resolved-value"
			}
		};

		// Act
		var result = await executor.ExecuteAsync(step, context);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Succeeded);
		result.Content.Should().Contain("resolved-value");
	}

	[Fact]
	public async Task ExecuteAsync_DependencyOutput_ResolvesInArguments()
	{
		// Arrange
		var executor = CreateExecutor();
		var (cmd, args) = GetEchoCommand("{{step1.output}}");
		var step = CreateCommandStep(
			command: cmd,
			arguments: args,
			dependsOn: ["step1"]);

		var context = new OrchestrationExecutionContext { OrchestrationInfo = s_defaultInfo, Parameters = new Dictionary<string, string>() };
		context.AddResult("step1", ExecutionResult.Succeeded("dep-output"));

		// Act
		var result = await executor.ExecuteAsync(step, context);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Succeeded);
		result.Content.Should().Contain("dep-output");
	}

	#endregion

	#region Environment Variables

	[Fact]
	public async Task ExecuteAsync_WithEnvironmentVariables_SetsEnvironment()
	{
		// Arrange
		var executor = CreateExecutor();
		var (cmd, args) = GetEnvPrintCommand("ORCHESTRA_TEST_VAR");
		var step = CreateCommandStep(
			command: cmd,
			arguments: args,
			environment: new Dictionary<string, string>
			{
				["ORCHESTRA_TEST_VAR"] = "env-value-123"
			});
		var context = new OrchestrationExecutionContext { OrchestrationInfo = s_defaultInfo, Parameters = new Dictionary<string, string>() };

		// Act
		var result = await executor.ExecuteAsync(step, context);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Succeeded);
		result.Content.Should().Contain("env-value-123");
	}

	[Fact]
	public async Task ExecuteAsync_EnvironmentVariableWithTemplate_ResolvesTemplate()
	{
		// Arrange
		var executor = CreateExecutor();
		var (cmd, args) = GetEnvPrintCommand("ORCHESTRA_DYNAMIC");
		var step = CreateCommandStep(
			command: cmd,
			arguments: args,
			environment: new Dictionary<string, string>
			{
				["ORCHESTRA_DYNAMIC"] = "{{param.envValue}}"
			},
			parameters: ["envValue"]);

		var context = new OrchestrationExecutionContext
		{
			OrchestrationInfo = s_defaultInfo,
			Parameters = new Dictionary<string, string>
			{
				["envValue"] = "dynamic-env-resolved"
			}
		};

		// Act
		var result = await executor.ExecuteAsync(step, context);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Succeeded);
		result.Content.Should().Contain("dynamic-env-resolved");
	}

	[Fact]
	public async Task ExecuteAsync_TraceIncludesResolvedProcessDetails()
	{
		// Arrange
		var executor = CreateExecutor();
		var step = CreateCommandStep(
			command: "echo",
			arguments: ["{{param.message}}"],
			environment: new Dictionary<string, string>
			{
				["ORCHESTRA_TRACE_ENV"] = "{{param.envValue}}"
			},
			parameters: ["message", "envValue"]);
		var context = new OrchestrationExecutionContext
		{
			OrchestrationInfo = s_defaultInfo,
			Parameters = new Dictionary<string, string>
			{
				["message"] = "resolved argument",
				["envValue"] = "resolved env",
			}
		};

		// Act
		var result = await executor.ExecuteAsync(step, context);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Succeeded);
		result.Trace.Should().NotBeNull();
		result.Trace!.Command.Should().Be("echo");
		result.Trace.CommandArguments.Should().Equal("resolved argument");
		result.Trace.Environment.Should().ContainKey("ORCHESTRA_TRACE_ENV").WhoseValue.Should().Be("resolved env");
		result.Trace.FinalResponse.Should().Contain("resolved argument");
	}

	#endregion

	#region IncludeStdErr

	[Fact]
	public async Task ExecuteAsync_IncludeStdErr_CapturesBothStreams()
	{
		// Arrange
		var executor = CreateExecutor();
		var (cmd, args) = GetStdoutAndStderrCommand("stdout-data", "stderr-data");
		var step = CreateCommandStep(
			command: cmd,
			arguments: args,
			includeStdErr: true);
		var context = new OrchestrationExecutionContext { OrchestrationInfo = s_defaultInfo, Parameters = new Dictionary<string, string>() };

		// Act
		var result = await executor.ExecuteAsync(step, context);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Succeeded);
		result.Content.Should().Contain("stdout-data");
		result.Content.Should().Contain("stderr-data");
	}

	[Fact]
	public async Task ExecuteAsync_IncludeStdErr_TraceSeparatesStdoutAndStderr()
	{
		// Arrange
		var executor = CreateExecutor();
		var (cmd, args) = GetStdoutAndStderrCommand("stdout-trace", "stderr-trace");
		var step = CreateCommandStep(
			command: cmd,
			arguments: args,
			includeStdErr: true);
		var context = new OrchestrationExecutionContext { OrchestrationInfo = s_defaultInfo, Parameters = new Dictionary<string, string>() };

		// Act
		var result = await executor.ExecuteAsync(step, context);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Succeeded);
		result.Trace.Should().NotBeNull();
		result.Trace!.FinalResponse.Should().Contain("stdout-trace");
		result.Trace.FinalResponse.Should().NotContain("stderr-trace");
		result.Trace.ResponseSegments.Should().ContainSingle().Which.Should().Contain("stderr-trace");
	}

	[Fact]
	public async Task ExecuteAsync_ExcludeStdErr_OnlyCapturesStdout()
	{
		// Arrange
		var executor = CreateExecutor();
		var (cmd, args) = GetStdoutAndStderrCommand("stdout-only", "hidden-stderr");
		var step = CreateCommandStep(
			command: cmd,
			arguments: args,
			includeStdErr: false);
		var context = new OrchestrationExecutionContext { OrchestrationInfo = s_defaultInfo, Parameters = new Dictionary<string, string>() };

		// Act
		var result = await executor.ExecuteAsync(step, context);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Succeeded);
		result.Content.Should().Contain("stdout-only");
		result.Content.Should().NotContain("hidden-stderr");
	}

	#endregion

	#region Wrong Step Type

	[Fact]
	public async Task ExecuteAsync_WrongStepType_ThrowsInvalidOperationException()
	{
		// Arrange
		var executor = CreateExecutor();
		var wrongStep = new PromptOrchestrationStep
		{
			Name = "wrong-step",
			Type = OrchestrationStepType.Prompt,
			DependsOn = [],
			SystemPrompt = "system",
			UserPrompt = "user",
			Model = "claude-opus-4.5"
		};

		var context = new OrchestrationExecutionContext { OrchestrationInfo = s_defaultInfo, Parameters = new Dictionary<string, string>() };

		// Act
		var act = () => executor.ExecuteAsync(wrongStep, context);

		// Assert
		await act.Should().ThrowAsync<InvalidOperationException>()
			.WithMessage("*CommandStepExecutor*PromptOrchestrationStep*CommandOrchestrationStep*");
	}

	#endregion

	#region Cancellation

	[Fact]
	public async Task ExecuteAsync_Cancellation_ThrowsOperationCanceledException()
	{
		// Arrange — also verifies the kill-on-cancel contract for
		// CommandStepExecutor: after the OperationCanceledException propagates,
		// (a) the actual command process must be dead, and (b) the executor's
		// shell wrapper (cmd.exe on Windows, /bin/sh on Linux/macOS) must be
		// dead too. The shell-wrapper part is what suppresses the
		// `Terminate batch job (Y/N)?` prompt on Windows: that prompt is only
		// produced when cmd.exe is signalled (CTRL_C_EVENT) while a .cmd/.bat
		// is on its call stack; force-killing via Win32 TerminateProcess (which
		// is what Process.Kill(entireProcessTree: true) emits) bypasses it.
		var marker = NewMarkerPath();
		var executor = CreateExecutor();

		// The inner process writes its own PID and its parent's PID (the
		// executor-spawned shell wrapper) to a marker file, then sleeps long
		// enough that we have time to cancel.
		//
		// NOTE: the snippet uses only single-quoted PowerShell strings and
		// string concatenation. Embedding double quotes would have to survive
		// THREE quoting layers (C# string literal -> cmd.exe /c arg parsing ->
		// powershell -Command parsing) and is extraordinarily error-prone.
		CommandOrchestrationStep step;
		if (OperatingSystem.IsWindows())
		{
			// Direct child = cmd.exe (executor wraps in `cmd.exe /c`). The
			// powershell process below is the grandchild we explicitly assert
			// has also been killed.
			var pwshSnippet =
				"$ppid = (Get-CimInstance Win32_Process -Filter ('ProcessId=' + $PID)).ParentProcessId; " +
				$"Set-Content -LiteralPath '{marker.Replace("'", "''")}' -Value ($PID.ToString() + '|' + $ppid) -NoNewline; " +
				"Start-Sleep -Seconds 30";
			step = CreateCommandStep(command: "powershell", arguments: ["-NoProfile", "-Command", pwshSnippet]);
		}
		else
		{
			// Direct child = /bin/sh -c "<resolvedLine>". The inner sh spawned
			// from `command: sh` is the grandchild we capture; $PPID is the
			// outer /bin/sh wrapper that the executor spawned directly.
			step = CreateCommandStep(command: "sh", arguments: ["-c", $"printf '%s|%s' \"$$\" \"$PPID\" > '{marker}'; sleep 30"]);
		}

		var context = new OrchestrationExecutionContext { OrchestrationInfo = s_defaultInfo, Parameters = new Dictionary<string, string>() };

		try
		{
			// Act — wait for the inner process to publish its PIDs, then cancel.
			// Waiting on the marker (instead of a fixed timer) eliminates flakiness
			// from cold-process startup variance under parallel CI load.
			using var cts = new CancellationTokenSource();
			var task = executor.ExecuteAsync(step, context, cts.Token);
			await WaitForMarkerAsync(marker, TimeSpan.FromSeconds(15), task);
			cts.Cancel();

			var act = async () => await task;

			// Assert — cancellation surfaces as OperationCanceledException.
			await act.Should().ThrowAsync<OperationCanceledException>();

			// Both the inner command process and the executor-spawned shell
			// wrapper must be dead (kill walked the whole tree).
			var (innerPid, wrapperPid) = await ReadPidPairAsync(marker);
			await AssertProcessExitedAsync(innerPid);
			await AssertProcessExitedAsync(wrapperPid);
		}
		finally
		{
			SafeDelete(marker);
		}
	}

	// ── Test helpers ────────────────────────────────────────────────────────

	/// <summary>
	/// Creates a unique marker file path under %TEMP%. The spawned command
	/// publishes PIDs back to the test via this file so the test does not
	/// have to enumerate child processes (which would be racy under
	/// parallel xUnit execution).
	/// </summary>
	private static string NewMarkerPath() =>
		Path.Combine(Path.GetTempPath(), $"orchestra-test-cmd-marker-{Guid.NewGuid():N}.txt");

	/// <summary>
	/// Polls until <paramref name="markerPath"/> exists or the executor task
	/// completes (whichever comes first). Throws if the marker is not written
	/// within the timeout — that indicates the command failed to start, which
	/// is a real test failure rather than a flaky timing.
	/// </summary>
	private static async Task WaitForMarkerAsync(string markerPath, TimeSpan timeout, Task? executorTask = null)
	{
		var deadline = DateTime.UtcNow.Add(timeout);
		while (DateTime.UtcNow < deadline)
		{
			if (File.Exists(markerPath))
				return;
			if (executorTask is not null && executorTask.IsCompleted)
			{
				// The executor returned before the command even wrote the
				// marker — propagate any exception so the caller sees the
				// real failure (e.g., a quoting bug producing an immediate
				// non-zero exit).
				await executorTask.ConfigureAwait(false);
				throw new Xunit.Sdk.XunitException(
					$"executor returned before marker '{markerPath}' was written; the command may have failed to start.");
			}
			await Task.Delay(25);
		}
		throw new Xunit.Sdk.XunitException(
			$"marker file '{markerPath}' was not written within {timeout.TotalSeconds}s — the spawned command may have failed to start.");
	}

	/// <summary>
	/// Reads a "innerPid|wrapperPid" marker file, retrying briefly to absorb
	/// the small window between cancellation firing and the marker write
	/// fully flushing to disk.
	/// </summary>
	private static async Task<(int innerPid, int wrapperPid)> ReadPidPairAsync(string path)
	{
		var deadline = DateTime.UtcNow.AddSeconds(5);
		string? raw = null;
		while (DateTime.UtcNow < deadline)
		{
			if (File.Exists(path))
			{
				try { raw = await File.ReadAllTextAsync(path); break; }
				catch (IOException) { /* writer not fully flushed; retry */ }
			}
			await Task.Delay(25);
		}

		if (raw is null)
			throw new Xunit.Sdk.XunitException(
				$"marker file '{path}' was never written. The spawned command must publish its PIDs " +
				"before cancellation fires; if it doesn't, either the command failed to start (check " +
				"stdin-sensitive tools like `timeout`) or the cancellation deadline is too short.");

		var parts = raw.Trim().Split('|');
		parts.Should().HaveCountGreaterOrEqualTo(2,
			because: $"marker must contain 'innerPid|wrapperPid' (raw: '{raw}')");
		var innerPid = int.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture);
		var wrapperPid = int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
		return (innerPid, wrapperPid);
	}

	/// <summary>
	/// Polls until the OS reports <paramref name="pid"/> as exited. The Kill
	/// itself is essentially instantaneous, but stdout/stderr drain and the
	/// kernel-side handle teardown add a small (~tens of ms) tail.
	/// </summary>
	private static async Task AssertProcessExitedAsync(int pid)
	{
		var deadline = DateTime.UtcNow.AddSeconds(10);
		while (DateTime.UtcNow < deadline)
		{
			try
			{
				using var p = System.Diagnostics.Process.GetProcessById(pid);
				if (p.HasExited)
					return;
			}
			catch (ArgumentException)
			{
				return; // No such process — already gone, which is the desired state.
			}
			catch (InvalidOperationException)
			{
				return; // Process exited between Get and HasExited.
			}
			await Task.Delay(50);
		}
		throw new Xunit.Sdk.XunitException(
			$"Process {pid} was not terminated within 10s of cancellation. " +
			"The kill-on-cancel path in CommandStepExecutor.ExecuteAsync may have regressed.");
	}

	private static void SafeDelete(string path)
	{
		try { File.Delete(path); }
		catch { /* best effort */ }
	}

	#endregion

	#region StepType Property

	[Fact]
	public void StepType_ReturnsCommand()
	{
		// Arrange
		var executor = CreateExecutor();

		// Assert
		executor.StepType.Should().Be(OrchestrationStepType.Command);
	}

	#endregion

	#region Cross-platform helpers

	/// <summary>Returns a command that echoes text to stdout.</summary>
	private static (string cmd, string[] args) GetEchoCommand(string text) =>
		("echo", [text]);

	/// <summary>Returns a command that prints an environment variable.</summary>
	private static (string cmd, string[] args) GetEnvPrintCommand(string envVarName) =>
		OperatingSystem.IsWindows()
			? ("cmd", ["/c", $"echo %{envVarName}%"])
			: ("printenv", [envVarName]);

	/// <summary>Returns a command that writes to both stdout and stderr.
	/// Uses shell syntax (&&, >&2) so wraps in an explicit shell on both platforms.</summary>
	private static (string cmd, string[] args) GetStdoutAndStderrCommand(string stdoutText, string stderrText) =>
		OperatingSystem.IsWindows()
			? ("cmd", ["/c", $"echo {stdoutText} && echo {stderrText} 1>&2"])
			: ("sh", ["-c", $"echo {stdoutText} && echo {stderrText} >&2"]);

	#endregion
}
