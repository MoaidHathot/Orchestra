using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orchestra.Engine.Tests.TestHelpers;

namespace Orchestra.Engine.Tests.Executor;

/// <summary>
/// <c>maxOutputBytes</c> on Command, Script and Http steps: output beyond the cap is dropped,
/// a marker is appended, and an <c>output-truncated</c> session warning is reported. Absent
/// means the 10 MiB default; 0 disables the cap; negative values are rejected at parse time.
/// </summary>
[Collection(ScriptProcessExecutionCollection.Name)]
public class MaxOutputBytesTests
{
	private static readonly OrchestrationInfo s_defaultInfo = new("test-orchestration", "1.0.0", "run123", DateTimeOffset.UtcNow);
	private readonly IOrchestrationReporter _reporter = SerializingReporterProxy.CreateRecording();
	private IOrchestrationReporter _recorded => SerializingReporterProxy.Recorded(_reporter);

	private static OrchestrationExecutionContext Context() => new()
	{
		Parameters = new Dictionary<string, string>(),
		OrchestrationInfo = s_defaultInfo,
	};

	#region Budget and buffer

	[Fact]
	public void OutputLimits_Resolve_MapsNullToDefault_AndZeroToUnbounded()
	{
		OutputLimits.Resolve(null).Should().Be(OutputLimits.DefaultMaxOutputBytes);
		OutputLimits.DefaultMaxOutputBytes.Should().Be(10 * 1024 * 1024);
		OutputLimits.Resolve(0).Should().Be(long.MaxValue);
		OutputLimits.Resolve(1234).Should().Be(1234);
		OutputLimits.IsBounded(null).Should().BeTrue();
		OutputLimits.IsBounded(0).Should().BeFalse();
		OutputLimits.IsBounded(5).Should().BeTrue();
	}

	[Fact]
	public void BoundedOutputBuffer_DropsWholeLinesOnceTheBudgetIsSpent()
	{
		var budget = new OutputBudget(maxBytes: 12);
		var buffer = new BoundedOutputBuffer(budget);
		var newline = Encoding.UTF8.GetByteCount(Environment.NewLine);

		buffer.TryAppendLine("abcd").Should().BeTrue();          // 4 + nl
		buffer.TryAppendLine("efgh").Should().Be(8 + 2 * newline <= 12); // fits only with 1-byte newline
		buffer.TryAppendLine("this line is far too long").Should().BeFalse();

		budget.Truncated.Should().BeTrue();
		budget.DroppedBytes.Should().BeGreaterThan(0);
		buffer.ToStringTrimmed().Should().StartWith("abcd").And.NotContain("far too long");
	}

	[Fact]
	public void OutputBudget_IsSharedAcrossBuffers_SoTheCapIsPerStep()
	{
		var budget = new OutputBudget(maxBytes: 100);
		var stdout = new BoundedOutputBuffer(budget);
		var stderr = new BoundedOutputBuffer(budget);

		stdout.TryAppendLine(new string('o', 60)).Should().BeTrue();
		stderr.TryAppendLine(new string('e', 60)).Should().BeFalse("stdout already consumed most of the shared budget");

		budget.Truncated.Should().BeTrue();
		budget.DroppedBytes.Should().Be(60 + Encoding.UTF8.GetByteCount(Environment.NewLine));
	}

	[Fact]
	public void OutputBudget_CountsBytesNotChars()
	{
		var budget = new OutputBudget(maxBytes: 8);
		var buffer = new BoundedOutputBuffer(budget);

		// "日本" is 2 chars but 6 UTF-8 bytes (+ newline) — it must be charged as bytes.
		buffer.TryAppendLine("日本").Should().Be(6 + Encoding.UTF8.GetByteCount(Environment.NewLine) <= 8);
		buffer.TryAppendLine("日本").Should().BeFalse();
	}

	[Fact]
	public void OutputBudget_RejectsNegativeCap()
	{
		var act = () => new OutputBudget(-1);
		act.Should().Throw<ArgumentOutOfRangeException>();
	}

	#endregion

	#region Command

