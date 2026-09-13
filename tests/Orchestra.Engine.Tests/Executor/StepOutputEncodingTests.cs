using System.Runtime.InteropServices;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Orchestra.Engine.Tests.Executor;

/// <summary>
/// Regression coverage for lossless non-ASCII output on the two paths that used to lack UTF-8
/// pinning: <c>Command</c> steps (no <c>Standard*Encoding</c>, no console code page switch) and
/// <c>scriptFile:</c> PowerShell steps (the author's file could not carry the encoding prologue).
/// Both paths turned every character outside the Windows OEM code page into <c>?</c> before the
/// bytes ever reached the engine.
/// </summary>
/// <remarks>
/// Strings under test are built from code points inside the child process so the test isolates
/// the OUTPUT encoding path from how the test source or script file itself is encoded.
/// </remarks>
public class StepOutputEncodingTests : IDisposable
{
	private static readonly OrchestrationInfo s_defaultInfo = new("encoding-tests", "1.0.0", "run-enc", DateTimeOffset.UtcNow);

	/// <summary>"שלום" (Hebrew) — none of these code points exist in CP437/CP850/CP1252.</summary>
	private const string Hebrew = "\u05E9\u05DC\u05D5\u05DD";

	/// <summary>Builds the Hebrew string from code points inside PowerShell.</summary>
	private const string PwshHebrewExpression = "[string]::new([char[]](0x05E9,0x05DC,0x05D5,0x05DD))";

	private readonly IOrchestrationReporter _reporter = Substitute.For<IOrchestrationReporter>();
	private readonly string _tempDir;

	public StepOutputEncodingTests()
	{
		_tempDir = Path.Combine(Path.GetTempPath(), $"orchestra-encoding-tests-{Guid.NewGuid():N}");
		Directory.CreateDirectory(_tempDir);
	}

	public void Dispose()
	{
		try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
	}

	private static OrchestrationExecutionContext Context(OrchestrationTempFileStore? store = null) => new()
	{
		OrchestrationInfo = s_defaultInfo,
		Parameters = new Dictionary<string, string>(),
		TempFileStore = store,
	};

	private ScriptStepExecutor CreateScriptExecutor() =>
		new(_reporter, NullLoggerFactory.Instance.CreateLogger<ScriptStepExecutor>());

	private CommandStepExecutor CreateCommandExecutor() =>
		new(_reporter, NullLoggerFactory.Instance.CreateLogger<CommandStepExecutor>());

	private string WriteScriptFile(string name, string content)
	{
		var path = Path.Combine(_tempDir, name);
		// BOM-less UTF-8, like most editors produce; the content only uses ASCII anyway.
		File.WriteAllText(path, content, new System.Text.UTF8Encoding(false));
		return path;
	}

	private static ScriptOrchestrationStep ScriptFileStep(string scriptFile, string[]? arguments = null, string shell = "pwsh") => new()
	{
		Name = "script-file-step",
		Type = OrchestrationStepType.Script,
		Shell = shell,
		ScriptFile = scriptFile,
		Arguments = arguments ?? [],
	};

	private static CommandOrchestrationStep CommandStep(string command, params string[] arguments) => new()
	{
		Name = "command-step",
		Type = OrchestrationStepType.Command,
		Command = command,
		Arguments = arguments,
	};

	private static string CodePoints(string s) => string.Join(' ', s.Select(c => ((int)c).ToString("X4")));

	// ── Command step ─────────────────────────────────────────────────────────────

	[Fact]
	public async Task CommandStep_PwshChildEmittingNonLatinText_RoundTripsLossless()
	{
		// A pwsh launched through a Command step reads its output encoding from the console code
		// page. Before the fix the Command step neither switched that code page nor decoded the
		// pipe as UTF-8, so Hebrew arrived as "????".
		var result = await CreateCommandExecutor().ExecuteAsync(
			CommandStep("pwsh", "-NoProfile", "-Command", $"Write-Output ('X=' + {PwshHebrewExpression})"),
			Context());

		result.Status.Should().Be(ExecutionStatus.Succeeded, because: result.ErrorMessage);
		var line = result.Content.Split('\n').Select(l => l.Trim()).Single(l => l.StartsWith("X="));
		CodePoints(line[2..]).Should().Be(CodePoints(Hebrew),
			"Command-step output must decode to the original code points, not '?' (003F) or mojibake");
	}

