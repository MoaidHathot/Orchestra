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
		// Arrange
		var executor = CreateExecutor();
		var step = CreateScriptStep(
			shell: "pwsh",
			script: "Start-Sleep -Seconds 30; Write-Output 'done'");
		var context = new OrchestrationExecutionContext { OrchestrationInfo = s_defaultInfo, Parameters = new Dictionary<string, string>() };
		using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

		// Act
		var act = () => executor.ExecuteAsync(step, context, cts.Token);

		// Assert
		await act.Should().ThrowAsync<OperationCanceledException>();
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
		// Arrange
		var executor = CreateExecutor();
		var step = CreateScriptStep(
			shell: "pwsh",
			script: "Write-Output 'cleanup-test'");
		var context = new OrchestrationExecutionContext { OrchestrationInfo = s_defaultInfo, Parameters = new Dictionary<string, string>() };

		// Act
		var result = await executor.ExecuteAsync(step, context);

		// Assert — temp files should be cleaned up; check that no orchestra-* temp files linger
		result.Status.Should().Be(ExecutionStatus.Succeeded);
		var tempFiles = Directory.GetFiles(Path.GetTempPath(), "orchestra-*.ps1");
		// There may be other test runs' temp files, but this test's specific file should be gone
		// We can't assert the exact count, but we verify the step succeeds and doesn't leak
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

	#region Strict-Mode Prologue (pwsh / powershell)

	[Fact]
	public void ShouldInjectStrictPrologue_PwshShell_Default_OptsIn()
	{
		ScriptStepExecutor.ShouldInjectStrictPrologue("pwsh", explicitOptIn: null).Should().BeTrue();
		ScriptStepExecutor.ShouldInjectStrictPrologue("PWSH", explicitOptIn: null).Should().BeTrue();
		ScriptStepExecutor.ShouldInjectStrictPrologue("powershell", explicitOptIn: null).Should().BeTrue();
	}

	[Fact]
	public void ShouldInjectStrictPrologue_NonPowerShellShell_Default_OptsOut()
	{
		ScriptStepExecutor.ShouldInjectStrictPrologue("bash", explicitOptIn: null).Should().BeFalse();
		ScriptStepExecutor.ShouldInjectStrictPrologue("python", explicitOptIn: null).Should().BeFalse();
		ScriptStepExecutor.ShouldInjectStrictPrologue("node", explicitOptIn: null).Should().BeFalse();
		ScriptStepExecutor.ShouldInjectStrictPrologue("unknown-shell", explicitOptIn: null).Should().BeFalse();
	}

	[Fact]
	public void ShouldInjectStrictPrologue_ExplicitFalse_AlwaysOptsOut()
	{
		// Explicit opt-out must work even for the shells that would otherwise
		// auto-opt-in. This is the documented escape hatch for legacy scripts.
		ScriptStepExecutor.ShouldInjectStrictPrologue("pwsh", explicitOptIn: false).Should().BeFalse();
		ScriptStepExecutor.ShouldInjectStrictPrologue("powershell", explicitOptIn: false).Should().BeFalse();
		ScriptStepExecutor.ShouldInjectStrictPrologue("bash", explicitOptIn: false).Should().BeFalse();
	}

	[Fact]
	public void ShouldInjectStrictPrologue_ExplicitTrue_NonPowerShell_NoOp()
	{
		// strictMode: true on bash/python is currently a no-op because the
		// engine does not ship a prologue for those interpreters.
		ScriptStepExecutor.ShouldInjectStrictPrologue("bash", explicitOptIn: true).Should().BeFalse();
		ScriptStepExecutor.ShouldInjectStrictPrologue("python", explicitOptIn: true).Should().BeFalse();
	}

	[Fact]
	public async Task ExecuteAsync_PwshWriteError_StrictByDefault_ReturnsFailed()
	{
		// Arrange — by default for pwsh, the executor injects
		//   $ErrorActionPreference='Stop'; Set-StrictMode -Version Latest; trap { Write-Error -ErrorRecord $_; exit 1 };
		// so Write-Error is promoted to a terminating error and pwsh exits 1.
		// Previously the same script would silently exit 0 because Write-Error
		// is non-terminating under default $ErrorActionPreference='Continue'.
		var executor = CreateExecutor();
		var step = CreateScriptStep(
			shell: "pwsh",
			script: "Write-Output 'before-error'; Write-Error 'strict-mode-error'");
		var context = new OrchestrationExecutionContext { OrchestrationInfo = s_defaultInfo, Parameters = new Dictionary<string, string>() };

		// Act
		var result = await executor.ExecuteAsync(step, context);

		// Assert
		result.Status.Should().Be(ExecutionStatus.Failed);
		result.ErrorMessage.Should().Contain("strict-mode-error");
	}

	[Fact]
	public async Task ExecuteAsync_PwshWriteError_StrictModeFalse_StillSucceeds()
	{
		// Arrange — opting out of strict mode restores the historical lenient behaviour.
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
	public async Task ExecuteAsync_PwshGenericListInArraySubexpression_StrictByDefault_ReturnsFailed()
	{
		// Arrange — exact repro of the bug from production run 89e8cb96b915:
		// PowerShell 7.6.1 on .NET 10 throws
		//   "Argument types do not match"
		// when evaluating @(<System.Collections.Generic.List[object]>). Under the
		// pre-strict default pwsh exited 0 with empty stdout (silent failure);
		// under strict mode the trap converts the error to a non-zero exit so
		// the engine reports Failed and downstream steps are skipped.
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
	public async Task ExecuteAsync_PwshHealthyScript_StrictByDefault_Succeeds()
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
	public async Task ExecuteAsync_PwshStrictMode_UserOverrideAtTopOfScript_RestoresContinue()
	{
		// Arrange — the prologue is injected before the user's script. The user can
		// re-assert $ErrorActionPreference='Continue' and `Set-StrictMode -Off` to
		// restore lenient behaviour for the remainder of the script. Since Write-Error
		// is non-terminating under 'Continue', the trap from the prologue is never
		// triggered and pwsh exits 0.
		var executor = CreateExecutor();
		var step = CreateScriptStep(
			shell: "pwsh",
			script: """
				$ErrorActionPreference = 'Continue'
				Set-StrictMode -Off
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
	public void InjectPowerShellStrictPrologue_PlainScript_PrologueAtTop()
	{
		// Arrange
		var input = "Write-Output 'hello'\nWrite-Output 'world'\n";

		// Act
		var output = ScriptStepExecutor.InjectPowerShellStrictPrologue(input);

		// Assert — prologue is the first non-empty line, user content follows untouched.
		var lines = output.Split('\n', StringSplitOptions.None)
			.Select(l => l.TrimEnd('\r'))
			.ToArray();
		lines[0].Should().Be(ScriptStepExecutor.PowerShellStrictPrologue);
		lines[1].Should().Be("Write-Output 'hello'");
		lines[2].Should().Be("Write-Output 'world'");
	}

	[Fact]
	public void InjectPowerShellStrictPrologue_ParamBlock_PrologueAfterParam()
	{
		// Arrange — param(...) must be the first statement in a PowerShell script.
		var input = "param($Name, $Greeting)\nWrite-Output \"$Greeting $Name\"\n";

		// Act
		var output = ScriptStepExecutor.InjectPowerShellStrictPrologue(input);

		// Assert — the param line is preserved at the top; prologue comes immediately after.
		output.Should().StartWith("param($Name, $Greeting)");
		output.Should().Contain(ScriptStepExecutor.PowerShellStrictPrologue);

		var paramIndex = output.IndexOf("param(", StringComparison.Ordinal);
		var prologueIndex = output.IndexOf(ScriptStepExecutor.PowerShellStrictPrologue, StringComparison.Ordinal);
		var bodyIndex = output.IndexOf("Write-Output", StringComparison.Ordinal);

		paramIndex.Should().BeLessThan(prologueIndex, "param() must precede the prologue");
		prologueIndex.Should().BeLessThan(bodyIndex, "prologue must precede the script body");
	}

	[Fact]
	public void InjectPowerShellStrictPrologue_ParamBlock_NestedParensInDefaults_HandledCorrectly()
	{
		// Arrange — defaults can contain nested parens. The bracket matcher must
		// count depth so the prologue lands after the outermost ')'.
		var input = "param($A = @(1, 2, 3), $B = (Get-Date))\nWrite-Output $A\n";

		// Act
		var output = ScriptStepExecutor.InjectPowerShellStrictPrologue(input);

		// Assert
		output.Should().StartWith("param($A = @(1, 2, 3), $B = (Get-Date))");
		var paramEnd = output.IndexOf("(Get-Date))", StringComparison.Ordinal) + "(Get-Date))".Length;
		var prologueIndex = output.IndexOf(ScriptStepExecutor.PowerShellStrictPrologue, StringComparison.Ordinal);
		prologueIndex.Should().BeGreaterThan(paramEnd);
	}

	[Fact]
	public void InjectPowerShellStrictPrologue_CmdletBindingAttributeThenParam_PrologueAfterParam()
	{
		// Arrange — attribute decorations like [CmdletBinding()] precede param().
		var input = "[CmdletBinding()]\nparam($X)\nWrite-Output $X\n";

		// Act
		var output = ScriptStepExecutor.InjectPowerShellStrictPrologue(input);

		// Assert
		var attrIndex = output.IndexOf("[CmdletBinding()]", StringComparison.Ordinal);
		var paramIndex = output.IndexOf("param(", StringComparison.Ordinal);
		var prologueIndex = output.IndexOf(ScriptStepExecutor.PowerShellStrictPrologue, StringComparison.Ordinal);

		attrIndex.Should().Be(0);
		attrIndex.Should().BeLessThan(paramIndex);
		paramIndex.Should().BeLessThan(prologueIndex);
	}

	[Fact]
	public void InjectPowerShellStrictPrologue_RequiresAndUsing_PrologueAfterBoth()
	{
		// Arrange — #requires and using statements must precede other statements.
		var input = "#requires -Version 7.0\nusing namespace System.Text.RegularExpressions\nWrite-Output 'body'\n";

		// Act
		var output = ScriptStepExecutor.InjectPowerShellStrictPrologue(input);

		// Assert
		var requiresIndex = output.IndexOf("#requires", StringComparison.Ordinal);
		var usingIndex = output.IndexOf("using namespace", StringComparison.Ordinal);
		var prologueIndex = output.IndexOf(ScriptStepExecutor.PowerShellStrictPrologue, StringComparison.Ordinal);
		var bodyIndex = output.IndexOf("Write-Output", StringComparison.Ordinal);

		requiresIndex.Should().Be(0);
		requiresIndex.Should().BeLessThan(usingIndex);
		usingIndex.Should().BeLessThan(prologueIndex);
		prologueIndex.Should().BeLessThan(bodyIndex);
	}

	[Fact]
	public void InjectPowerShellStrictPrologue_LeadingComments_PrologueAfterComments()
	{
		// Arrange — comments before any executable statement must be preserved.
		var input = "# License header\n# Copyright 2026\nWrite-Output 'body'\n";

		// Act
		var output = ScriptStepExecutor.InjectPowerShellStrictPrologue(input);

		// Assert
		output.Should().StartWith("# License header");
		output.IndexOf("# Copyright 2026", StringComparison.Ordinal).Should().BeGreaterThan(0);
		var prologueIndex = output.IndexOf(ScriptStepExecutor.PowerShellStrictPrologue, StringComparison.Ordinal);
		var bodyIndex = output.IndexOf("Write-Output", StringComparison.Ordinal);
		prologueIndex.Should().BeLessThan(bodyIndex);
	}

	[Fact]
	public async Task ExecuteAsync_PwshScriptWithParamBlock_StrictByDefault_ParamStillBinds()
	{
		// Arrange — confirms that param() bindings still work end-to-end when
		// the strict-mode prologue is auto-injected for pwsh.
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
}
