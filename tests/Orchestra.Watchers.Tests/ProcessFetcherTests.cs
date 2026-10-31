using System.Text.Json.Nodes;
using FluentAssertions;
using Orchestra.Watchers.Models;
using Orchestra.Watchers.Runtime;
using Xunit;

namespace Orchestra.Watchers.Tests;

/// <summary>
/// Real-process fetcher tests. They spawn <c>pwsh</c> (present on every CI image Orchestra
/// targets and required by the Script step executor anyway), so each case costs a second or so.
/// </summary>
public class ProcessFetcherTests : IDisposable
{
	private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"orchestra-watcher-fetch-{Guid.NewGuid():N}");

	public ProcessFetcherTests() => Directory.CreateDirectory(_tempDir);

	public void Dispose()
	{
		try
		{ Directory.Delete(_tempDir, recursive: true); }
		catch { }
	}

	private static WatcherFetchRequest Request(WatcherEntry entry, JsonNode? cursor = null)
		=> WatcherFetchRequest.Create(entry, cursor, ["sub-1"]);

	private static PollCommandWatcher PwshCommand(string script, int timeoutSeconds = 30, bool includeStdErr = false) => new()
	{
		Name = "cmd",
		Schedule = new WatcherSchedule { IntervalSeconds = 60 },
		Fetch = new WatcherFetchCommandConfig
		{
			Command = "pwsh",
			Arguments = ["-NoProfile", "-NonInteractive", "-Command", script],
			TimeoutSeconds = timeoutSeconds,
			IncludeStdErr = includeStdErr,
			Config = new JsonObject { ["folder"] = "./inbox" },
		},
	};

	private PollScriptWatcher PwshScript(string? inline = null, string? scriptFile = null, int timeoutSeconds = 30, string? workingDirectory = null) => new()
	{
		Name = "script",
		BaseDirectory = _tempDir,
		Schedule = new WatcherSchedule { IntervalSeconds = 60 },
		Fetch = new WatcherFetchScriptConfig
		{
			Shell = "pwsh",
			Script = inline,
			ScriptFile = scriptFile,
			TimeoutSeconds = timeoutSeconds,
			WorkingDirectory = workingDirectory,
			Config = new JsonObject { ["folder"] = "./inbox" },
			Env = new Dictionary<string, string> { ["ORCHESTRA_WATCHER_TEST_ENV"] = "env-value" },
		},
	};

	[Fact]
	public async Task Command_SuccessfulExitWithValidJson_PopulatesResult()
	{
		var entry = PwshCommand("[Console]::Out.Write('{\"protocolVersion\":\"1\",\"items\":[{\"id\":\"a\"}],\"cursor\":{\"n\":1}}')");

		var result = await new CommandWatcherFetcher().RunAsync(entry, Request(entry), CancellationToken.None);

		result.Succeeded.Should().BeTrue(result.FailureReason);
		result.ExitCode.Should().Be(0);
		result.Output!["items"]!.AsArray().Should().HaveCount(1);
		result.Output["cursor"]!["n"]!.GetValue<int>().Should().Be(1);
		result.Elapsed.Should().BePositive();
	}

	[Fact]
	public async Task Command_ReadsTheRequestFromStdin()
	{
		var entry = PwshCommand("$r = [Console]::In.ReadToEnd() | ConvertFrom-Json; [Console]::Out.Write((@{ protocolVersion='1'; items=@(@{ watcher=$r.watcher; folder=$r.config.folder; subs=@($r.subscribers); cursor=$r.cursor }) } | ConvertTo-Json -Depth 5 -Compress))");
		var cursor = JsonNode.Parse("""{ "lastEventId": 7 }""");

		var result = await new CommandWatcherFetcher().RunAsync(entry, Request(entry, cursor), CancellationToken.None);

		result.Succeeded.Should().BeTrue(result.FailureReason);
		var item = result.Output!["items"]![0]!;
		item["watcher"]!.GetValue<string>().Should().Be("cmd");
		item["folder"]!.GetValue<string>().Should().Be("./inbox");
		item["subs"]!.AsArray().Select(s => s!.GetValue<string>()).Should().Equal("sub-1");
		item["cursor"]!["lastEventId"]!.GetValue<int>().Should().Be(7);
	}

	[Fact]
	public async Task Command_Timeout_ReportsTimeoutAndKillsProcess()
	{
		var entry = PwshCommand("Start-Sleep -Seconds 30; [Console]::Out.Write('{}')", timeoutSeconds: 1);

		var started = DateTime.UtcNow;
		var result = await new CommandWatcherFetcher().RunAsync(entry, Request(entry), CancellationToken.None);

		result.Succeeded.Should().BeFalse();
		result.TimedOut.Should().BeTrue();
		result.FailureReason.Should().Contain("timed out after 1s");
		(DateTime.UtcNow - started).Should().BeLessThan(TimeSpan.FromSeconds(20), "the child must be killed, not awaited");
	}

	[Fact]
	public async Task Command_NonZeroExit_ReportsFailureWithStderr()
	{
		var entry = PwshCommand("[Console]::Error.Write('something broke'); exit 3");

		var result = await new CommandWatcherFetcher().RunAsync(entry, Request(entry), CancellationToken.None);

		result.Succeeded.Should().BeFalse();
		result.ExitCode.Should().Be(3);
		result.RawStdErr.Should().Contain("something broke");
		result.FailureReason.Should().Contain("exit code 3").And.Contain("something broke");
	}

	[Fact]
	public async Task Command_InvalidJsonOnStdout_ReportsParseFailure()
	{
		var entry = PwshCommand("[Console]::Out.Write('this is not json')");

		var result = await new CommandWatcherFetcher().RunAsync(entry, Request(entry), CancellationToken.None);

		result.Succeeded.Should().BeFalse();
		result.ExitCode.Should().Be(0);
		result.FailureReason.Should().Contain("not valid JSON");
		result.RawStdout.Should().Be("this is not json");
	}

	[Fact]
	public async Task Command_EmptyStdout_ReportsFailure()
	{
		var entry = PwshCommand("exit 0");

		var result = await new CommandWatcherFetcher().RunAsync(entry, Request(entry), CancellationToken.None);

		result.Succeeded.Should().BeFalse();
		result.FailureReason.Should().Contain("no output");
	}

	[Fact]
	public async Task Command_MissingExecutable_ReportsStartFailure()
	{
		var entry = new PollCommandWatcher
		{
			Name = "missing",
			Schedule = new WatcherSchedule { IntervalSeconds = 60 },
			Fetch = new WatcherFetchCommandConfig { Command = $"orchestra-no-such-binary-{Guid.NewGuid():N}" },
		};

		var result = await new CommandWatcherFetcher().RunAsync(entry, Request(entry), CancellationToken.None);

		result.Succeeded.Should().BeFalse();
		result.FailureReason.Should().Contain("failed to start");
	}

	[Fact]
	public async Task Command_IncludeStdErr_MergesStreamsBeforeParsing()
	{
		// stdout has the opening, stderr the rest: only valid when merged.
		var entry = PwshCommand("[Console]::Out.Write('{\"items\":'); [Console]::Error.Write('[]}')", includeStdErr: true);

		var result = await new CommandWatcherFetcher().RunAsync(entry, Request(entry), CancellationToken.None);

		result.Succeeded.Should().BeTrue(result.FailureReason);
		result.Output!["items"]!.AsArray().Should().BeEmpty();
	}

	[Fact]
	public async Task Script_InlineScript_RunsAndSeesEnvAndStdin()
	{
		var marker = Guid.NewGuid().ToString("N");
		await File.WriteAllTextAsync(Path.Combine(_tempDir, marker), marker);
		var entry = PwshScript(inline: "$r = [Console]::In.ReadToEnd() | ConvertFrom-Json; [Console]::Out.Write((@{ protocolVersion='1'; items=@(@{ env=$env:ORCHESTRA_WATCHER_TEST_ENV; watcher=$r.watcher; cwd=(Get-Location).Path }) } | ConvertTo-Json -Depth 5 -Compress))");

		var result = await new ScriptWatcherFetcher().RunAsync(entry, Request(entry), CancellationToken.None);

		result.Succeeded.Should().BeTrue(result.FailureReason);
		var item = result.Output!["items"]![0]!;
		item["env"]!.GetValue<string>().Should().Be("env-value");
		item["watcher"]!.GetValue<string>().Should().Be("script");
		(await File.ReadAllTextAsync(Path.Combine(item["cwd"]!.GetValue<string>(), marker)))
			.Should().Be(marker, "the working directory defaults to the entry's base directory, including symlinked macOS temp roots");
	}

	[Fact]
	public async Task Script_ScriptFile_RelativeToBaseDirectory_Works()
	{
		File.Copy(TestEntries.Fixture("echo-fetch.ps1"), Path.Combine(_tempDir, "fetch.ps1"));
		var entry = PwshScript(scriptFile: "./fetch.ps1");
		var cursor = JsonNode.Parse("""{ "page": 2 }""");

		var result = await new ScriptWatcherFetcher().RunAsync(entry, Request(entry, cursor), CancellationToken.None);

		result.Succeeded.Should().BeTrue(result.FailureReason);
		var validator = new WatcherOutputValidator();
		validator.TryValidate(result.Output, out var errors).Should().BeTrue(string.Join("; ", errors));
		var output = new WatcherFetcherOutput(result.Output!.AsObject());
		var items = output.SelectItems(null);
		items.Should().HaveCount(2);
		items[0]!["watcher"]!.GetValue<string>().Should().Be("script");
		items[0]!["config"]!["folder"]!.GetValue<string>().Should().Be("./inbox");
		output.Cursor!["lastEventId"]!.GetValue<int>().Should().Be(42);
		output.Cursor["previous"]!["page"]!.GetValue<int>().Should().Be(2);
		output.Diagnostics!["message"]!.GetValue<string>().Should().Be("fixture ok");
	}

	[Fact]
	public async Task Script_ScriptFile_NonAsciiRoundTrips()
	{
		var script = Path.Combine(_tempDir, "unicode.ps1");
		await File.WriteAllTextAsync(script, "$r = [Console]::In.ReadToEnd() | ConvertFrom-Json; [Console]::Out.Write((@{ items=@(@{ name='caf\u00e9 \u2014 \u201Cquoted\u201D'; echo=$r.config.folder }) } | ConvertTo-Json -Depth 3 -Compress))");
		var entry = PwshScript(scriptFile: "unicode.ps1");

		var result = await new ScriptWatcherFetcher().RunAsync(entry, Request(entry), CancellationToken.None);

		result.Succeeded.Should().BeTrue(result.FailureReason);
		result.Output!["items"]![0]!["name"]!.GetValue<string>().Should().Be("caf\u00e9 \u2014 \u201Cquoted\u201D");
	}

	[Fact]
	public async Task Script_MissingScriptFile_ReportsPreparationFailure()
	{
		var entry = PwshScript(scriptFile: "does-not-exist.ps1");

		var result = await new ScriptWatcherFetcher().RunAsync(entry, Request(entry), CancellationToken.None);

		result.Succeeded.Should().BeFalse();
		result.FailureReason.Should().Contain("could not prepare").And.Contain("does-not-exist.ps1");
	}

	[Fact]
	public async Task Script_NonZeroExit_PropagatesFromDotSourcedFile()
	{
		var script = Path.Combine(_tempDir, "fail.ps1");
		await File.WriteAllTextAsync(script, "[Console]::Error.Write('nope'); exit 7");
		var entry = PwshScript(scriptFile: "fail.ps1");

		var result = await new ScriptWatcherFetcher().RunAsync(entry, Request(entry), CancellationToken.None);

		result.Succeeded.Should().BeFalse();
		result.ExitCode.Should().Be(7);
		result.RawStdErr.Should().Contain("nope");
	}

	[Fact]
	public async Task Script_Timeout_IsReported()
	{
		var entry = PwshScript(inline: "Start-Sleep -Seconds 30", timeoutSeconds: 1);

		var result = await new ScriptWatcherFetcher().RunAsync(entry, Request(entry), CancellationToken.None);

		result.TimedOut.Should().BeTrue();
		result.Succeeded.Should().BeFalse();
	}

	[Fact]
	public async Task Fetcher_Cancellation_PropagatesAsOperationCanceled()
	{
		var entry = PwshScript(inline: "Start-Sleep -Seconds 30", timeoutSeconds: 60);
		using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

		var act = () => new ScriptWatcherFetcher().RunAsync(entry, Request(entry), cts.Token);

		await act.Should().ThrowAsync<OperationCanceledException>();
	}

	[Fact]
	public void Factory_PicksFetcherByEntryType()
	{
		var factory = new WatcherFetcherFactory();

		factory.Create(PwshCommand("x")).Should().BeOfType<CommandWatcherFetcher>();
		factory.Create(PwshScript(inline: "x")).Should().BeOfType<ScriptWatcherFetcher>();
	}
}