	[Fact]
	public async Task CommandStep_NonZeroExitCode_StillFails_WithCodePagePrefixInPlace()
	{
		// The Windows chcp prefix must not mask the user command's exit code.
		var result = await CreateCommandExecutor().ExecuteAsync(
			RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
				? CommandStep("cmd", "/c", "exit 7")
				: CommandStep("sh", "-c", "exit 7"),
			Context());

		result.Status.Should().Be(ExecutionStatus.Failed);
		result.ErrorMessage.Should().Contain("exited with code 7");
	}

	[Fact]
	public void CommandStep_WindowsPrefix_SwitchesConsoleToUtf8_AndKeepsUserCommandRunning()
	{
		// Guard the exact shape of the prefix: chcp output discarded, chained with a plain '&' so
		// the user's command (and its exit code) run even if chcp were to fail.
		CommandStepExecutor.WindowsUtf8CodePagePrefix.Should().Be("chcp 65001>nul & ");
	}

	// ── scriptFile: PowerShell ───────────────────────────────────────────────────

	[Fact]
	public async Task ScriptFile_Pwsh_NonLatinOutput_RoundTripsLossless()
	{
		// Regression: the author's file cannot carry the inline UTF-8 prologue, so before the fix
		// a scriptFile pwsh step emitted CP437 and Hebrew degraded to "????".
		var file = WriteScriptFile("hebrew.ps1", $"Write-Output ('H=' + {PwshHebrewExpression})");

		var result = await CreateScriptExecutor().ExecuteAsync(ScriptFileStep(file), Context());

		result.Status.Should().Be(ExecutionStatus.Succeeded, because: result.ErrorMessage);
		var line = result.Content.Split('\n').Select(l => l.Trim()).Single(l => l.StartsWith("H="));
		CodePoints(line[2..]).Should().Be(CodePoints(Hebrew));
	}

	[Fact]
	public async Task ScriptFile_Pwsh_ExitCode_IsPropagated()
	{
		// exit N inside the dot-sourced file must become the process exit code, as with -File.
		var file = WriteScriptFile("exit7.ps1", "Write-Output 'before'\nexit 7\nWrite-Output 'never'");

		var result = await CreateScriptExecutor().ExecuteAsync(ScriptFileStep(file), Context());

		result.Status.Should().Be(ExecutionStatus.Failed);
		result.ErrorMessage.Should().Contain("exited with code 7");
	}

	[Fact]
	public async Task ScriptFile_Pwsh_StaleNativeExitCode_DoesNotFailTheStep()
	{
		// A script that ran a failing native command, handled it, and ended normally exits 0 under
		// pwsh -File. The wrapper must not turn the stale $LASTEXITCODE into a failure.
		var nativeFailure = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
			? "& cmd /c \"exit 3\""
			: "& sh -c 'exit 3'";
		var file = WriteScriptFile("stale.ps1", $"{nativeFailure}\nWrite-Output \"handled=$LASTEXITCODE\"");

		var result = await CreateScriptExecutor().ExecuteAsync(ScriptFileStep(file), Context());

		result.Status.Should().Be(ExecutionStatus.Succeeded, because: result.ErrorMessage);
		result.Content.Should().Contain("handled=3");
	}

	[Fact]
	public async Task ScriptFile_Pwsh_TrailingFailingNativeCommand_StillSucceedsLikeDashFile()
	{
		var nativeFailure = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
			? "& cmd /c \"exit 5\""
			: "& sh -c 'exit 5'";
		var file = WriteScriptFile("trailing.ps1", $"Write-Output 'ran'\n{nativeFailure}");

		var result = await CreateScriptExecutor().ExecuteAsync(ScriptFileStep(file), Context());

		result.Status.Should().Be(ExecutionStatus.Succeeded, because: result.ErrorMessage);
	}

	[Fact]
	public async Task ScriptFile_Pwsh_TerminatingError_FailsWithExitCode1_AndPointsAtAuthorFile()
	{
		var file = WriteScriptFile("boom.ps1", "Write-Output 'start'\nthrow 'boom-marker'");

		var result = await CreateScriptExecutor().ExecuteAsync(ScriptFileStep(file), Context());

		result.Status.Should().Be(ExecutionStatus.Failed);
		result.ErrorMessage.Should().Contain("exited with code 1").And.Contain("boom-marker");
		result.ErrorMessage.Should().Contain("boom.ps1", "error locations must reference the author's file, not the wrapper");
	}

