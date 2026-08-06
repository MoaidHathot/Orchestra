using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Orchestra.Engine.Tests.Executor;

/// <summary>
/// Round-trip tests for non-ASCII content piped into a Script step via <c>stdin</c>.
/// </summary>
/// <remarks>
/// <para>These run the real executor, not a hand-built <see cref="System.Diagnostics.ProcessStartInfo"/>,
/// because the bug they pin lives in the interaction between the host-side
/// <c>StandardInputEncoding</c> and the child-side <c>PowerShellInputEncodingPrologue</c> — either
/// one alone is worse than neither, so only the full pipeline proves the fix.</para>
/// <para>Before the fix, <c>[Console]::In.ReadToEnd()</c> saw <c>U+2014 U+00E9 U+201C U+201D U+4E2D</c>
/// arrive as <c>- é " " ?</c>: the em-dash flattened to a hyphen, both curly quotes collapsed to an
/// unescaped ASCII <c>"</c>, and the CJK character was replaced outright.</para>
/// </remarks>
[Collection(ScriptProcessExecutionCollection.Name)]
public class ScriptStepStdinEncodingTests
{
	private static readonly OrchestrationInfo s_defaultInfo =
		new("test-orchestration", "1.0.0", "run123", DateTimeOffset.UtcNow);

	private readonly IOrchestrationReporter _reporter = Substitute.For<IOrchestrationReporter>();
	private readonly ILogger<ScriptStepExecutor> _logger =
		NullLoggerFactory.Instance.CreateLogger<ScriptStepExecutor>();

	// The exact characters the OEM code page destroys.
	private const string EmDash = "\u2014";
	private const string EAcute = "\u00E9";
	private const string LeftQuote = "\u201C";
	private const string RightQuote = "\u201D";
	private const string Cjk = "\u4E2D";

	private ScriptOrchestrationStep Step(string script, string? stdin, bool? strictMode = null) => new()
	{
		Name = "stdin-step",
		Type = OrchestrationStepType.Script,
		DependsOn = [],
		Parameters = [],
		Shell = "pwsh",
		Script = script,
		Arguments = [],
		Environment = [],
		Stdin = stdin,
		StrictMode = strictMode,
	};

	private static OrchestrationExecutionContext Context() => new()
	{
		OrchestrationInfo = s_defaultInfo,
		Parameters = new Dictionary<string, string>(),
	};

	/// <summary>
	/// Echoes stdin back as a comma-separated list of code points, so an assertion failure names
	/// the exact characters that were mangled instead of comparing visually identical strings.
	/// </summary>
	private const string EchoCodePointsScript =
		"$s = [Console]::In.ReadToEnd(); Write-Output (($s.ToCharArray() | ForEach-Object { [int]$_ }) -join ',')";

    private static string CodePoints(string value) =>
		string.Join(",", value.Select(c => ((int)c).ToString()));

	// ── The core round-trip ──

	/// <summary>
	/// Every character class the Windows OEM code page destroys, in one process launch. Each is
	/// called out individually so a failure names the specific corruption rather than just a
	/// mismatched string.
	/// </summary>
	[Fact]
	public async Task Stdin_PreservesCharactersTheOemCodePageCannotRepresent()
	{
		var payload = EmDash + EAcute + LeftQuote + RightQuote + Cjk;
		var executor = new ScriptStepExecutor(_reporter, _logger);

		var result = await executor.ExecuteAsync(Step(EchoCodePointsScript, payload), Context());

		result.Status.Should().Be(ExecutionStatus.Succeeded);

		var codes = result.Content.Trim().Split(',');
		codes.Should().HaveCount(5, "no character may be dropped or split");
		codes[0].Should().Be("8212", "U+2014 previously flattened to '-' (45)");
		codes[1].Should().Be("233", "U+00E9 survived CP1252 but not CP437/850");
		codes[2].Should().Be("8220", "U+201C previously collapsed to an unescaped ASCII '\"' (34)");
		codes[3].Should().Be("8221", "U+201D previously collapsed to an unescaped ASCII '\"' (34)");
		codes[4].Should().Be("20013", "U+4E2D previously became '?' (63)");
	}

