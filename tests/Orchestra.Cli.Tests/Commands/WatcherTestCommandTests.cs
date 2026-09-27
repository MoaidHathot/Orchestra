using FluentAssertions;
using Orchestra.Cli.Commands;
using Spectre.Console.Cli;
using Spectre.Console.Testing;
using Xunit;

namespace Orchestra.Cli.Tests.Commands;

/// <summary>
/// <c>orchestra watcher test</c>: runs a fetcher from a library file without a host. The exit codes
/// (0 ok / 1 fetch or validation failure / 2 config error) are the contract scripts branch on.
/// </summary>
public sealed class WatcherTestCommandTests : IDisposable
{
	private readonly string _tempDir;

	public WatcherTestCommandTests()
	{
		_tempDir = Path.Combine(Path.GetTempPath(), $"orchestra-watcher-cli-{Guid.NewGuid():N}");
		Directory.CreateDirectory(_tempDir);
	}

	public void Dispose()
	{
		try { Directory.Delete(_tempDir, recursive: true); } catch { }
	}

	private static CommandAppTester NewTester()
	{
		var tester = new CommandAppTester();
		tester.Configure(Program.Configure);
		return tester;
	}

	private string WriteLibrary(string watchersJson)
	{
		var path = Path.Combine(_tempDir, "orchestra.watchers.json");
		File.WriteAllText(path, watchersJson);
		return path;
	}

	private const string GoodScript = """
		$r = [Console]::In.ReadToEnd() | ConvertFrom-Json
		$items = @(@{ id = 'a'; folder = $r.config.folder }, @{ id = 'b' })
		[Console]::Out.Write((@{ protocolVersion = '1'; items = $items; cursor = @{ page = 1 } } | ConvertTo-Json -Depth 5 -Compress))
		""";

	private string GoodLibrary()
	{
		File.WriteAllText(Path.Combine(_tempDir, "fetch.ps1"), GoodScript);
		return WriteLibrary("""
			{
			  "watchers": {
			    "local": {
			      "type": "poll-script",
			      "schedule": { "intervalSeconds": 5 },
			      "fetch": { "shell": "pwsh", "scriptFile": "./fetch.ps1", "timeoutSeconds": 30, "config": { "folder": "./inbox" } },
			      "dedup": { "key": "{{item.id}}" }
			    },
			    "broken": {
			      "type": "poll-script",
			      "schedule": { "intervalSeconds": 5 },
			      "fetch": { "shell": "pwsh", "script": "[Console]::Out.Write('{\"items\": {\"not\": \"an array\"}, \"extra\": 1}')", "timeoutSeconds": 30 }
			    },
			    "failing": {
			      "type": "poll-command",
			      "schedule": { "intervalSeconds": 5 },
			      "fetch": { "command": "pwsh", "arguments": ["-NoProfile", "-Command", "[Console]::Error.Write('kaput'); exit 4"], "timeoutSeconds": 30 }
			    }
			  }
			}
			""");
	}

	[Fact]
	public void Test_GoodWatcher_PrintsReport_AndExitsZero()
	{
		var library = GoodLibrary();

		var result = NewTester().Run("watcher", "test", "--watcher", "local", "--config-path", library, "--empty-state");

		result.ExitCode.Should().Be(WatcherTestCommand.SuccessExitCode, result.Output);
		result.Output.Should().Contain("== orchestra watcher test: local ==")
			.And.Contain("Type:").And.Contain("poll-script")
			.And.Contain("Command:").And.Contain("pwsh ./fetch.ps1")
			.And.Contain("Timeout:").And.Contain("30s")
			.And.Contain("stdin sent:").And.Contain("\"watcher\":\"local\"").And.Contain("\"cursor\":null")
			.And.Contain("stdout received")
			.And.Contain("schema validation: OK")
			.And.Contain("items extracted (jsonPath=$.items[*]): 2")
			.And.Contain("simulated subscribers: (none")
			.And.Contain("cursor advance preview:").And.Contain("before: null").And.Contain("after:  {\"page\":1}")
			.And.Contain("exit: 0");
	}

	[Fact]
	public void Test_InvalidOutput_ShowsValidationErrors_AndExitsOne()
	{
		var library = GoodLibrary();

		var result = NewTester().Run("watcher", "test", "--watcher", "broken", "--config-path", library);

		result.ExitCode.Should().Be(WatcherTestCommand.FetchFailureExitCode, result.Output);
		result.Output.Should().Contain("schema validation: FAILED")
			.And.Contain("/items")
			.And.Contain("/extra")
			.And.Contain("exit: 1");
	}