	[Fact]
	public async Task ScriptFile_Pwsh_ScriptRootAndCommandPath_PointAtAuthorFile()
	{
		var file = WriteScriptFile("where.ps1", "Write-Output \"root=$PSScriptRoot\"\nWrite-Output \"path=$PSCommandPath\"\nWrite-Output \"inv=$($MyInvocation.MyCommand.Path)\"");

		var result = await CreateScriptExecutor().ExecuteAsync(ScriptFileStep(file), Context());

		result.Status.Should().Be(ExecutionStatus.Succeeded, because: result.ErrorMessage);
		result.Content.Should().Contain($"root={_tempDir}");
		result.Content.Should().Contain($"path={file}");
		result.Content.Should().Contain($"inv={file}");
	}

	[Fact]
	public async Task ScriptFile_Pwsh_PositionalAndNamedArguments_Bind()
	{
		var file = WriteScriptFile("params.ps1", "param([string]$Name, [string]$Mode = 'default')\nWrite-Output \"name=$Name mode=$Mode rest=$($args -join '|')\"");

		var positional = await CreateScriptExecutor().ExecuteAsync(ScriptFileStep(file, ["alice", "fast", "extra"]), Context());
		positional.Status.Should().Be(ExecutionStatus.Succeeded, because: positional.ErrorMessage);
		positional.Content.Should().Contain("name=alice mode=fast rest=extra");

		var named = await CreateScriptExecutor().ExecuteAsync(ScriptFileStep(file, ["-Mode", "slow", "-Name", "bob"]), Context());
		named.Status.Should().Be(ExecutionStatus.Succeeded, because: named.ErrorMessage);
		named.Content.Should().Contain("name=bob mode=slow rest=");
	}

	[Fact]
	public async Task ScriptFile_Pwsh_ControlHelpers_AreAvailable()
	{
		// Documented as "injected for every pwsh/powershell Script step"; scriptFile steps used to
		// miss them because nothing was injected around the author's file.
		var file = WriteScriptFile("gate.ps1", "Orchestra-SetStatus -Status no_action -Reason 'nothing to do'");

		var result = await CreateScriptExecutor().ExecuteAsync(ScriptFileStep(file), Context());

		result.Status.Should().Be(ExecutionStatus.NoAction, because: result.ErrorMessage);
		result.Content.Should().Contain("nothing to do");
	}

	[Fact]
	public async Task ScriptFile_Pwsh_OversizeArgument_IsResolvedFromSpillFile()
	{
		// The arg-spill marker is resolved by the injected resolver; scriptFile steps used to
		// receive the raw "@orchestra-file:" token instead of the content.
		var bigPayload = "{" + new string('a', 40_000) + "}";
		var store = new OrchestrationTempFileStore(_tempDir, "orch", "run");
		var file = WriteScriptFile("big.ps1", "Write-Output ('len=' + $args[0].Length)\nWrite-Output ('head=' + $args[0].Substring(0, 3))");

		var result = await CreateScriptExecutor().ExecuteAsync(ScriptFileStep(file, [bigPayload]), Context(store));

		result.Status.Should().Be(ExecutionStatus.Succeeded, because: result.ErrorMessage);
		result.Content.Should().Contain("len=" + bigPayload.Length).And.Contain("head={aa");
	}

	[Fact]
	public async Task ScriptFile_Pwsh_StdinPayload_IsReadableAndLossless_ThroughWrapper()
	{
		// Hooks run their scriptFile: through the same executor with the JSON payload on stdin.
		// The wrapper must leave stdin untouched for the dot-sourced file and the encoding
		// prologue must make [Console]::In decode it as UTF-8.
		var file = WriteScriptFile("stdin.ps1", "$payload = [Console]::In.ReadToEnd()\nWrite-Output ('S=' + $payload)");
		var step = new ScriptOrchestrationStep
		{
			Name = "script-file-step",
			Type = OrchestrationStepType.Script,
			Shell = "pwsh",
			ScriptFile = file,
			Stdin = $"{{\"who\":\"{Hebrew}\"}}",
		};

		var result = await CreateScriptExecutor().ExecuteAsync(step, Context());

		result.Status.Should().Be(ExecutionStatus.Succeeded, because: result.ErrorMessage);
		var line = result.Content.Split('\n').Select(l => l.Trim()).Single(l => l.StartsWith("S="));
		line[2..].Should().Be($"{{\"who\":\"{Hebrew}\"}}");
	}