	/// <summary>
	/// The damaging case: curly quotes inside JSON string values. A serializer only escapes ASCII
	/// quotes, so a smart quote silently transliterated to <c>"</c> terminates the string early and
	/// the document no longer parses.
	/// </summary>
	[Fact]
	public async Task Stdin_JsonWithCurlyQuotesInsideStringValues_StaysParseable()
	{
		var json = $$"""{"note":"He said {{LeftQuote}}hello{{RightQuote}} to me","dash":"a{{EmDash}}b"}""";
		var executor = new ScriptStepExecutor(_reporter, _logger);

		var script =
			"$s = [Console]::In.ReadToEnd(); " +
			"$o = $s | ConvertFrom-Json; " +
			"Write-Output $o.note; Write-Output $o.dash";

		var result = await executor.ExecuteAsync(Step(script, json), Context());

		result.Status.Should().Be(ExecutionStatus.Succeeded,
			"the payload must still be valid JSON after crossing the process boundary");
		result.Content.Should().Contain($"He said {LeftQuote}hello{RightQuote} to me");
		result.Content.Should().Contain($"a{EmDash}b");
	}

	// ── The $input idiom ──

	/// <summary>
	/// Pins the documented limitation: <c>$input</c> and <c>[Console]::In</c> compete for a single
	/// stream, and merely referencing <c>$input</c> makes PowerShell drain it before the script body
	/// runs. This is a property of PowerShell's <c>-File</c> host, not something the engine can
	/// configure, so it is asserted rather than worked around.
	/// </summary>
	[Fact]
	public async Task Stdin_ReferencingInput_DrainsTheStreamBeforeConsoleInCanRead()
	{
		var executor = new ScriptStepExecutor(_reporter, _logger);

		var script =
			"$fromInput = ($input | Out-String); " +
			"$fromConsole = [Console]::In.ReadToEnd(); " +
			"Write-Output \"console-len=$($fromConsole.Length)\"";

		var result = await executor.ExecuteAsync(Step(script, EmDash + LeftQuote), Context());

		result.Status.Should().Be(ExecutionStatus.Succeeded);
		result.Content.Should().Contain("console-len=0",
			"referencing $input consumes stdin first — the two idioms are mutually exclusive, "
			+ "which is why [Console]::In.ReadToEnd() is the documented way to read stdin");
	}

	/// <summary>
	/// ASCII is identical in UTF-8 and every OEM/ANSI code page, so scripts using <c>$input</c>
	/// with ASCII payloads — the overwhelming majority, including every existing hook — are
	/// unaffected by pinning the encodings.
	/// </summary>
	[Fact]
	public async Task Stdin_InputAutomaticVariable_StillWorksForAscii()
	{
		var executor = new ScriptStepExecutor(_reporter, _logger);

		var script = "$s = ($input | Out-String).Trim(); Write-Output \"got=$s\"";

		var result = await executor.ExecuteAsync(Step(script, "plain ascii payload"), Context());

		result.Status.Should().Be(ExecutionStatus.Succeeded);
		result.Content.Should().Contain("got=plain ascii payload");
	}

	/// <summary>
	/// A hook payload is JSON built by the engine; asserts the shape hooks actually rely on
	/// survives, read through the supported idiom.
	/// </summary>
	[Fact]
	public async Task Stdin_HookStyleJsonPayload_ParsesWithConsoleIn()
	{
		var payload = "{\"orchestration\":{\"name\":\"caf" + EAcute + "-run\"},\"step\":{\"name\":\"a" + EmDash + "b\"}}";
		var executor = new ScriptStepExecutor(_reporter, _logger);

		var script =
			"$p = [Console]::In.ReadToEnd() | ConvertFrom-Json; " +
			"Write-Output $p.orchestration.name; Write-Output $p.step.name";

		var result = await executor.ExecuteAsync(Step(script, payload), Context());

		result.Status.Should().Be(ExecutionStatus.Succeeded);
		result.Content.Should().Contain($"caf{EAcute}-run");
		result.Content.Should().Contain($"a{EmDash}b");
	}

	// ── Applies regardless of strictMode ──

	[Theory]
	[InlineData(null)]
	[InlineData(true)]
	[InlineData(false)]
	public async Task Stdin_EncodingHoldsForEveryStrictModeSetting(bool? strictMode)
	{
		// strictMode:false skips the error-handling prologue entirely; the encoding prologue must
		// still be injected, otherwise opting out of guardrails would silently corrupt data.
		var payload = EmDash + LeftQuote + RightQuote;
		var executor = new ScriptStepExecutor(_reporter, _logger);

		var result = await executor.ExecuteAsync(Step(EchoCodePointsScript, payload, strictMode), Context());

		result.Content.Trim().Should().Be(CodePoints(payload));
	}