	[Fact]
	public void Test_FailingFetcher_ShowsStderrAndExitCode_AndExitsOne()
	{
		var library = GoodLibrary();

		var result = NewTester().Run("watcher", "test", "--watcher", "failing", "--config-path", library);

		result.ExitCode.Should().Be(WatcherTestCommand.FetchFailureExitCode, result.Output);
		result.Output.Should().Contain("exit 4").And.Contain("kaput").And.Contain("fetch failed:");
	}

	[Fact]
	public void Test_UnknownWatcher_ListsKnownOnes_AndExitsTwo()
	{
		var library = GoodLibrary();

		var result = NewTester().Run("watcher", "test", "--watcher", "nope", "--config-path", library);

		result.ExitCode.Should().Be(WatcherTestCommand.ConfigErrorExitCode, result.Output);
		result.Output.Should().Contain("'nope' is not declared").And.Contain("broken, failing, local");
	}

	[Fact]
	public void Test_MissingLibraryFile_ExitsTwo()
	{
		var result = NewTester().Run("watcher", "test", "--watcher", "x", "--config-path", Path.Combine(_tempDir, "missing.json"));

		result.ExitCode.Should().Be(WatcherTestCommand.ConfigErrorExitCode, result.Output);
		result.Output.Should().Contain("no orchestra.watchers.json found");
	}

	[Fact]
	public void Test_InvalidLibrary_ExitsTwoWithPath()
	{
		var library = WriteLibrary("""{ "watchers": { "w": { "type": "poll-command", "schedule": { }, "fetch": { "command": "x" } } } }""");

		var result = NewTester().Run("watcher", "test", "--watcher", "w", "--config-path", library);

		result.ExitCode.Should().Be(WatcherTestCommand.ConfigErrorExitCode, result.Output);
		result.Output.Should().Contain("watchers.w.schedule");
	}

	[Fact]
	public void Test_MissingWatcherOption_IsAValidationError()
	{
		var library = GoodLibrary();

		var act = () => NewTester().Run("watcher", "test", "--config-path", library);

		act.Should().Throw<CommandRuntimeException>().WithMessage("*--watcher <NAME> is required*");
	}

	[Fact]
	public void Test_FromStateAndEmptyState_AreMutuallyExclusive()
	{
		var library = GoodLibrary();

		var act = () => NewTester().Run("watcher", "test", "--watcher", "local", "--config-path", library, "--empty-state", "--from-state", "x.json");

		act.Should().Throw<CommandRuntimeException>().WithMessage("*mutually exclusive*");
	}

	[Fact]
	public void Test_SubscribersAndState_SimulateDedupAcrossRuns()
	{
		var library = GoodLibrary();
		var statePath = Path.Combine(_tempDir, "state.json");

		var first = NewTester().Run("watcher", "test", "--watcher", "local", "--config-path", library, "--subscribers", "orch-a,orch-b", "--save-state", statePath);
		first.ExitCode.Should().Be(0, first.Output);
		first.Output.Should().Contain("orch-a: 2 new, 0 seen").And.Contain("orch-b: 2 new, 0 seen").And.Contain("state saved to");
		File.Exists(statePath).Should().BeTrue();

		var second = NewTester().Run("watcher", "test", "--watcher", "local", "--config-path", library, "--subscribers", "orch-a", "--from-state", statePath);
		second.ExitCode.Should().Be(0, second.Output);
		second.Output.Should().Contain("orch-a: 0 new, 2 seen")
			.And.Contain("\"cursor\":{\"page\":1}", "the saved cursor is sent back to the fetcher")
			.And.Contain("(unchanged)");
	}

	[Fact]
	public void Test_MultipleTicks_CarryCursorForward()
	{
		var library = GoodLibrary();

		var result = NewTester().Run("watcher", "test", "--watcher", "local", "--config-path", library, "--ticks", "2", "--subscribers", "s");

		result.ExitCode.Should().Be(0, result.Output);
		result.Output.Should().Contain("-- tick 1/2 --").And.Contain("-- tick 2/2 --")
			.And.Contain("s: 2 new, 0 seen").And.Contain("s: 0 new, 2 seen");
	}

	[Fact]
	public void Test_PrintInput_ShowsIndentedPayload()
	{
		var library = GoodLibrary();

		var result = NewTester().Run("watcher", "test", "--watcher", "local", "--config-path", library, "--print-input");

		result.ExitCode.Should().Be(0, result.Output);
		result.Output.Should().Contain("\"protocolVersion\": \"1\"").And.Contain("\"folder\": \"./inbox\"");
	}

	[Fact]
	public void WatcherBranch_Help_ListsTest()
	{
		var result = NewTester().Run("watcher", "--help");

		result.ExitCode.Should().Be(0);
		result.Output.Should().Contain("test");
	}
}