	private static CommandOrchestrationStep CommandPrinting(int lines, int? maxOutputBytes, bool includeStdErr = false) => new()
	{
		Name = "cmd",
		Type = OrchestrationStepType.Command,
		Command = "pwsh",
		Arguments = ["-NoProfile", "-Command", $"1..{lines} | ForEach-Object {{ Write-Output ('line-' + $_ + '-' + ('x' * 90)) }}"],
		MaxOutputBytes = maxOutputBytes,
		IncludeStdErr = includeStdErr,
	};

	[Fact]
	public async Task Command_OutputBeyondTheCap_IsTruncatedWithMarkerAndWarning()
	{
		// 200 lines × ~100 bytes ≈ 20 KB; cap at 2 KB.
		var executor = new CommandStepExecutor(_reporter, NullLoggerFactory.Instance.CreateLogger<CommandStepExecutor>());
		var step = CommandPrinting(lines: 200, maxOutputBytes: 2048);

		var result = await executor.ExecuteAsync(step, Context()).WaitAsync(TimeSpan.FromSeconds(60));

		result.Status.Should().Be(ExecutionStatus.Succeeded);
		result.Content.Should().Contain("line-1-");
		result.Content.Should().NotContain("line-200-");
		result.Content.Should().Contain("[output truncated: exceeded maxOutputBytes (2,048 bytes);");
		Encoding.UTF8.GetByteCount(result.Content).Should().BeLessThan(2048 + 200, "captured text stays within the cap plus the marker");
		result.Trace!.FinalResponse.Should().Contain("[output truncated");
		_recorded.Received(1).ReportSessionWarning(OutputLimits.WarningType, Arg.Is<string>(m => m.Contains("'cmd'") && m.Contains("2,048 bytes")));
		_recorded.Received(1).ReportContentDelta("cmd", Arg.Is<string>(chunk => chunk.Contains("[output truncated")));
		_recorded.DidNotReceive().ReportContentDelta("cmd", Arg.Is<string>(chunk => chunk.Contains("line-200-")));
	}

	[Fact]
	public async Task Command_OutputWithinTheCap_IsUntouched()
	{
		var executor = new CommandStepExecutor(_reporter, NullLoggerFactory.Instance.CreateLogger<CommandStepExecutor>());
		var step = CommandPrinting(lines: 5, maxOutputBytes: null);

		var result = await executor.ExecuteAsync(step, Context()).WaitAsync(TimeSpan.FromSeconds(60));

		result.Status.Should().Be(ExecutionStatus.Succeeded);
		result.Content.Should().Contain("line-5-").And.NotContain("[output truncated");
		_recorded.DidNotReceive().ReportSessionWarning(OutputLimits.WarningType, Arg.Any<string>());
	}

	[Fact]
	public async Task Command_ZeroDisablesTheCap()
	{
		var executor = new CommandStepExecutor(_reporter, NullLoggerFactory.Instance.CreateLogger<CommandStepExecutor>());
		var step = CommandPrinting(lines: 200, maxOutputBytes: 0);

		var result = await executor.ExecuteAsync(step, Context()).WaitAsync(TimeSpan.FromSeconds(60));

		result.Status.Should().Be(ExecutionStatus.Succeeded);
		result.Content.Should().Contain("line-200-").And.NotContain("[output truncated");
		_recorded.DidNotReceive().ReportSessionWarning(OutputLimits.WarningType, Arg.Any<string>());
	}

	#endregion

	#region Script

	[Fact]
	public async Task Script_OutputBeyondTheCap_IsTruncatedWithMarkerAndWarning()
	{
		var executor = new ScriptStepExecutor(_reporter, NullLoggerFactory.Instance.CreateLogger<ScriptStepExecutor>());
		var step = new ScriptOrchestrationStep
		{
			Name = "script",
			Type = OrchestrationStepType.Script,
			Shell = "pwsh",
			Script = "1..200 | ForEach-Object { Write-Output ('line-' + $_ + '-' + ('y' * 90)) }",
			MaxOutputBytes = 1024,
		};

		var result = await executor.ExecuteAsync(step, Context()).WaitAsync(TimeSpan.FromSeconds(60));

		result.Status.Should().Be(ExecutionStatus.Succeeded);
		result.Content.Should().Contain("line-1-").And.NotContain("line-200-");
		result.Content.Should().Contain("[output truncated: exceeded maxOutputBytes (1,024 bytes);");
		_recorded.Received(1).ReportSessionWarning(OutputLimits.WarningType, Arg.Is<string>(m => m.Contains("'script'")));
	}

