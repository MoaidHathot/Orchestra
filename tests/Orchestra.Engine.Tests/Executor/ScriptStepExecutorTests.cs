using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Orchestra.Engine.Tests.Executor;

public class ScriptStepExecutorTests
{
	private static readonly OrchestrationInfo s_defaultInfo = new("test-orchestration", "1.0.0", "run123", DateTimeOffset.UtcNow);
	private readonly IOrchestrationReporter _reporter = Substitute.For<IOrchestrationReporter>();
	private readonly ILogger<ScriptStepExecutor> _logger = NullLoggerFactory.Instance.CreateLogger<ScriptStepExecutor>();

	private ScriptStepExecutor CreateExecutor() => new(_reporter, _logger);

	private static ScriptOrchestrationStep CreateScriptStep(
		string name = "script-step",
		string shell = "pwsh",
		string? script = null,
		string? scriptFile = null,
		string[]? arguments = null,
		string? workingDirectory = null,
		Dictionary<string, string>? environment = null,
		bool includeStdErr = false,
		string? stdin = null,
		string[]? dependsOn = null,
		string[]? parameters = null,
		bool? strictMode = null) => new()
	{
		Name = name,
		Type = OrchestrationStepType.Script,
		DependsOn = dependsOn ?? [],
		Parameters = parameters ?? [],
		Shell = shell,
		Script = script,
		ScriptFile = scriptFile,
		Arguments = arguments ?? [],
		WorkingDirectory = workingDirectory,
		Environment = environment ?? [],
		IncludeStdErr = includeStdErr,
		Stdin = stdin,
		StrictMode = strictMode,
	};

	#region Success Scenarios