	// ── No stdin ──

	[Fact]
	public async Task NoStdin_StepStillSucceeds()
	{
		// StandardInputEncoding is only set when stdin is redirected; assert the un-redirected
		// path is untouched.
		var executor = new ScriptStepExecutor(_reporter, _logger);

		var result = await executor.ExecuteAsync(Step("Write-Output 'no stdin here'", stdin: null), Context());

		result.Status.Should().Be(ExecutionStatus.Succeeded);
		result.Content.Should().Contain("no stdin here");
	}

	[Fact]
	public async Task EmptyStdin_StepStillSucceeds()
	{
		var executor = new ScriptStepExecutor(_reporter, _logger);

		var result = await executor.ExecuteAsync(
			Step("$s = [Console]::In.ReadToEnd(); Write-Output \"len=$($s.Length)\"", stdin: ""),
			Context());

		result.Status.Should().Be(ExecutionStatus.Succeeded);
		result.Content.Should().Contain("len=0");
	}

	// ── Non-PowerShell shells ──
	//
	// These get no injected prologue, so they rely purely on the host-side StandardInputEncoding
	// (plus PYTHONIOENCODING for python, which decodes stdin via the ambient locale). Skipped when
	// the interpreter is absent so the suite stays runnable on machines without them.

	private static bool OnPath(string exe) =>
		(System.Environment.GetEnvironmentVariable("PATH") ?? "")
			.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
			.Any(dir =>
			{
				try
				{
					return File.Exists(Path.Combine(dir, exe))
						|| File.Exists(Path.Combine(dir, exe + ".exe"));
				}
				catch { return false; }
			});

	private ScriptOrchestrationStep ShellStep(string shell, string script, string stdin) => new()
	{
		Name = "stdin-step",
		Type = OrchestrationStepType.Script,
		DependsOn = [],
		Parameters = [],
		Shell = shell,
		Script = script,
		Arguments = [],
		Environment = [],
		Stdin = stdin,
	};

	[Fact]
	public async Task Stdin_Python_RoundTripsNonAscii()
	{
		if (!OnPath("python"))
			return;

		var payload = EmDash + EAcute + LeftQuote + RightQuote + Cjk;
		var executor = new ScriptStepExecutor(_reporter, _logger);

		var script = "import sys\nd = sys.stdin.read()\nprint(\",\".join(str(ord(c)) for c in d))";
		var result = await executor.ExecuteAsync(ShellStep("python", script, payload), Context());

		result.Status.Should().Be(ExecutionStatus.Succeeded);
		result.Content.Trim().Should().Be(CodePoints(payload),
			"python needs PYTHONIOENCODING=utf-8 alongside the host-side encoding");
	}

	[Fact]
	public async Task Stdin_Node_RoundTripsNonAscii()
	{
		if (!OnPath("node"))
			return;

		var payload = EmDash + EAcute + LeftQuote + RightQuote + Cjk;
		var executor = new ScriptStepExecutor(_reporter, _logger);

		var script =
			"let b=[];process.stdin.on(\"data\",c=>b.push(c)).on(\"end\",()=>{"
			+ "const s=Buffer.concat(b).toString(\"utf8\");"
			+ "console.log([...s].map(c=>c.codePointAt(0)).join(\",\"))});";
		var result = await executor.ExecuteAsync(ShellStep("node", script, payload), Context());

		result.Status.Should().Be(ExecutionStatus.Succeeded);
		result.Content.Trim().Should().Be(CodePoints(payload));
	}

	// ── Both halves are injected ──

	[Fact]
	public void Preamble_InjectsBothEncodingPrologues_ForEveryStrictModeSetting()
	{
		foreach (bool? strictMode in new bool?[] { null, true, false })
		{
			var preamble = ScriptStepExecutor.BuildPowerShellPreamble("pwsh", strictMode);

			preamble.Should().NotBeNull();
			preamble.Should().Contain(ScriptStepExecutor.PowerShellOutputEncodingPrologue);
			preamble.Should().Contain(ScriptStepExecutor.PowerShellInputEncodingPrologue,
				$"the input prologue must apply for strictMode:{strictMode?.ToString() ?? "null"}");
		}
	}

	[Fact]
	public void Preamble_IsNotInjectedForNonPowerShellShells()
	{
		ScriptStepExecutor.BuildPowerShellPreamble("bash", null).Should().BeNull();
		ScriptStepExecutor.BuildPowerShellPreamble("python", null).Should().BeNull();
	}
}