	[Fact]
	public async Task Script_StdErrSharesTheBudget()
	{
		// stderr is captured even when not included in the output, so a chatty stderr must
		// count against the same cap — otherwise it could still grow without bound.
		var executor = new ScriptStepExecutor(_reporter, NullLoggerFactory.Instance.CreateLogger<ScriptStepExecutor>());
		var step = new ScriptOrchestrationStep
		{
			Name = "script",
			Type = OrchestrationStepType.Script,
			Shell = "pwsh",
			Script = "Write-Output 'stdout-ok'; 1..200 | ForEach-Object { [Console]::Error.WriteLine('err-' + $_ + '-' + ('z' * 90)) }",
			StrictMode = false,
			MaxOutputBytes = 1024,
		};

		var result = await executor.ExecuteAsync(step, Context()).WaitAsync(TimeSpan.FromSeconds(60));

		result.Status.Should().Be(ExecutionStatus.Succeeded);
		result.Content.Should().Contain("stdout-ok").And.Contain("[output truncated");
		result.Trace!.ResponseSegments.Should().ContainSingle().Which.Should().NotContain("err-200-");
		_recorded.Received(1).ReportSessionWarning(OutputLimits.WarningType, Arg.Any<string>());
	}

	#endregion

	#region Http

	private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
	{
		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
			=> Task.FromResult(respond(request));
	}

	private HttpStepExecutor HttpExecutor(Func<HttpRequestMessage, HttpResponseMessage> respond)
		=> new(new HttpClient(new StubHandler(respond)), _reporter, NullLoggerFactory.Instance.CreateLogger<HttpStepExecutor>());

	private static HttpOrchestrationStep HttpStep(int? maxOutputBytes) => new()
	{
		Name = "http",
		Type = OrchestrationStepType.Http,
		Url = "https://example.invalid/big",
		MaxOutputBytes = maxOutputBytes,
	};

	[Fact]
	public async Task Http_BodyBeyondTheCap_IsTruncatedWithMarkerAndWarning()
	{
		var body = string.Concat(Enumerable.Range(1, 1000).Select(i => $"row-{i:0000};"));
		var executor = HttpExecutor(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/plain") });

		var result = await executor.ExecuteAsync(HttpStep(maxOutputBytes: 500), Context()).WaitAsync(TimeSpan.FromSeconds(30));

		result.Status.Should().Be(ExecutionStatus.Succeeded);
		result.Content.Should().StartWith("row-0001;");
		result.Content.Should().NotContain("row-1000;");
		result.Content.Should().Contain("[output truncated: exceeded maxOutputBytes (500 bytes);");
		var expectedDropped = Encoding.UTF8.GetByteCount(body) - 500;
		result.Content.Should().Contain($"{expectedDropped:N0} bytes dropped");
		result.Trace!.FinalResponse.Should().Contain("[output truncated");
		_recorded.Received(1).ReportSessionWarning(OutputLimits.WarningType, Arg.Is<string>(m => m.Contains("'http'")));
	}

	[Fact]
	public async Task Http_BodyWithinTheCap_IsUntouched()
	{
		var executor = HttpExecutor(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"ok\":true}", Encoding.UTF8, "application/json") });

		var result = await executor.ExecuteAsync(HttpStep(maxOutputBytes: null), Context()).WaitAsync(TimeSpan.FromSeconds(30));