	[Fact]
	public async Task ExecuteAsync_InlinePwshScript_ReturnsSuccessWithStdout()
	{
		// Arrange
		var executor = CreateExecutor();
		var step = CreateScriptStep(
			shell: "pwsh",
			script: "Write-Output 'Hello from Script step'");
		var context = new OrchestrationExecutionContext { OrchestrationInfo = s_defaultInfo, Parameters = new Dictionary<string, string>() };

		// Act
		var result = await executor.ExecuteAsync(step, context);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Succeeded);
		result.Content.Should().Contain("Hello from Script step");
	}

	[Fact]
	public async Task ExecuteAsync_MultilineInlineScript_Succeeds()
	{
		// Arrange
		var executor = CreateExecutor();
		var step = CreateScriptStep(
			shell: "pwsh",
			script: """
				$items = @('alpha', 'beta', 'gamma')
				foreach ($item in $items) {
				    Write-Output $item
				}
				""");
		var context = new OrchestrationExecutionContext { OrchestrationInfo = s_defaultInfo, Parameters = new Dictionary<string, string>() };

		// Act
		var result = await executor.ExecuteAsync(step, context);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Succeeded);
		result.Content.Should().Contain("alpha");
		result.Content.Should().Contain("beta");
		result.Content.Should().Contain("gamma");
	}

	[Fact]
	public async Task ExecuteAsync_ScriptFile_ReturnsSuccessWithStdout()
	{
		// Arrange
		var executor = CreateExecutor();
		var tempFile = Path.Combine(Path.GetTempPath(), $"orchestra-test-{Guid.NewGuid():N}.ps1");
		await File.WriteAllTextAsync(tempFile, "Write-Output 'From script file'");

		try
		{
			var step = CreateScriptStep(
				shell: "pwsh",
				scriptFile: tempFile);
			var context = new OrchestrationExecutionContext { OrchestrationInfo = s_defaultInfo, Parameters = new Dictionary<string, string>() };

			// Act
			var result = await executor.ExecuteAsync(step, context);

			// Assert
			result.Status.Should().Be(ExecutionStatus.Succeeded);
			result.Content.Should().Contain("From script file");
		}
		finally
		{
			File.Delete(tempFile);
		}
	}

	[Fact]
	public async Task ExecuteAsync_ScriptWithArguments_PassesArgumentsToScript()
	{
		// Arrange
		var executor = CreateExecutor();
		var step = CreateScriptStep(
			shell: "pwsh",
			script: "param($Name, $Greeting) Write-Output \"$Greeting $Name\"",
			arguments: ["World", "Hello"]);
		var context = new OrchestrationExecutionContext { OrchestrationInfo = s_defaultInfo, Parameters = new Dictionary<string, string>() };

		// Act
		var result = await executor.ExecuteAsync(step, context);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Succeeded);
		result.Content.Should().Contain("Hello World");
	}

	#endregion

	#region Failure Scenarios

	[Fact]
	public async Task ExecuteAsync_ScriptFileNotFound_ReturnsFailedResult()
	{
		// Arrange
		var executor = CreateExecutor();
		var step = CreateScriptStep(
			shell: "pwsh",
			scriptFile: "/nonexistent/path/script.ps1");
		var context = new OrchestrationExecutionContext { OrchestrationInfo = s_defaultInfo, Parameters = new Dictionary<string, string>() };

		// Act
		var result = await executor.ExecuteAsync(step, context);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Failed);
		result.ErrorMessage.Should().Contain("not found");
	}

	[Fact]
	public async Task ExecuteAsync_ScriptWithError_ReturnsFailedResult()
	{
		// Arrange
		var executor = CreateExecutor();
		var step = CreateScriptStep(
			shell: "pwsh",
			script: "throw 'Intentional error'");
		var context = new OrchestrationExecutionContext { OrchestrationInfo = s_defaultInfo, Parameters = new Dictionary<string, string>() };

		// Act
		var result = await executor.ExecuteAsync(step, context);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Failed);
		result.ErrorMessage.Should().Contain("Intentional error");
	}

	[Fact]
	public async Task ExecuteAsync_UnknownShellNotInstalled_ReturnsFailedResult()
	{
		// Arrange
		var executor = CreateExecutor();
		var step = CreateScriptStep(
			shell: "nonexistent-shell-xyz-123",
			script: "hello");
		var context = new OrchestrationExecutionContext { OrchestrationInfo = s_defaultInfo, Parameters = new Dictionary<string, string>() };

		// Act
		var result = await executor.ExecuteAsync(step, context);

		// Assert
		// The executor should fail gracefully — either the shell is not found or the process fails
		result.Status.Should().Be(ExecutionStatus.Failed);
	}

	#endregion

	#region Template Resolution

	[Fact]
	public async Task ExecuteAsync_TemplateInScript_ResolvesParameters()
	{
		// Arrange
		var executor = CreateExecutor();
		var step = CreateScriptStep(
			shell: "pwsh",
			script: "Write-Output '{{param.message}}'",
			parameters: ["message"]);
		var context = new OrchestrationExecutionContext
		{
			OrchestrationInfo = s_defaultInfo,
			Parameters = new Dictionary<string, string>
			{
				["message"] = "resolved-template-value"
			}
		};

		// Act
		var result = await executor.ExecuteAsync(step, context);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Succeeded);
		result.Content.Should().Contain("resolved-template-value");
	}

	[Fact]
	public async Task ExecuteAsync_TemplateInArguments_ResolvesParameters()
	{
		// Arrange
		var executor = CreateExecutor();
		var step = CreateScriptStep(
			shell: "pwsh",
			script: "param($Value) Write-Output $Value",
			arguments: ["{{param.argValue}}"],
			parameters: ["argValue"]);
		var context = new OrchestrationExecutionContext
		{
			OrchestrationInfo = s_defaultInfo,
			Parameters = new Dictionary<string, string>
			{
				["argValue"] = "resolved-arg"
			}
		};

		// Act
		var result = await executor.ExecuteAsync(step, context);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Succeeded);
		result.Content.Should().Contain("resolved-arg");
	}

	[Fact]
	public async Task ExecuteAsync_DependencyOutput_ResolvesInScript()
	{
		// Arrange
		var executor = CreateExecutor();
		var step = CreateScriptStep(
			shell: "pwsh",
			script: "Write-Output '{{step1.output}}'",
			dependsOn: ["step1"]);
		var context = new OrchestrationExecutionContext { OrchestrationInfo = s_defaultInfo, Parameters = new Dictionary<string, string>() };
		context.AddResult("step1", ExecutionResult.Succeeded("dependency-data"));

		// Act
		var result = await executor.ExecuteAsync(step, context);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Succeeded);
		result.Content.Should().Contain("dependency-data");
	}

	#endregion

	#region Environment Variables

	[Fact]
	public async Task ExecuteAsync_WithEnvironmentVariables_SetsEnvironment()
	{
		// Arrange
		var executor = CreateExecutor();
		var step = CreateScriptStep(
			shell: "pwsh",
			script: "Write-Output $env:ORCHESTRA_SCRIPT_TEST",
			environment: new Dictionary<string, string>
			{
				["ORCHESTRA_SCRIPT_TEST"] = "env-value-script"
			});
		var context = new OrchestrationExecutionContext { OrchestrationInfo = s_defaultInfo, Parameters = new Dictionary<string, string>() };

		// Act
		var result = await executor.ExecuteAsync(step, context);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Succeeded);
		result.Content.Should().Contain("env-value-script");
	}

	[Fact]
	public async Task ExecuteAsync_TraceIncludesResolvedProcessDetails()
	{
		// Arrange
		var executor = CreateExecutor();
		var step = CreateScriptStep(
			shell: "pwsh",
			script: "param($Value) Write-Output \"arg=$Value env=$env:ORCHESTRA_SCRIPT_TRACE stdin=$([Console]::In.ReadToEnd())\"",
			arguments: ["{{param.argValue}}"],
			environment: new Dictionary<string, string>
			{
				["ORCHESTRA_SCRIPT_TRACE"] = "{{param.envValue}}"
			},
			stdin: "{{param.stdinValue}}",
			parameters: ["argValue", "envValue", "stdinValue"]);
		var context = new OrchestrationExecutionContext
		{
			OrchestrationInfo = s_defaultInfo,
			Parameters = new Dictionary<string, string>
			{
				["argValue"] = "resolved arg",
				["envValue"] = "resolved env",
				["stdinValue"] = "resolved stdin",
			}
		};

		// Act
		var result = await executor.ExecuteAsync(step, context);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Succeeded);
		result.Trace.Should().NotBeNull();
		result.Trace!.Shell.Should().Be("pwsh");
		result.Trace.ScriptSource.Should().Be("(inline)");
		result.Trace.CommandArguments.Should().Contain("resolved arg");
		result.Trace.Environment.Should().ContainKey("ORCHESTRA_SCRIPT_TRACE").WhoseValue.Should().Be("resolved env");
		result.Trace.Stdin.Should().Be("resolved stdin");
		result.Trace.FinalResponse.Should().Contain("arg=resolved arg");
		result.Trace.FinalResponse.Should().Contain("env=resolved env");
		result.Trace.FinalResponse.Should().Contain("stdin=resolved stdin");
	}

	#endregion

	#region IncludeStdErr

	[Fact]
	public async Task ExecuteAsync_IncludeStdErr_CapturesBothStreams()
	{
		// Arrange — opt out of the strict-mode prologue so that pwsh's default
		// non-terminating Write-Error behaviour is preserved (the script keeps
		// running after Write-Error and pwsh exits 0). With strict mode (the
		// default for pwsh) Write-Error would be promoted to a terminating
		// error and exit non-zero; that scenario is exercised separately.
		var executor = CreateExecutor();
		var step = CreateScriptStep(
			shell: "pwsh",
			script: "Write-Output 'stdout-data'; Write-Error 'stderr-data'",
			includeStdErr: true,
			strictMode: false);
		var context = new OrchestrationExecutionContext { OrchestrationInfo = s_defaultInfo, Parameters = new Dictionary<string, string>() };

		// Act
		var result = await executor.ExecuteAsync(step, context);

		// Assert — with strictMode opt-out, pwsh emits the Write-Error record to
		// stderr but exits 0, so the step succeeds and the captured content
		// contains stdout.
		result.Status.Should().Be(ExecutionStatus.Succeeded);
		result.Content.Should().Contain("stdout-data");
	}

	[Fact]
	public async Task ExecuteAsync_TraceSeparatesStdoutAndStderr()
	{
		// Arrange — opt out of strict mode so writing to stderr directly via
		// [Console]::Error.WriteLine doesn't trigger the trap (it doesn't write
		// to the PowerShell error stream, but the trap injection still affects
		// downstream behaviour, so we keep the opt-out for parity with the
		// historical test contract).
		var executor = CreateExecutor();
		var step = CreateScriptStep(
			shell: "pwsh",
			script: "Write-Output 'stdout-trace'; [Console]::Error.WriteLine('stderr-trace')",
			includeStdErr: true,
			strictMode: false);
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
		// Arrange — opt out of strict mode (see ExecuteAsync_IncludeStdErr_CapturesBothStreams).
		var executor = CreateExecutor();
		var step = CreateScriptStep(
			shell: "pwsh",
			script: "Write-Output 'visible'; [Console]::Error.WriteLine('hidden')",
			includeStdErr: false,
			strictMode: false);
		var context = new OrchestrationExecutionContext { OrchestrationInfo = s_defaultInfo, Parameters = new Dictionary<string, string>() };

		// Act
		var result = await executor.ExecuteAsync(step, context);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Succeeded);
		result.Content.Should().Contain("visible");
		result.Content.Should().NotContain("hidden");
	}

	#endregion

	#region Stdin

	[Fact]
	public async Task ExecuteAsync_WithStdin_PipesContentToProcess()
	{
		// Arrange
		var executor = CreateExecutor();
		var step = CreateScriptStep(
			shell: "pwsh",
			script: "$inputContent = [Console]::In.ReadToEnd(); Write-Output \"Got: $inputContent\"",
			stdin: "piped-data");
		var context = new OrchestrationExecutionContext { OrchestrationInfo = s_defaultInfo, Parameters = new Dictionary<string, string>() };

		// Act
		var result = await executor.ExecuteAsync(step, context);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Succeeded);
		result.Content.Should().Contain("piped-data");
	}

	#endregion

	#region Wrong Step Type

	[Fact]
	public async Task ExecuteAsync_WrongStepType_ThrowsInvalidOperationException()
	{
		// Arrange
		var executor = CreateExecutor();
		var wrongStep = new CommandOrchestrationStep
		{
			Name = "wrong-step",
			Type = OrchestrationStepType.Command,
			DependsOn = [],
			Command = "echo",
		};
		var context = new OrchestrationExecutionContext { OrchestrationInfo = s_defaultInfo, Parameters = new Dictionary<string, string>() };

		// Act
		var act = () => executor.ExecuteAsync(wrongStep, context);

		// Assert
		await act.Should().ThrowAsync<InvalidOperationException>()
			.WithMessage("*ScriptStepExecutor*CommandOrchestrationStep*ScriptOrchestrationStep*");
	}

	#endregion

	#region Cancellation

	[Fact]
	public async Task ExecuteAsync_Cancellation_ThrowsOperationCanceledException()
	{
		// Arrange — also verifies the kill-on-cancel contract: after the
		// OperationCanceledException propagates, (a) the spawned pwsh process
		// must be dead (no orphaned strays), and (b) the temp script file must
		// be deleted (pwsh held a read handle on it, so File.Delete in the
		// executor's finally only succeeds if we actually terminate pwsh first).
		var marker = NewMarkerPath();
		var executor = CreateExecutor();
		var step = CreateScriptStep(
			shell: "pwsh",
			script: $$"""
				Set-Content -LiteralPath '{{EscapeForPwsh(marker)}}' -Value "$PID|$PSCommandPath" -NoNewline
				Start-Sleep -Seconds 30
				Write-Output 'should-not-reach-here'
				""");
		var context = new OrchestrationExecutionContext { OrchestrationInfo = s_defaultInfo, Parameters = new Dictionary<string, string>() };

		try
		{
			// Act — wait for the script to publish its PID, then cancel. Waiting
			// on the marker (instead of a fixed timer) eliminates flakiness from
			// cold pwsh startup variance under parallel CI load: cancellation
			// only fires after the script has actually captured the data we need.
			await RunAndAssertCancellationKillsAsync(
				executor, step, context, marker,
				assertions: async (childPid, tempScriptPath) =>
				{
					// Assert — the spawned pwsh process was force-killed by the executor.
					await AssertProcessExitedAsync(childPid);

					// Assert — the temp script file was deleted, even though pwsh held it open.
					File.Exists(tempScriptPath).Should().BeFalse(
						$"the temp script file '{tempScriptPath}' must be cleaned up after cancellation; if it remains, pwsh was not terminated before File.Delete ran.");
				});
		}
		finally
		{
			SafeDelete(marker);
		}
	}

	[Fact]
	public async Task ExecuteAsync_Cancellation_KillsEntireProcessTreeIncludingGrandchildren()
	{
		// Arrange — the real-world failure mode is not a hung pwsh; it's the
		// cmd.exe grandchild that pwsh transparently spawns to invoke an
		// `az.cmd`/`gh.cmd`/`npm.cmd` shim. Without entireProcessTree, the
		// pwsh dies and the cmd.exe is reparented to the system as an orphan
		// (and on a real Orchestra host, sits on the unanswerable
		// "Terminate batch job (Y/N)?" prompt). This test reproduces that
		// topology and asserts that the kill walks the tree.
		if (!OperatingSystem.IsWindows())
			return; // cmd.exe is Windows-only.

		var marker = NewMarkerPath();
		var executor = CreateExecutor();
		var step = CreateScriptStep(
			shell: "pwsh",
			// Spawn a real cmd.exe grandchild (mirrors the az.cmd shim case),
			// capture both PIDs to the marker, then wait long enough that we
			// have time to cancel before it exits on its own.
			//
			// `ping -n 30 127.0.0.1` is used rather than `timeout /t 30` because
			// Orchestra spawns pwsh with redirected stdout/stderr; under that
			// configuration `timeout` immediately fails with "Input redirection
			// is not supported, exiting the process immediately." `ping` has no
			// such dependency and reliably keeps the cmd.exe grandchild alive
			// for ~30 seconds.
			script: $$"""
				$cmdProc = Start-Process -FilePath 'cmd.exe' -ArgumentList '/c','ping -n 30 127.0.0.1 >NUL' -PassThru -NoNewWindow
				Set-Content -LiteralPath '{{EscapeForPwsh(marker)}}' -Value "$PID|$($cmdProc.Id)|$PSCommandPath" -NoNewline
				Wait-Process -Id $cmdProc.Id
				""");
		var context = new OrchestrationExecutionContext { OrchestrationInfo = s_defaultInfo, Parameters = new Dictionary<string, string>() };

		try
		{
			using var cts = new CancellationTokenSource();
			var task = executor.ExecuteAsync(step, context, cts.Token);
			await WaitForMarkerAsync(marker, TimeSpan.FromSeconds(15), task);
			cts.Cancel();

			var act = async () => await task;
			await act.Should().ThrowAsync<OperationCanceledException>();

			var rawMarker = await ReadAllTextWithRetryAsync(marker);
			var parts = rawMarker.Trim().Split('|');
			parts.Should().HaveCountGreaterOrEqualTo(3,
				because: $"script must have captured pwshPid|cmdPid|scriptPath before sleeping (raw marker: '{rawMarker}')");
			var pwshPid = int.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture);
			var cmdPid = int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
			var tempScriptPath = parts[2];

			// Both the direct child (pwsh) and the grandchild (cmd.exe) must be dead.
			await AssertProcessExitedAsync(pwshPid);
			await AssertProcessExitedAsync(cmdPid);

			// And the temp script file must be gone, even though pwsh held it open.
			File.Exists(tempScriptPath).Should().BeFalse(
				$"the temp script file '{tempScriptPath}' must be cleaned up after cancellation");
		}
		finally
		{
			SafeDelete(marker);
		}
	}

	[Fact]
	public async Task ExecuteAsync_Cancellation_DeletesTempFileEvenWhenChildHoldsItOpen()
	{
		// Arrange — focused regression for the pwsh `-File` lock semantics: pwsh
		// opens the script with FILE_SHARE_READ but not FILE_SHARE_DELETE on
		// Windows, so a swallowed File.Delete in the executor's finally was the
		// historical leak source. After the kill-on-cancel fix, pwsh is dead by
		// the time File.Delete runs and the file is removed.
		var marker = NewMarkerPath();
		var executor = CreateExecutor();
		var step = CreateScriptStep(
			shell: "pwsh",
			script: $$"""
				Set-Content -LiteralPath '{{EscapeForPwsh(marker)}}' -Value $PSCommandPath -NoNewline
				Start-Sleep -Seconds 30
				""");
		var context = new OrchestrationExecutionContext { OrchestrationInfo = s_defaultInfo, Parameters = new Dictionary<string, string>() };

		try
		{
			using var cts = new CancellationTokenSource();
			var task = executor.ExecuteAsync(step, context, cts.Token);
			await WaitForMarkerAsync(marker, TimeSpan.FromSeconds(15), task);
			cts.Cancel();

			var act = async () => await task;
			await act.Should().ThrowAsync<OperationCanceledException>();

			var tempScriptPath = (await ReadAllTextWithRetryAsync(marker)).Trim();
			tempScriptPath.Should().NotBeNullOrWhiteSpace();
			tempScriptPath.Should().MatchRegex(@"orchestra-[0-9a-f]{32}\.ps1$",
				because: "the temp file path captured from $PSCommandPath should match the Orchestra naming convention");

			File.Exists(tempScriptPath).Should().BeFalse(
				$"the temp script file '{tempScriptPath}' must be deleted by the executor's finally after cancellation kills pwsh");
		}
		finally
		{
			SafeDelete(marker);
		}
	}

	// ── Test helpers ────────────────────────────────────────────────────────

	/// <summary>
	/// Creates a unique marker file path under %TEMP%. Used so a cancelled
	/// pwsh script can publish its PID and $PSCommandPath back to the test
	/// before sleeping, without having to enumerate the parent's child PIDs
	/// (which is racy when xUnit runs tests in parallel).
	/// </summary>
	private static string NewMarkerPath() =>
		Path.Combine(Path.GetTempPath(), $"orchestra-test-marker-{Guid.NewGuid():N}.txt");

	/// <summary>
	/// Escapes a Windows path for embedding in a single-quoted PowerShell string
	/// literal. Single quotes inside single-quoted pwsh strings are escaped by
	/// doubling them; backslashes are literal so no further escaping is needed.
	/// </summary>
	private static string EscapeForPwsh(string path) => path.Replace("'", "''");

	/// <summary>
	/// Drives a cancellation test deterministically: starts the executor,
	/// waits for the script to publish its marker (proving the script has
	/// reached the post-marker statements), then cancels and asserts that
	/// (a) the cancellation throws <see cref="OperationCanceledException"/>,
	/// (b) the captured pwsh PID is dead, and (c) the temp script file is
	/// deleted. The marker-driven trigger eliminates cold-start timing
	/// flakiness under parallel CI load — cancellation only fires after the
	/// script has actually captured the data we need.
	/// </summary>
	private static async Task RunAndAssertCancellationKillsAsync(
		ScriptStepExecutor executor,
		ScriptOrchestrationStep step,
		OrchestrationExecutionContext context,
		string markerPath,
		Func<int, string, Task> assertions)
	{
		using var cts = new CancellationTokenSource();
		var task = executor.ExecuteAsync(step, context, cts.Token);

		await WaitForMarkerAsync(markerPath, TimeSpan.FromSeconds(15), task);

		cts.Cancel();

		var act = async () => await task;
		await act.Should().ThrowAsync<OperationCanceledException>();

		var (pid, tempScriptPath) = await ReadPidAndPathMarkerAsync(markerPath);
		await assertions(pid, tempScriptPath);
	}

	/// <summary>
	/// Polls until <paramref name="markerPath"/> exists or the executor task
	/// completes (whichever comes first). Throws if the marker is not
	/// written within the timeout — that indicates the script failed to
	/// start, which is a real test failure rather than a flaky timing.
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
				// The executor returned before the script even wrote the
				// marker — propagate any exception so the caller sees the
				// real failure (e.g., script syntax error, missing pwsh).
				await executorTask.ConfigureAwait(false);
				throw new Xunit.Sdk.XunitException(
					$"executor returned before marker '{markerPath}' was written; the script may have failed to start.");
			}
			await Task.Delay(25);
		}
		throw new Xunit.Sdk.XunitException(
			$"marker file '{markerPath}' was not written within {timeout.TotalSeconds}s — the spawned pwsh may have failed to start.");
	}

	/// <summary>
	/// Reads a marker file written by the test script. Polls briefly because
	/// the writer may not yet have flushed even though the file exists.
	/// </summary>
	private static async Task<string> ReadAllTextWithRetryAsync(string path)
	{
		var deadline = DateTime.UtcNow.AddSeconds(5);
		while (DateTime.UtcNow < deadline)
		{
			if (File.Exists(path))
			{
				try { return await File.ReadAllTextAsync(path); }
				catch (IOException) { /* writer not fully flushed; retry */ }
			}
			await Task.Delay(25);
		}
		throw new Xunit.Sdk.XunitException($"marker file '{path}' was never written by the test script — it may have been cancelled before reaching the marker statement.");
	}

	/// <summary>
	/// Parses a "pid|scriptPath" marker into its components.
	/// </summary>
	private static async Task<(int pid, string tempScriptPath)> ReadPidAndPathMarkerAsync(string path)
	{
		var raw = (await ReadAllTextWithRetryAsync(path)).Trim();
		var parts = raw.Split('|');
		parts.Should().HaveCountGreaterOrEqualTo(2,
			because: $"marker must contain 'pid|scriptPath' (raw: '{raw}')");
		var pid = int.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture);
		return (pid, parts[1]);
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
			"The kill-on-cancel path in ScriptStepExecutor.ExecuteAsync may have regressed.");
	}

	private static void SafeDelete(string path)
	{
		try { File.Delete(path); }
		catch { /* best effort */ }
	}

	#endregion

	#region StepType Property

	[Fact]
	public void StepType_ReturnsScript()
	{
		// Arrange
		var executor = CreateExecutor();

		// Assert
		executor.StepType.Should().Be(OrchestrationStepType.Script);
	}

	#endregion

	#region Trace

	[Fact]
	public async Task ExecuteAsync_Success_ReportsTrace()
	{
		// Arrange
		var executor = CreateExecutor();
		var step = CreateScriptStep(
			shell: "pwsh",
			script: "Write-Output 'trace-test'");
		var context = new OrchestrationExecutionContext { OrchestrationInfo = s_defaultInfo, Parameters = new Dictionary<string, string>() };

		// Act
		var result = await executor.ExecuteAsync(step, context);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Succeeded);
		result.Trace.Should().NotBeNull();
		result.Trace!.SystemPrompt.Should().Contain("pwsh");
		result.Trace.FinalResponse.Should().Contain("trace-test");
	}

	#endregion

	#region TempFile Cleanup

	[Fact]
	public async Task ExecuteAsync_InlineScript_CleansTempFile()
	{
		// Arrange — capture the actual temp script path via $PSCommandPath
		// inside the script so we can assert deletion of the exact file we
		// created (rather than a flaky "no orchestra-*.ps1 leftover anywhere"
		// check that races against other test runs).
		var marker = NewMarkerPath();
		var executor = CreateExecutor();
		var step = CreateScriptStep(
			shell: "pwsh",
			script: $$"""
				Set-Content -LiteralPath '{{EscapeForPwsh(marker)}}' -Value $PSCommandPath -NoNewline
				Write-Output 'cleanup-test'
				""");
		var context = new OrchestrationExecutionContext { OrchestrationInfo = s_defaultInfo, Parameters = new Dictionary<string, string>() };

		try
		{
			// Act
			var result = await executor.ExecuteAsync(step, context);

			// Assert — the step succeeded …
			result.Status.Should().Be(ExecutionStatus.Succeeded);
			result.Content.Should().Contain("cleanup-test");

			// … and the exact temp file the executor created has been deleted.
			var tempScriptPath = (await ReadAllTextWithRetryAsync(marker)).Trim();
			tempScriptPath.Should().MatchRegex(@"orchestra-[0-9a-f]{32}\.ps1$",
				because: "the temp file path captured from $PSCommandPath should match the Orchestra naming convention");
			File.Exists(tempScriptPath).Should().BeFalse(
				$"the temp script file '{tempScriptPath}' must be cleaned up after successful execution");
		}
		finally
		{
			SafeDelete(marker);
		}
	}

	#endregion

	#region ANSI Sanitization

	[Fact]
	public async Task ExecuteAsync_PwshErrorWithAnsiFormatting_StripsEscapeSequencesFromOutput()
	{
		// Arrange — `throw` triggers PowerShell 7's ConciseView error formatter, which
		// historically emits ANSI escape sequences (red/cyan colors) even on a redirected
		// stdout. Without sanitization, the captured stderr would contain literal noise
		// like "[31;1m" and "[0m". After our changes, both NO_COLOR=1 should suppress
		// the codes at the source AND AnsiSanitizer.Strip should defensively scrub anything
		// that slips through.
		var executor = CreateExecutor();
		var step = CreateScriptStep(
			shell: "pwsh",
			script: "throw 'ansi-fixture-error-message'",
			includeStdErr: true);
		var context = new OrchestrationExecutionContext { OrchestrationInfo = s_defaultInfo, Parameters = new Dictionary<string, string>() };

		// Act
		var result = await executor.ExecuteAsync(step, context);

		// Assert — the script should fail (non-zero exit because of the throw),
		// but the captured error text must be ANSI-free.
		result.Status.Should().Be(ExecutionStatus.Failed);
		result.ErrorMessage.Should().Contain("ansi-fixture-error-message");
		result.ErrorMessage.Should().NotContain("\x1B");
		result.ErrorMessage.Should().NotContain("[31;1m");
		result.ErrorMessage.Should().NotContain("[36;1m");
		result.ErrorMessage.Should().NotContain("[0m");

		// Trace data shown in the viewer must also be clean.
		result.Trace.Should().NotBeNull();
		result.Trace!.FinalResponse.Should().NotContain("\x1B");
		foreach (var segment in result.Trace.ResponseSegments)
		{
			segment.Should().NotContain("\x1B");
			segment.Should().NotContain("[31;1m");
			segment.Should().NotContain("[0m");
		}
	}

	[Fact]
	public async Task ExecuteAsync_PwshExplicitlyEmittingAnsi_StillSanitizesOutput()
	{
		// Arrange — even if a script explicitly writes raw ANSI escape bytes
		// (some tools do this regardless of NO_COLOR), the defensive sanitizer
		// must remove them from the captured stdout.
		var executor = CreateExecutor();
		var step = CreateScriptStep(
			shell: "pwsh",
			script: """
				$esc = [char]27
				Write-Output "$esc[31;1mred-text$esc[0m and $esc[32mgreen-text$esc[0m"
				""");
		var context = new OrchestrationExecutionContext { OrchestrationInfo = s_defaultInfo, Parameters = new Dictionary<string, string>() };

		// Act
		var result = await executor.ExecuteAsync(step, context);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Succeeded);
		result.Content.Should().Contain("red-text");
		result.Content.Should().Contain("green-text");
		result.Content.Should().NotContain("\x1B");
		result.Content.Should().NotContain("[31;1m");
		result.Content.Should().NotContain("[32m");
		result.Content.Should().NotContain("[0m");

		// Trace's stdout (FinalResponse) is the same captured buffer — also clean.
		result.Trace.Should().NotBeNull();
		result.Trace!.FinalResponse.Should().NotContain("\x1B");
	}

	[Fact]
	public async Task ExecuteAsync_NoColorEnvironmentVariable_IsSetForChildProcess()
	{
		// Arrange — confirm that the child process actually sees NO_COLOR=1 and
		// TERM=dumb so that downstream tools (git, gh, npm, etc.) honor them.
		var executor = CreateExecutor();
		var step = CreateScriptStep(
			shell: "pwsh",
			script: "Write-Output \"NO_COLOR=$env:NO_COLOR;TERM=$env:TERM\"");
		var context = new OrchestrationExecutionContext { OrchestrationInfo = s_defaultInfo, Parameters = new Dictionary<string, string>() };

		// Act
		var result = await executor.ExecuteAsync(step, context);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Succeeded);
		result.Content.Should().Contain("NO_COLOR=1");
		result.Content.Should().Contain("TERM=dumb");
	}

	[Fact]
	public async Task ExecuteAsync_UserOverridesNoColor_RespectsUserValue()
	{
		// Arrange — orchestration authors must be able to override NO_COLOR/TERM
		// via the step's Environment section if they have a tool that needs it.
		var executor = CreateExecutor();
		var step = CreateScriptStep(
			shell: "pwsh",
			script: "Write-Output \"NO_COLOR=$env:NO_COLOR;TERM=$env:TERM\"",
			environment: new Dictionary<string, string>
			{
				["NO_COLOR"] = "",
				["TERM"] = "xterm-256color",
			});
		var context = new OrchestrationExecutionContext { OrchestrationInfo = s_defaultInfo, Parameters = new Dictionary<string, string>() };

		// Act
		var result = await executor.ExecuteAsync(step, context);

		// Assert — user values must win over our defaults.
		result.Status.Should().Be(ExecutionStatus.Succeeded);
		result.Content.Should().Contain("NO_COLOR=;");
		result.Content.Should().Contain("TERM=xterm-256color");
	}

	#endregion

	#region Prologue Injection (pwsh / powershell)

	[Fact]
	public void GetPowerShellPrologue_PwshShell_Default_ReturnsDefaultPrologue()
	{
		ScriptStepExecutor.GetPowerShellPrologue("pwsh", explicitOptIn: null)
			.Should().Be(ScriptStepExecutor.PowerShellDefaultPrologue);
		ScriptStepExecutor.GetPowerShellPrologue("PWSH", explicitOptIn: null)
			.Should().Be(ScriptStepExecutor.PowerShellDefaultPrologue);
		ScriptStepExecutor.GetPowerShellPrologue("powershell", explicitOptIn: null)
			.Should().Be(ScriptStepExecutor.PowerShellDefaultPrologue);
	}

	[Fact]
	public void GetPowerShellPrologue_PwshShell_StrictModeTrue_ReturnsStrictPrologue()
	{
		ScriptStepExecutor.GetPowerShellPrologue("pwsh", explicitOptIn: true)
			.Should().Be(ScriptStepExecutor.PowerShellStrictPrologue);
		ScriptStepExecutor.GetPowerShellPrologue("powershell", explicitOptIn: true)
			.Should().Be(ScriptStepExecutor.PowerShellStrictPrologue);
	}

	[Fact]
	public void GetPowerShellPrologue_StrictModeFalse_AlwaysReturnsNull()
	{
		// Explicit opt-out must work for the shells that would otherwise auto-opt-in.
		// This is the documented escape hatch for scripts that intentionally write to
		// stderr and expect a zero exit code.
		ScriptStepExecutor.GetPowerShellPrologue("pwsh", explicitOptIn: false).Should().BeNull();
		ScriptStepExecutor.GetPowerShellPrologue("powershell", explicitOptIn: false).Should().BeNull();
		ScriptStepExecutor.GetPowerShellPrologue("bash", explicitOptIn: false).Should().BeNull();
	}

	[Fact]
	public void GetPowerShellPrologue_NonPowerShellShell_AlwaysReturnsNull()
	{
		// Non-PowerShell shells receive no prologue regardless of strictMode value
		// because the engine does not ship a prologue for those interpreters.
		ScriptStepExecutor.GetPowerShellPrologue("bash", explicitOptIn: null).Should().BeNull();
		ScriptStepExecutor.GetPowerShellPrologue("python", explicitOptIn: null).Should().BeNull();
		ScriptStepExecutor.GetPowerShellPrologue("node", explicitOptIn: null).Should().BeNull();
		ScriptStepExecutor.GetPowerShellPrologue("unknown-shell", explicitOptIn: null).Should().BeNull();
		ScriptStepExecutor.GetPowerShellPrologue("bash", explicitOptIn: true).Should().BeNull();
		ScriptStepExecutor.GetPowerShellPrologue("python", explicitOptIn: true).Should().BeNull();
	}

	[Fact]
	public void PowerShellDefaultPrologue_DoesNotIncludeSetStrictMode()
	{
		// Regression guard: the default prologue must NOT include Set-StrictMode because
		// most production scripts read optional properties off ConvertFrom-Json output,
		// a pattern that throws under strict mode v3+ (regression from run f3c03a951a2d).
		ScriptStepExecutor.PowerShellDefaultPrologue.Should().Contain("$ErrorActionPreference='Stop'");
		ScriptStepExecutor.PowerShellDefaultPrologue.Should().Contain("trap {");
		ScriptStepExecutor.PowerShellDefaultPrologue.Should().NotContain("Set-StrictMode");
	}

	[Fact]
	public void PowerShellStrictPrologue_IncludesSetStrictMode()
	{
		// strictMode: true must additionally include Set-StrictMode -Version Latest so
		// scripts written with strict-mode discipline can opt in to the extra checks.
		ScriptStepExecutor.PowerShellStrictPrologue.Should().Contain("$ErrorActionPreference='Stop'");
		ScriptStepExecutor.PowerShellStrictPrologue.Should().Contain("Set-StrictMode -Version Latest");
		ScriptStepExecutor.PowerShellStrictPrologue.Should().Contain("trap {");
	}

	[Fact]
	public void PowerShellPrologues_AreSinglePhysicalLine()
	{
		// Regression guard: the prologue must stay on one physical line so the user
		// script's original line numbers shift by exactly 1, preserving operator
		// muscle-memory for line:col references between source and resolved scripts.
		ScriptStepExecutor.PowerShellDefaultPrologue.Should().NotContain("\n");
		ScriptStepExecutor.PowerShellDefaultPrologue.Should().NotContain("\r");
		ScriptStepExecutor.PowerShellStrictPrologue.Should().NotContain("\n");
		ScriptStepExecutor.PowerShellStrictPrologue.Should().NotContain("\r");
	}

	[Fact]
	public void PowerShellPrologues_EmitStructuredDiagnosticMarker()
	{
		// Regression guard for run dbbadcb778b8: the previous trap (Write-Error -ErrorRecord)
		// pointed operators at the engine-injected prologue rather than the user's failing
		// line, because pwsh's standard error renderer attributes the location to the trap
		// statement itself (always line 1 in the resolved file) and truncated long source
		// lines with U+2026 markers that captured stderr could not represent. The current
		// prologue writes a structured ORCHESTRA-PWSH-ERROR: <file>:<line>:<col>: <msg>
		// line directly to stderr so the inner script location is preserved verbatim.
		ScriptStepExecutor.PowerShellDefaultPrologue.Should().Contain("ORCHESTRA-PWSH-ERROR:");
		ScriptStepExecutor.PowerShellDefaultPrologue.Should().Contain("[Console]::Error.WriteLine");
		ScriptStepExecutor.PowerShellDefaultPrologue.Should().Contain("$($r.InvocationInfo.ScriptLineNumber)");
		ScriptStepExecutor.PowerShellDefaultPrologue.Should().NotContain("Write-Error -ErrorRecord",
			"the misleading pwsh error renderer prefix must not be reintroduced");

		ScriptStepExecutor.PowerShellStrictPrologue.Should().Contain("ORCHESTRA-PWSH-ERROR:");
		ScriptStepExecutor.PowerShellStrictPrologue.Should().Contain("[Console]::Error.WriteLine");
		ScriptStepExecutor.PowerShellStrictPrologue.Should().Contain("$($r.InvocationInfo.ScriptLineNumber)");
		ScriptStepExecutor.PowerShellStrictPrologue.Should().NotContain("Write-Error -ErrorRecord");
	}

	[Fact]
	public void BuildPowerShellPreamble_Pwsh_PrependsUtf8OutputEncodingForEveryStrictModeSetting()
	{
		// Regression guard for the raindrop-tracker failure (run bb4e5c787a0a): every
		// pwsh/powershell step must switch its output streams to UTF-8 BEFORE any user output,
		// independent of the strict-mode setting (including strictMode:false, where the
		// error-handling prologue is null). Otherwise pwsh encodes stdout with the Windows OEM
		// code page and best-fit mangles non-ASCII characters — collapsing curly quotes into an
		// unescaped ASCII '"' that breaks downstream JSON parsing.
		foreach (var shell in new[] { "pwsh", "powershell" })
		{
			foreach (var strict in new bool?[] { null, true, false })
			{
				var preamble = ScriptStepExecutor.BuildPowerShellPreamble(shell, strict);
				preamble.Should().NotBeNull($"pwsh-family shells always get a preamble (shell={shell}, strict={strict})");
				preamble!.Should().StartWith(ScriptStepExecutor.PowerShellOutputEncodingPrologue,
					"the encoding switch must run before the control helpers and error-handling prologue");
				preamble.Should().Contain("[Console]::OutputEncoding");
				preamble.Should().Contain("UTF8Encoding");
			}
		}
	}

	[Fact]
	public void BuildPowerShellPreamble_NonPowerShellShell_ReturnsNull()
	{
		// Non-pwsh shells (bash/python/node) are UTF-8 by default and receive no injection.
		ScriptStepExecutor.BuildPowerShellPreamble("bash", null).Should().BeNull();
		ScriptStepExecutor.BuildPowerShellPreamble("python", true).Should().BeNull();
		ScriptStepExecutor.BuildPowerShellPreamble("node", false).Should().BeNull();
	}

	[Fact]
	public void PowerShellOutputEncodingPrologue_IsSinglePhysicalLine()
	{
		// Same one-physical-line contract as the other prologues so injecting it shifts the
		// user's script by exactly one line, preserving line:col references in error output.
		ScriptStepExecutor.PowerShellOutputEncodingPrologue.Should().NotContain("\n");
		ScriptStepExecutor.PowerShellOutputEncodingPrologue.Should().NotContain("\r");
	}

	[Fact]
	public async Task ExecuteAsync_PwshEmitsJsonWithNonAsciiPunctuation_RoundTripsAsValidUtf8Json()
	{
		// End-to-end regression guard for the raindrop-tracker failure (run bb4e5c787a0a): a pwsh
		// step serialized a raindrop whose excerpt contained curly quotes U+201C/U+201D. With the
		// child's stdout left on the Windows OEM code page, best-fit mapping collapsed those smart
		// quotes to an unescaped ASCII '"', producing invalid JSON that the next step's
		// ConvertFrom-Json rejected ("unexpected character 'p'" at [8].excerpt). Building the string
		// from code points (not the .ps1 file bytes) isolates the OUTPUT encoding path: the payload
		// must round-trip byte-lossless and parse as valid JSON.
		var executor = CreateExecutor();
		var step = CreateScriptStep(
			shell: "pwsh",
			script: """
				$smartOpen = [char]0x201C
				$smartClose = [char]0x201D
				$check = [char]0x2705
				$pin = [System.Char]::ConvertFromUtf32(0x1F4CC)
				$item = [ordered]@{
				    title = 'Movies That Were Left Unrated For Being Too Extreme'
				    excerpt = "the Hays Code was used for a simplistic ${smartOpen}pass/fail${smartClose} system $check $pin end"
				}
				$item | ConvertTo-Json -Compress
				""");
		var context = new OrchestrationExecutionContext { OrchestrationInfo = s_defaultInfo, Parameters = new Dictionary<string, string>() };

		// Act
		var result = await executor.ExecuteAsync(step, context);

		// Assert — the step succeeds and its stdout is valid, lossless UTF-8 JSON.
		result.Status.Should().Be(ExecutionStatus.Succeeded);
		var json = result.Content.Trim();

		var parse = () => System.Text.Json.JsonDocument.Parse(json);
		parse.Should().NotThrow(
			"smart quotes must survive as UTF-8 rather than best-fit to an unescaped ASCII quote");

		using var doc = System.Text.Json.JsonDocument.Parse(json);
		var excerpt = doc.RootElement.GetProperty("excerpt").GetString();
		excerpt.Should().Contain("\u201Cpass/fail\u201D", "curly quotes must be preserved verbatim");
		excerpt.Should().Contain("\u2705", "the check-mark emoji must not be flattened to '?'");
		excerpt.Should().Contain("\U0001F4CC", "the surrogate-pair pushpin emoji must round-trip intact");
	}

	[Fact]
	public async Task ExecuteAsync_PwshWriteError_ByDefault_ReturnsFailed()
	{
		// Arrange — by default for pwsh, the executor injects
		//   $ErrorActionPreference='Stop'; trap { ...diagnostic stderr...; exit 1 };
		// so Write-Error is promoted to a terminating error and pwsh exits 1.
		// Previously (no prologue) the same script would silently exit 0 because
		// Write-Error is non-terminating under default $ErrorActionPreference='Continue'.
		var executor = CreateExecutor();
		var step = CreateScriptStep(
			shell: "pwsh",
			script: "Write-Output 'before-error'; Write-Error 'write-error-text'");
		var context = new OrchestrationExecutionContext { OrchestrationInfo = s_defaultInfo, Parameters = new Dictionary<string, string>() };

		// Act
		var result = await executor.ExecuteAsync(step, context);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Failed);
		result.ErrorMessage.Should().Contain("write-error-text");
	}

	[Fact]
	public async Task ExecuteAsync_PwshWriteError_StrictModeFalse_StillSucceeds()
	{
		// Arrange — opting out of the prologue (strictMode: false) restores the
		// historical lenient behaviour where Write-Error is non-terminating and the
		// script exits 0.
		var executor = CreateExecutor();
		var step = CreateScriptStep(
			shell: "pwsh",
			script: "Write-Output 'before-error'; Write-Error 'should-be-non-terminating'",
			strictMode: false);
		var context = new OrchestrationExecutionContext { OrchestrationInfo = s_defaultInfo, Parameters = new Dictionary<string, string>() };

		// Act
		var result = await executor.ExecuteAsync(step, context);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Succeeded);
		result.Content.Should().Contain("before-error");
	}

	[Fact]
	public async Task ExecuteAsync_PwshTerminatingError_ByDefault_ReturnsFailed()
	{
		// Arrange — exact repro of the bug from production run 89e8cb96b915:
		// PowerShell 7.6.1 on .NET 10 throws
		//   "Argument types do not match"
		// when evaluating @(<System.Collections.Generic.List[object]>). Under the
		// pre-fix engine pwsh exited 0 with empty stdout (silent failure) because
		// $ErrorActionPreference defaults to Continue; the default prologue
		// (Stop + trap) promotes the error to a non-zero exit so the engine
		// reports Failed and downstream steps are skipped.
		var executor = CreateExecutor();
		var step = CreateScriptStep(
			shell: "pwsh",
			script: """
				$L = New-Object System.Collections.Generic.List[object]
				$L.Add('alpha')
				$L.Add('beta')
				$wrapped = @($L)
				Write-Output "wrapped count: $($wrapped.Count)"
				""");
		var context = new OrchestrationExecutionContext { OrchestrationInfo = s_defaultInfo, Parameters = new Dictionary<string, string>() };

		// Act
		var result = await executor.ExecuteAsync(step, context);

		// Assert — the failing line must surface as a Failed status with the
		// PowerShell error text included in the ErrorMessage.
		result.Status.Should().Be(ExecutionStatus.Failed);
		result.ErrorMessage.Should().Contain("Argument types do not match");
	}

	[Fact]
	public async Task ExecuteAsync_PwshTerminatingError_ByDefault_DiagnosticSurfacesInnerScriptLine()
	{
		// Arrange — regression guard for the prologue's diagnostic output. The previous
		// trap `trap { Write-Error -ErrorRecord $_; exit 1 }` re-emitted the error via the
		// standard pwsh error renderer, which pointed at the *trap statement* on line 1
		// and rendered the source line with U+2026 truncation markers that captured
		// stderr could not represent. The current prologue must instead write a
		// structured diagnostic that surfaces the inner script line and the failing
		// source line so production investigations are not misled into thinking the
		// engine-injected prologue itself is corrupt.
		var executor = CreateExecutor();
		var step = CreateScriptStep(
			shell: "pwsh",
			script: """
				$L = New-Object System.Collections.Generic.List[object]
				$L.Add('alpha')
				$wrapped = @($L)
				""");
		var context = new OrchestrationExecutionContext { OrchestrationInfo = s_defaultInfo, Parameters = new Dictionary<string, string>() };

		// Act
		var result = await executor.ExecuteAsync(step, context);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Failed);
		result.ErrorMessage.Should().Contain("ORCHESTRA-PWSH-ERROR:",
			"the structured diagnostic marker is what downstream log consumers grep for");
		result.ErrorMessage.Should().Contain("Argument types do not match",
			"the original PowerShell error message must survive the trap");
		result.ErrorMessage.Should().Contain("$wrapped = @($L)",
			"the failing source line must be echoed back so operators can see the offending statement without diffing the resolved script");
		result.ErrorMessage.Should().NotContain("Write-Error -ErrorRecord",
			"the new prologue writes directly to [Console]::Error and must not reintroduce the misleading pwsh error renderer prefix");
	}

	[Fact]
	public async Task ExecuteAsync_PwshTerminatingError_StrictMode_DiagnosticSurfacesInnerScriptLine()
	{
		// Arrange — strict-mode prologue must carry the same diagnostic improvements
		// as the default prologue. We trigger a strict-mode-only failure (reading a
		// non-existent property under Set-StrictMode -Version Latest) and verify the
		// diagnostic surfaces the property name and the failing source line.
		var executor = CreateExecutor();
		var step = CreateScriptStep(
			shell: "pwsh",
			script: """
				$obj = '{"a":1}' | ConvertFrom-Json
				$value = $obj.MissingProp
				""",
			strictMode: true);
		var context = new OrchestrationExecutionContext { OrchestrationInfo = s_defaultInfo, Parameters = new Dictionary<string, string>() };

		// Act
		var result = await executor.ExecuteAsync(step, context);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Failed);
		result.ErrorMessage.Should().Contain("ORCHESTRA-PWSH-ERROR:");
		result.ErrorMessage.Should().Contain("MissingProp",
			"the property name from the strict-mode error must survive the trap");
		result.ErrorMessage.Should().Contain("$value = $obj.MissingProp",
			"the failing source line must be echoed back even under the strict prologue");
	}

	[Fact]
	public async Task ExecuteAsync_PwshMissingJsonProperty_ByDefault_DoesNotFail()
	{
		// Arrange — regression guard for run f3c03a951a2d. The default prologue must
		// NOT make `$obj.MaybeMissingProperty` throw, because most production scripts
		// rely on PowerShell's standard behaviour of returning $null for that pattern
		// when normalising JSON dependency outputs that may or may not contain a field.
		var executor = CreateExecutor();
		var step = CreateScriptStep(
			shell: "pwsh",
			script: """
				$obj = '{"a":1}' | ConvertFrom-Json
				if ($obj.b) { Write-Output 'has-b' } else { Write-Output 'no-b' }
				""");
		var context = new OrchestrationExecutionContext { OrchestrationInfo = s_defaultInfo, Parameters = new Dictionary<string, string>() };

		// Act
		var result = await executor.ExecuteAsync(step, context);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Succeeded);
		result.Content.Should().Contain("no-b");
	}

	[Fact]
	public async Task ExecuteAsync_PwshMissingJsonProperty_StrictModeTrue_ReturnsFailed()
	{
		// Arrange — opting in via strictMode: true brings Set-StrictMode -Version Latest
		// into scope, so reading a property that does not exist on a parsed JSON object
		// throws "The property 'b' cannot be found on this object".
		var executor = CreateExecutor();
		var step = CreateScriptStep(
			shell: "pwsh",
			script: """
				$obj = '{"a":1}' | ConvertFrom-Json
				if ($obj.b) { Write-Output 'has-b' } else { Write-Output 'no-b' }
				""",
			strictMode: true);
		var context = new OrchestrationExecutionContext { OrchestrationInfo = s_defaultInfo, Parameters = new Dictionary<string, string>() };

		// Act
		var result = await executor.ExecuteAsync(step, context);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Failed);
		result.ErrorMessage.Should().Contain("'b'");
	}

	[Fact]
	public async Task ExecuteAsync_PwshHealthyScript_ByDefault_Succeeds()
	{
		// Arrange — confirm the auto-prologue does not regress healthy scripts.
		var executor = CreateExecutor();
		var step = CreateScriptStep(
			shell: "pwsh",
			script: "Write-Output 'healthy'; @(1, 2, 3) | ForEach-Object { $_ * 2 }");
		var context = new OrchestrationExecutionContext { OrchestrationInfo = s_defaultInfo, Parameters = new Dictionary<string, string>() };

		// Act
		var result = await executor.ExecuteAsync(step, context);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Succeeded);
		result.Content.Should().Contain("healthy");
	}

	[Fact]
	public async Task ExecuteAsync_PwshDefault_UserOverrideAtTopOfScript_RestoresContinue()
	{
		// Arrange — the default prologue is injected before the user's script. The user
		// can re-assert $ErrorActionPreference='Continue' to restore lenient behaviour
		// for the remainder of the script. Write-Error is then non-terminating, the
		// trap from the prologue is never triggered, and pwsh exits 0.
		var executor = CreateExecutor();
		var step = CreateScriptStep(
			shell: "pwsh",
			script: """
				$ErrorActionPreference = 'Continue'
				Write-Output 'user-restored'
				Write-Error 'user-allowed-stderr'
				""");
		var context = new OrchestrationExecutionContext { OrchestrationInfo = s_defaultInfo, Parameters = new Dictionary<string, string>() };

		// Act
		var result = await executor.ExecuteAsync(step, context);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Succeeded);
		result.Content.Should().Contain("user-restored");
	}

	[Fact]
	public async Task ExecuteAsync_BashShell_NoPrologueInjected()
	{
		// Arrange — non-PowerShell shells must not receive the prologue. We assert
		// this by running a bash script that echoes the file contents back to us;
		// the first line should be the user's own first line, not a $-prefixed
		// PowerShell statement.
		if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
			return; // bash isn't a guaranteed PATH entry on Windows CI hosts.

		var executor = CreateExecutor();
		var step = CreateScriptStep(
			shell: "bash",
			script: "head -n 1 \"$0\"");
		var context = new OrchestrationExecutionContext { OrchestrationInfo = s_defaultInfo, Parameters = new Dictionary<string, string>() };

		// Act
		var result = await executor.ExecuteAsync(step, context);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Succeeded);
		result.Content.Should().Contain("head -n 1");
		result.Content.Should().NotContain("$ErrorActionPreference");
		result.Content.Should().NotContain("Set-StrictMode");
	}

	[Fact]
	public void InjectPowerShellPrologue_PlainScript_PrologueAtTop()
	{
		// Arrange
		var input = "Write-Output 'hello'\nWrite-Output 'world'\n";

		// Act
		var output = ScriptStepExecutor.InjectPowerShellPrologue(input, ScriptStepExecutor.PowerShellDefaultPrologue);

		// Assert — prologue is the first non-empty line, user content follows untouched.
		var lines = output.Split('\n', StringSplitOptions.None)
			.Select(l => l.TrimEnd('\r'))
			.ToArray();
		lines[0].Should().Be(ScriptStepExecutor.PowerShellDefaultPrologue);
		lines[1].Should().Be("Write-Output 'hello'");
		lines[2].Should().Be("Write-Output 'world'");
	}

	[Fact]
	public void InjectPowerShellPrologue_StrictPrologue_AlsoPlacedAtTop()
	{
		// Arrange — the same placement logic applies when the strict prologue is requested.
		var input = "Write-Output 'hi'\n";

		// Act
		var output = ScriptStepExecutor.InjectPowerShellPrologue(input, ScriptStepExecutor.PowerShellStrictPrologue);

		// Assert
		var lines = output.Split('\n', StringSplitOptions.None)
			.Select(l => l.TrimEnd('\r'))
			.ToArray();
		lines[0].Should().Be(ScriptStepExecutor.PowerShellStrictPrologue);
		lines[1].Should().Be("Write-Output 'hi'");
	}

	[Fact]
	public void InjectPowerShellPrologue_ParamBlock_PrologueAfterParam()
	{
		// Arrange — param(...) must be the first statement in a PowerShell script.
		var input = "param($Name, $Greeting)\nWrite-Output \"$Greeting $Name\"\n";

		// Act
		var output = ScriptStepExecutor.InjectPowerShellPrologue(input, ScriptStepExecutor.PowerShellDefaultPrologue);

		// Assert — the param line is preserved at the top; prologue comes immediately after.
		output.Should().StartWith("param($Name, $Greeting)");
		output.Should().Contain(ScriptStepExecutor.PowerShellDefaultPrologue);

		var paramIndex = output.IndexOf("param(", StringComparison.Ordinal);
		var prologueIndex = output.IndexOf(ScriptStepExecutor.PowerShellDefaultPrologue, StringComparison.Ordinal);
		var bodyIndex = output.IndexOf("Write-Output", StringComparison.Ordinal);

		paramIndex.Should().BeLessThan(prologueIndex, "param() must precede the prologue");
		prologueIndex.Should().BeLessThan(bodyIndex, "prologue must precede the script body");
	}

	[Fact]
	public void InjectPowerShellPrologue_ParamBlock_NestedParensInDefaults_HandledCorrectly()
	{
		// Arrange — defaults can contain nested parens. The bracket matcher must
		// count depth so the prologue lands after the outermost ')'.
		var input = "param($A = @(1, 2, 3), $B = (Get-Date))\nWrite-Output $A\n";

		// Act
		var output = ScriptStepExecutor.InjectPowerShellPrologue(input, ScriptStepExecutor.PowerShellDefaultPrologue);

		// Assert
		output.Should().StartWith("param($A = @(1, 2, 3), $B = (Get-Date))");
		var paramEnd = output.IndexOf("(Get-Date))", StringComparison.Ordinal) + "(Get-Date))".Length;
		var prologueIndex = output.IndexOf(ScriptStepExecutor.PowerShellDefaultPrologue, StringComparison.Ordinal);
		prologueIndex.Should().BeGreaterThan(paramEnd);
	}

	[Fact]
	public void InjectPowerShellPrologue_CmdletBindingAttributeThenParam_PrologueAfterParam()
	{
		// Arrange — attribute decorations like [CmdletBinding()] precede param().
		var input = "[CmdletBinding()]\nparam($X)\nWrite-Output $X\n";

		// Act
		var output = ScriptStepExecutor.InjectPowerShellPrologue(input, ScriptStepExecutor.PowerShellDefaultPrologue);

		// Assert
		var attrIndex = output.IndexOf("[CmdletBinding()]", StringComparison.Ordinal);
		var paramIndex = output.IndexOf("param(", StringComparison.Ordinal);
		var prologueIndex = output.IndexOf(ScriptStepExecutor.PowerShellDefaultPrologue, StringComparison.Ordinal);

		attrIndex.Should().Be(0);
		attrIndex.Should().BeLessThan(paramIndex);
		paramIndex.Should().BeLessThan(prologueIndex);
	}

	[Fact]
	public void InjectPowerShellPrologue_RequiresAndUsing_PrologueAfterBoth()
	{
		// Arrange — #requires and using statements must precede other statements.
		var input = "#requires -Version 7.0\nusing namespace System.Text.RegularExpressions\nWrite-Output 'body'\n";

		// Act
		var output = ScriptStepExecutor.InjectPowerShellPrologue(input, ScriptStepExecutor.PowerShellDefaultPrologue);

		// Assert
		var requiresIndex = output.IndexOf("#requires", StringComparison.Ordinal);
		var usingIndex = output.IndexOf("using namespace", StringComparison.Ordinal);
		var prologueIndex = output.IndexOf(ScriptStepExecutor.PowerShellDefaultPrologue, StringComparison.Ordinal);
		var bodyIndex = output.IndexOf("Write-Output", StringComparison.Ordinal);

		requiresIndex.Should().Be(0);
		requiresIndex.Should().BeLessThan(usingIndex);
		usingIndex.Should().BeLessThan(prologueIndex);
		prologueIndex.Should().BeLessThan(bodyIndex);
	}

	[Fact]
	public void InjectPowerShellPrologue_LeadingComments_PrologueAfterComments()
	{
		// Arrange — comments before any executable statement must be preserved.
		var input = "# License header\n# Copyright 2026\nWrite-Output 'body'\n";

		// Act
		var output = ScriptStepExecutor.InjectPowerShellPrologue(input, ScriptStepExecutor.PowerShellDefaultPrologue);

		// Assert
		output.Should().StartWith("# License header");
		output.IndexOf("# Copyright 2026", StringComparison.Ordinal).Should().BeGreaterThan(0);
		var prologueIndex = output.IndexOf(ScriptStepExecutor.PowerShellDefaultPrologue, StringComparison.Ordinal);
		var bodyIndex = output.IndexOf("Write-Output", StringComparison.Ordinal);
		prologueIndex.Should().BeLessThan(bodyIndex);
	}

	[Fact]
	public void InjectPowerShellPrologue_LeadingStaticMethodCall_PrologueAtOffsetZero()
	{
		// Regression — a leading `[Type]::Method(...)` is a type-literal
		// expression, NOT an attribute. The scanner historically misidentified
		// it as an attribute and inserted the prologue between `]` and `::`,
		// breaking the static call with "The term '::WriteAllText' is not
		// recognized as a name of a cmdlet, function, script file, or
		// executable program."
		var input = "[System.IO.File]::WriteAllText('marker.txt', 'data')\n";

		// Act
		var output = ScriptStepExecutor.InjectPowerShellPrologue(input, ScriptStepExecutor.PowerShellDefaultPrologue);

		// Assert — prologue is at offset 0, the user script is preserved
		// verbatim afterwards (in particular, `[System.IO.File]` and
		// `::WriteAllText` are NOT split).
		output.Should().StartWith(ScriptStepExecutor.PowerShellDefaultPrologue);
		output.Should().Contain("[System.IO.File]::WriteAllText('marker.txt', 'data')");
		output.IndexOf("[System.IO.File]::WriteAllText", StringComparison.Ordinal)
			.Should().BeGreaterThan(ScriptStepExecutor.PowerShellDefaultPrologue.Length,
				because: "the user's static method call must appear AFTER the injected prologue, not have the prologue inserted in the middle of it.");
	}

	[Fact]
	public void InjectPowerShellPrologue_LeadingTypeStaticPropertyAccess_PrologueAtOffsetZero()
	{
		// Regression — same family as the static method case but with a
		// property access: `[int]::MaxValue`, `[datetime]::Now`. The trailing
		// `::` after `]` must classify the bracket as a type-literal
		// expression rather than an attribute.
		var input = "$max = [int]::MaxValue\nWrite-Output $max\n";

		// Act
		var output = ScriptStepExecutor.InjectPowerShellPrologue(input, ScriptStepExecutor.PowerShellDefaultPrologue);

		// Assert
		output.Should().StartWith(ScriptStepExecutor.PowerShellDefaultPrologue);
		output.Should().Contain("$max = [int]::MaxValue");
	}

	[Fact]
	public void InjectPowerShellPrologue_LeadingTypeCast_PrologueAtOffsetZero()
	{
		// Regression — `[int]$x` is a type cast onto a variable, also a
		// type-literal expression. The `$` after `]` must classify it as
		// an expression, NOT an attribute.
		var input = "[int]$x = 5\nWrite-Output $x\n";

		// Act
		var output = ScriptStepExecutor.InjectPowerShellPrologue(input, ScriptStepExecutor.PowerShellDefaultPrologue);

		// Assert
		output.Should().StartWith(ScriptStepExecutor.PowerShellDefaultPrologue);
		output.Should().Contain("[int]$x = 5");
	}

	[Fact]
	public void InjectPowerShellPrologue_LeadingTypeMemberAccess_PrologueAtOffsetZero()
	{
		// Regression — `[Type].Member` is instance-member access on the
		// reflected Type object, which is a type-literal expression. The
		// trailing `.` after `]` must classify it as an expression.
		var input = "$name = [string].FullName\nWrite-Output $name\n";

		// Act
		var output = ScriptStepExecutor.InjectPowerShellPrologue(input, ScriptStepExecutor.PowerShellDefaultPrologue);

		// Assert
		output.Should().StartWith(ScriptStepExecutor.PowerShellDefaultPrologue);
		output.Should().Contain("$name = [string].FullName");
	}

	[Fact]
	public void InjectPowerShellPrologue_LeadingTypeInvocation_PrologueAtOffsetZero()
	{
		// Regression — `[Type](args)` is a constructor-style cast invocation
		// (e.g., `[System.IO.FileInfo]('path')`). The trailing `(` after `]`
		// must classify it as an expression.
		var input = "$fi = [System.IO.FileInfo]('C:\\\\test.txt')\nWrite-Output $fi\n";

		// Act
		var output = ScriptStepExecutor.InjectPowerShellPrologue(input, ScriptStepExecutor.PowerShellDefaultPrologue);

		// Assert
		output.Should().StartWith(ScriptStepExecutor.PowerShellDefaultPrologue);
		output.Should().Contain("$fi = [System.IO.FileInfo]");
	}

	[Fact]
	public void InjectPowerShellPrologue_AttributeFollowedByBlockCommentThenParam_PrologueAfterParam()
	{
		// Regression — attribute decorations may legally be separated from the
		// param/function/class keyword they decorate by intervening comments.
		// The scanner must treat a block comment after `[Attr()]` as an
		// "attribute follower" so it continues scanning forward to the param
		// block.
		var input = "[CmdletBinding()]\n<# explanatory note #>\nparam($X)\nWrite-Output $X\n";

		// Act
		var output = ScriptStepExecutor.InjectPowerShellPrologue(input, ScriptStepExecutor.PowerShellDefaultPrologue);

		// Assert
		var attrIndex = output.IndexOf("[CmdletBinding()]", StringComparison.Ordinal);
		var commentIndex = output.IndexOf("<# explanatory note #>", StringComparison.Ordinal);
		var paramIndex = output.IndexOf("param(", StringComparison.Ordinal);
		var prologueIndex = output.IndexOf(ScriptStepExecutor.PowerShellDefaultPrologue, StringComparison.Ordinal);

		attrIndex.Should().Be(0);
		attrIndex.Should().BeLessThan(commentIndex);
		commentIndex.Should().BeLessThan(paramIndex);
		paramIndex.Should().BeLessThan(prologueIndex,
			because: "the prologue must land after `param()`; a block comment between attribute and param must not stop the scanner.");
	}

	[Fact]
	public void InjectPowerShellPrologue_AttributeFollowedByLineCommentThenParam_PrologueAfterParam()
	{
		// Regression — same as the block-comment case but with a `# line`
		// comment instead.
		var input = "[CmdletBinding()]\n# inline reason\nparam($X)\nWrite-Output $X\n";

		// Act
		var output = ScriptStepExecutor.InjectPowerShellPrologue(input, ScriptStepExecutor.PowerShellDefaultPrologue);

		// Assert
		var paramIndex = output.IndexOf("param(", StringComparison.Ordinal);
		var prologueIndex = output.IndexOf(ScriptStepExecutor.PowerShellDefaultPrologue, StringComparison.Ordinal);
		paramIndex.Should().BeLessThan(prologueIndex);
	}

	[Fact]
	public void InjectPowerShellPrologue_AttributeFollowedByFunction_PrologueAfterAttribute()
	{
		// Regression — attributes can decorate a `function` declaration (less
		// common at script top level but legal). The scanner consumes the
		// attribute past `]` and then breaks at `function` (because the
		// scanner does not look inside function bodies for `param`), so the
		// prologue lands BETWEEN the attribute and the function declaration.
		// The function declaration itself remains intact.
		//
		// This test guards against a different regression than the
		// `[Type]::Method` case: here we want to ensure the scanner DOES
		// continue past the attribute (IsAttributeFollower returns true for
		// `function`) rather than treating `[CmdletBinding()]` as the body
		// start (which would put the prologue at offset 0 and leave the
		// attribute decorating the prologue, which is invalid).
		var input = "[CmdletBinding()]\nfunction Invoke-Foo { Write-Output 'foo' }\nInvoke-Foo\n";

		var output = ScriptStepExecutor.InjectPowerShellPrologue(input, ScriptStepExecutor.PowerShellDefaultPrologue);

		var attrIndex = output.IndexOf("[CmdletBinding()]", StringComparison.Ordinal);
		var prologueIndex = output.IndexOf(ScriptStepExecutor.PowerShellDefaultPrologue, StringComparison.Ordinal);
		var fnIndex = output.IndexOf("function Invoke-Foo", StringComparison.Ordinal);

		attrIndex.Should().Be(0,
			because: "the attribute must be preserved at the top of the script.");
		attrIndex.Should().BeLessThan(prologueIndex,
			because: "the prologue must land AFTER the attribute, not before it.");
		prologueIndex.Should().BeLessThan(fnIndex,
			because: "the prologue lands between the attribute and the function declaration; the scanner does not look inside function bodies.");
	}

	[Fact]
	public async Task ExecuteAsync_PwshScriptStartingWithStaticMethodCall_RunsSuccessfully()
	{
		// End-to-end regression for the prologue-injection bug above. Before
		// the fix, this exact script would fail with:
		//   "The term '::WriteAllText' is not recognized as a name of a
		//    cmdlet, function, script file, or executable program."
		// because the scanner inserted the prologue between `[System.IO.File]`
		// and `::WriteAllText`. With the fix, the prologue lands at offset 0
		// and the static call runs intact.
		var marker = NewMarkerPath();
		var executor = CreateExecutor();
		var step = CreateScriptStep(
			shell: "pwsh",
			script: $$"""
				[System.IO.File]::WriteAllText('{{EscapeForPwsh(marker)}}', 'hello-from-static-call')
				Write-Output 'done'
				""");
		var context = new OrchestrationExecutionContext { OrchestrationInfo = s_defaultInfo, Parameters = new Dictionary<string, string>() };

		try
		{
			// Act
			var result = await executor.ExecuteAsync(step, context);

			// Assert — the script runs to completion (the bug previously made
			// it fail with a "not recognized" error before reaching `Write-Output`).
			result.Status.Should().Be(ExecutionStatus.Succeeded,
				because: $"the prologue must not be inserted between `]` and `::`. error was: {result.ErrorMessage}");
			result.Content.Should().Contain("done");

			File.Exists(marker).Should().BeTrue(
				"the static `[System.IO.File]::WriteAllText` call must have run and written the marker.");
			(await File.ReadAllTextAsync(marker)).Should().Be("hello-from-static-call");
		}
		finally
		{
			SafeDelete(marker);
		}
	}

	[Fact]
	public async Task ExecuteAsync_PwshScriptWithParamBlock_ByDefault_ParamStillBinds()
	{
		// Arrange — confirms that param() bindings still work end-to-end when
		// the default prologue is auto-injected for pwsh.
		var executor = CreateExecutor();
		var step = CreateScriptStep(
			shell: "pwsh",
			script: "param($Name, $Greeting) Write-Output \"$Greeting $Name\"",
			arguments: ["World", "Hello"]);
		var context = new OrchestrationExecutionContext { OrchestrationInfo = s_defaultInfo, Parameters = new Dictionary<string, string>() };

		// Act
		var result = await executor.ExecuteAsync(step, context);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Succeeded);
		result.Content.Should().Contain("Hello World");
	}

	#endregion

	#region Argument Spill (CreateProcess limit)

	[Fact]
	public void SpillOversizeArguments_BelowTotalThreshold_ReturnsArgumentsUnchanged()
	{
		// Arrange
		var args = new[] { "small", new string('a', 500), "another" };
		var store = new OrchestrationTempFileStore(Path.GetTempPath(), "spill-below", Guid.NewGuid().ToString("N"));

		// Act
		var (rewritten, env) = ScriptStepExecutor.SpillOversizeArguments(args, store, "step");

		// Assert
		rewritten.Should().Equal(args);
		env.Should().BeEmpty();
	}

	[Fact]
	public void SpillOversizeArguments_NullTempStore_ReturnsArgumentsUnchanged()
	{
		// Arrange
		var args = new[] { new string('a', 100_000) };

		// Act
		var (rewritten, env) = ScriptStepExecutor.SpillOversizeArguments(args, tempFileStore: null, "step");

		// Assert
		rewritten.Should().Equal(args);
		env.Should().BeEmpty();
	}

	[Fact]
	public void SpillOversizeArguments_ExceedsThreshold_SpillsLargeArgsToFiles()
	{
		// Arrange
		var huge = new string('x', 50_000);
		var small = "small-value";
		var args = new[] { small, huge, small, huge };
		var store = new OrchestrationTempFileStore(Path.GetTempPath(), "spill-over", Guid.NewGuid().ToString("N"));

		// Act
		var (rewritten, env) = ScriptStepExecutor.SpillOversizeArguments(args, store, "step");

		// Assert: small args pass through, large args become markers pointing to files containing the original text
		rewritten.Should().HaveCount(4);
		rewritten[0].Should().Be(small);
		rewritten[2].Should().Be(small);

		rewritten[1].Should().StartWith(ScriptStepExecutor.OrchestraFileMarker);
		rewritten[3].Should().StartWith(ScriptStepExecutor.OrchestraFileMarker);

		var path1 = rewritten[1][ScriptStepExecutor.OrchestraFileMarker.Length..];
		var path3 = rewritten[3][ScriptStepExecutor.OrchestraFileMarker.Length..];
		File.Exists(path1).Should().BeTrue();
		File.Exists(path3).Should().BeTrue();
		File.ReadAllText(path1).Should().Be(huge);
		File.ReadAllText(path3).Should().Be(huge);

		// Manifest env var points to a JSON file containing the original (resolved) args
		env.Should().ContainKey("ORCHESTRA_ARGS_FILE");
		var manifestPath = env["ORCHESTRA_ARGS_FILE"];
		File.Exists(manifestPath).Should().BeTrue();
		var manifestArgs = System.Text.Json.JsonSerializer.Deserialize<string[]>(File.ReadAllText(manifestPath));
		manifestArgs.Should().Equal(args);

		// Spilled files are registered for the step so they appear in the run trace
		var filesForStep = store.GetFilesForStep("step");
		filesForStep.Should().Contain(path1);
		filesForStep.Should().Contain(path3);
		filesForStep.Should().Contain(manifestPath);
	}

	[Fact]
	public async Task ExecuteAsync_LargeArgument_DoesNotExceedCommandLineLimit()
	{
		// Arrange: a payload that, if inlined, would exceed Windows' CreateProcess 32K limit
		var bigPayload = "{" + new string('a', 40_000) + "}";
		var dataPath = Path.Combine(Path.GetTempPath(), "orchestra-spill-e2e-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(dataPath);
		var store = new OrchestrationTempFileStore(dataPath, "orch", "run");

		var step = CreateScriptStep(
			shell: "pwsh",
			// Script reads $args[0] and asserts the original content is restored
			script: "if ($args[0].Length -ne " + bigPayload.Length + ") { Write-Error 'unexpected arg length'; exit 1 } Write-Output ('len=' + $args[0].Length)",
			arguments: [bigPayload]);
		var context = new OrchestrationExecutionContext
		{
			OrchestrationInfo = s_defaultInfo,
			Parameters = new Dictionary<string, string>(),
			TempFileStore = store,
		};
		var executor = CreateExecutor();

		// Act
		var result = await executor.ExecuteAsync(step, context);

		// Assert: previously this would fail with "The filename or extension is too long".
		// With spill in place, pwsh starts and the prologue restores the original arg.
		result.Status.Should().Be(ExecutionStatus.Succeeded, because: $"output was: {result.Content}");
		result.Content.Should().Contain("len=" + bigPayload.Length);
	}

	#endregion
}