	[Fact]
	public async Task ScriptFile_Pwsh_PathWithQuoteAndSpaces_IsDotSourcedCorrectly()
	{
		var file = WriteScriptFile("it's a script.ps1", "Write-Output 'quoted-path-ok'");

		var result = await CreateScriptExecutor().ExecuteAsync(ScriptFileStep(file), Context());

		result.Status.Should().Be(ExecutionStatus.Succeeded, because: result.ErrorMessage);
		result.Content.Should().Contain("quoted-path-ok");
	}

	[Fact]
	public async Task ScriptFile_Pwsh_TraceReportsAuthorFile_NotWrapper()
	{
		var file = WriteScriptFile("trace.ps1", "Write-Output 'traced'");
		StepExecutionTrace? trace = null;
		_reporter.When(r => r.ReportStepTrace(Arg.Any<string>(), Arg.Any<StepExecutionTrace>()))
			.Do(call => trace = call.Arg<StepExecutionTrace>());

		var result = await CreateScriptExecutor().ExecuteAsync(ScriptFileStep(file), Context());

		result.Status.Should().Be(ExecutionStatus.Succeeded, because: result.ErrorMessage);
		trace.Should().NotBeNull();
		trace!.ScriptSource.Should().Be(file);
	}

	[Fact]
	public async Task ScriptFile_Pwsh_WrapperTempFile_IsCleanedUp()
	{
		var file = WriteScriptFile("cleanup.ps1", "Write-Output 'ok'");
		StepExecutionTrace? trace = null;
		_reporter.When(r => r.ReportStepTrace(Arg.Any<string>(), Arg.Any<StepExecutionTrace>()))
			.Do(call => trace = call.Arg<StepExecutionTrace>());

		var result = await CreateScriptExecutor().ExecuteAsync(ScriptFileStep(file), Context());

		result.Status.Should().Be(ExecutionStatus.Succeeded, because: result.ErrorMessage);
		// The process arguments carry the generated wrapper path (-NoProfile -File <wrapper> ...).
		var wrapperPath = trace!.CommandArguments.Single(a => a.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase) && !string.Equals(a, file, StringComparison.OrdinalIgnoreCase));
		Path.GetFileName(wrapperPath).Should().StartWith("orchestra-");
		File.Exists(wrapperPath).Should().BeFalse("the generated wrapper is deleted like any inline temp script");
		File.Exists(file).Should().BeTrue("the author's file is never touched");
	}

	[Fact]
	public void BuildPowerShellScriptFileWrapper_Shape()
	{
		var wrapper = ScriptStepExecutor.BuildPowerShellScriptFileWrapper("pwsh", @"C:\scripts\it's here\run.ps1");

		wrapper.Should().NotBeNull();
		var lines = wrapper!.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
		lines.Should().HaveCount(3, "prologue line, dot-source line, exit shim");
		lines[0].Should().StartWith(ScriptStepExecutor.PowerShellOutputEncodingPrologue,
			"the encoding must be pinned before anything is written");
		lines[0].Should().Contain(ScriptStepExecutor.PowerShellInputEncodingPrologue)
			.And.Contain("function Orchestra-Complete")
			.And.Contain(ScriptStepExecutor.OrchestraFileMarker);
		lines[0].Should().NotContain("$ErrorActionPreference", "scriptFile steps keep their own error-handling semantics")
			.And.NotContain("Set-StrictMode");
		lines[1].Should().Be(@". 'C:\scripts\it''s here\run.ps1' @args");
		lines[2].Should().Be("if (-not $?) { exit $LASTEXITCODE }");
	}

	[Theory]
	[InlineData("bash")]
	[InlineData("python")]
	[InlineData("node")]
	[InlineData("sh")]
	public void BuildPowerShellScriptFileWrapper_NonPowerShellShells_ReturnNull(string shell)
	{
		ScriptStepExecutor.BuildPowerShellScriptFileWrapper(shell, "/tmp/x").Should().BeNull();
	}
}