		result.Status.Should().Be(ExecutionStatus.Succeeded);
		result.Content.Should().Be("{\"ok\":true}");
		_recorded.DidNotReceive().ReportSessionWarning(OutputLimits.WarningType, Arg.Any<string>());
	}

	[Fact]
	public async Task Http_ZeroDisablesTheCap()
	{
		var body = new string('q', 3000);
		var executor = HttpExecutor(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });

		var result = await executor.ExecuteAsync(HttpStep(maxOutputBytes: 0), Context()).WaitAsync(TimeSpan.FromSeconds(30));

		result.Content.Should().Be(body);
		_recorded.DidNotReceive().ReportSessionWarning(OutputLimits.WarningType, Arg.Any<string>());
	}

	[Fact]
	public async Task Http_TruncationNeverEndsOnAPartialCharacter()
	{
		// Every char is 3 UTF-8 bytes; a cap of 10 bytes lands mid-character.
		var body = new string('日', 100);
		var executor = HttpExecutor(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/plain") });

		var result = await executor.ExecuteAsync(HttpStep(maxOutputBytes: 10), Context()).WaitAsync(TimeSpan.FromSeconds(30));

		result.Content.Should().StartWith("日日日");
		result.Content.Should().NotContain("\uFFFD");
		result.Content.Should().Contain("[output truncated");
	}

	[Fact]
	public async Task Http_HonoursTheDeclaredCharset()
	{
		var latin1 = Encoding.Latin1;
		var text = "caf\u00e9 cr\u00e8me";
		var executor = HttpExecutor(_ => new HttpResponseMessage(HttpStatusCode.OK)
		{
			Content = new ByteArrayContent(latin1.GetBytes(text)) { Headers = { ContentType = new("text/plain") { CharSet = "iso-8859-1" } } },
		});

		var result = await executor.ExecuteAsync(HttpStep(maxOutputBytes: null), Context()).WaitAsync(TimeSpan.FromSeconds(30));

		result.Content.Should().Be(text);
	}

	[Fact]
	public async Task Http_NonSuccessBody_IsAlsoBounded()
	{
		var body = new string('e', 5000);
		var executor = HttpExecutor(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent(body) });

		var result = await executor.ExecuteAsync(HttpStep(maxOutputBytes: 100), Context()).WaitAsync(TimeSpan.FromSeconds(30));

		result.Status.Should().Be(ExecutionStatus.Failed);
		result.Trace!.FinalResponse.Should().HaveLength(100 + OutputLimits.Marker(100, 4900).Length);
	}

	#endregion

	#region Parsing

	[Theory]
	[InlineData("command", "\"command\": \"echo\"")]
	[InlineData("script", "\"shell\": \"pwsh\", \"script\": \"Write-Output 1\"")]
	[InlineData("http", "\"url\": \"https://example.invalid\"")]
	public void Parse_MaxOutputBytes_IsReadForEveryOutputCapturingStep(string type, string body)
	{
		var json = $$"""
		{
			"name": "t",
			"description": "d",
			"steps": [
				{ "name": "a", "type": "{{type}}", {{body}} },
				{ "name": "b", "type": "{{type}}", {{body}}, "maxOutputBytes": 4096 },
				{ "name": "c", "type": "{{type}}", {{body}}, "maxOutputBytes": 0 }
			]
		}
		""";

		var orchestration = OrchestrationParser.ParseOrchestration(json, []);

		MaxOutputBytesOf(orchestration.Steps[0]).Should().BeNull();
		MaxOutputBytesOf(orchestration.Steps[1]).Should().Be(4096);
		MaxOutputBytesOf(orchestration.Steps[2]).Should().Be(0);
	}

	[Theory]
	[InlineData("-1")]
	[InlineData("\"big\"")]
	[InlineData("1.5")]
	public void Parse_MaxOutputBytes_RejectsInvalidValues(string literal)
	{
		var json = $$"""
		{
			"name": "t",
			"description": "d",
			"steps": [ { "name": "a", "type": "command", "command": "echo", "maxOutputBytes": {{literal}} } ]
		}
		""";

		var act = () => OrchestrationParser.ParseOrchestration(json, []);

		act.Should().Throw<JsonException>().WithMessage("*maxOutputBytes*");
	}

	private static int? MaxOutputBytesOf(OrchestrationStep step) => step switch
	{
		CommandOrchestrationStep c => c.MaxOutputBytes,
		ScriptOrchestrationStep s => s.MaxOutputBytes,
		HttpOrchestrationStep h => h.MaxOutputBytes,
		_ => throw new InvalidOperationException(step.GetType().Name),
	};

	#endregion
}
