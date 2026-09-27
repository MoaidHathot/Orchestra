using System.Text.Json.Nodes;
using FluentAssertions;
using Orchestra.Watchers.Config;
using Orchestra.Watchers.Models;
using Xunit;

namespace Orchestra.Watchers.Tests;

public class WatcherConfigLoaderTests : IDisposable
{
	private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"orchestra-watcher-config-{Guid.NewGuid():N}");

	public WatcherConfigLoaderTests() => Directory.CreateDirectory(_tempDir);

	public void Dispose()
	{
		try { Directory.Delete(_tempDir, recursive: true); } catch { }
	}

	private const string ValidLibrary = """
		{
		  // comments and trailing commas are fine
		  "$schema": "../schemas/orchestra.watchers.schema.json",
		  "watchers": {
		    "pr-comments": {
		      "type": "poll-command",
		      "schedule": { "intervalSeconds": 15 },
		      "fetch": { "command": "dnx", "arguments": ["Icm.Cli", "--", "incidents", "--json"], "timeoutSeconds": 30, "config": { "org": "contoso" } },
		      "items": { "jsonPath": "$.items[*]" },
		      "dedup": { "key": "{{item.id}}", "mode": "fifo", "fifo": { "maxEntries": 500, "ttlDays": 2 } },
		      "failurePolicy": "fail",
		    },
		    "folder": {
		      "type": "poll-script",
		      "enabled": false,
		      "schedule": { "cron": "*/30 * * * * *" },
		      "fetch": { "shell": "pwsh", "scriptFile": "./watchers/fetch.ps1", "script": null, "includeStdErr": true },
		      "dedup": { "key": "{{item.path}}", "mode": "presence", "presence": { "graceTicks": 5 } }
		    }
		  }
		}
		""";

	[Fact]
	public void Parse_ValidLibrary_ExposesEntriesByNameWithTypedFields()
	{
		var library = WatcherConfigLoader.Parse(ValidLibrary, Path.Combine(_tempDir, "orchestra.watchers.json"));

		library.Watchers.Should().HaveCount(2);
		library.SourcePath.Should().EndWith("orchestra.watchers.json");

		var command = library.Watchers["pr-comments"].Should().BeOfType<PollCommandWatcher>().Subject;
		command.Name.Should().Be("pr-comments");
		command.BaseDirectory.Should().Be(_tempDir);
		command.Enabled.Should().BeTrue();
		command.Schedule.IntervalSeconds.Should().Be(15);
		command.Fetch.Command.Should().Be("dnx");
		command.Fetch.Arguments.Should().Equal("Icm.Cli", "--", "incidents", "--json");
		command.Fetch.TimeoutSeconds.Should().Be(30);
		command.Fetch.Config!["org"]!.GetValue<string>().Should().Be("contoso");
		command.Dedup!.Mode.Should().Be(WatcherDedupMode.Fifo);
		command.Dedup.EffectiveFifo.MaxEntries.Should().Be(500);
		command.Dedup.EffectiveFifo.TtlDays.Should().Be(2);
		command.FailurePolicy.Should().Be(WatcherFailurePolicy.Fail);

		var script = library.Watchers["folder"].Should().BeOfType<PollScriptWatcher>().Subject;
		script.Enabled.Should().BeFalse();
		script.Schedule.Cron.Should().Be("*/30 * * * * *");
		script.Fetch.Shell.Should().Be("pwsh");
		script.Fetch.ScriptFile.Should().Be("./watchers/fetch.ps1");
		script.Fetch.IncludeStdErr.Should().BeTrue();
		script.Fetch.TimeoutSeconds.Should().Be(60, "default applies when omitted");
		script.Dedup!.Mode.Should().Be(WatcherDedupMode.Presence);
		script.Dedup.EffectivePresence.GraceTicks.Should().Be(5);
		script.FailurePolicy.Should().Be(WatcherFailurePolicy.Warn, "default applies when omitted");
	}

	[Fact]
	public void Load_MissingFile_ReturnsEmptyLibrary()
	{
		var library = WatcherConfigLoader.Load(Path.Combine(_tempDir, "nope.json"));

		library.Watchers.Should().BeEmpty();
		library.SourcePath.Should().BeNull();
	}

	[Fact]
	public void Load_ExpandsEnvironmentVariables()
	{
		var variable = $"ORCHESTRA_TEST_{Guid.NewGuid():N}";
		Environment.SetEnvironmentVariable(variable, "from-env");
		try
		{
			var path = Path.Combine(_tempDir, "orchestra.watchers.json");
			File.WriteAllText(path, $$"""
				{ "watchers": { "w": { "type": "poll-command", "schedule": { "intervalSeconds": 5 }, "fetch": { "command": "${{{variable}}}" } } } }
				""");

			var library = WatcherConfigLoader.Load(path);

			((PollCommandWatcher)library.Watchers["w"]).Fetch.Command.Should().Be("from-env");
		}
		finally
		{
			Environment.SetEnvironmentVariable(variable, null);
		}
	}

	[Theory]
	[InlineData("""{ "watchers": { "w": { "type": "poll-command", "schedule": { "intervalSeconds": 5, "cron": "* * * * *" }, "fetch": { "command": "x" } } } }""", "watchers.w.schedule", "not both")]
	[InlineData("""{ "watchers": { "w": { "type": "poll-command", "schedule": { }, "fetch": { "command": "x" } } } }""", "watchers.w.schedule", "exactly one")]
	[InlineData("""{ "watchers": { "w": { "type": "poll-command", "schedule": { "intervalSeconds": 0 }, "fetch": { "command": "x" } } } }""", "watchers.w.schedule.intervalSeconds", "positive")]
	[InlineData("""{ "watchers": { "w": { "type": "poll-command", "schedule": { "cron": "not a cron" }, "fetch": { "command": "x" } } } }""", "watchers.w.schedule.cron", "invalid cron")]
	[InlineData("""{ "watchers": { "w": { "type": "poll-script", "schedule": { "intervalSeconds": 5 }, "fetch": { "shell": "pwsh", "script": "1", "scriptFile": "a.ps1" } } } }""", "watchers.w.fetch", "not both")]
	[InlineData("""{ "watchers": { "w": { "type": "poll-script", "schedule": { "intervalSeconds": 5 }, "fetch": { "shell": "pwsh" } } } }""", "watchers.w.fetch", "exactly one")]
	[InlineData("""{ "watchers": { "w": { "type": "poll-command", "schedule": { "intervalSeconds": 5 }, "fetch": { "command": "x" }, "dedup": { "mode": "fifo", "presence": { "graceTicks": 2 } } } } }""", "watchers.w.dedup.presence", "not allowed")]
	[InlineData("""{ "watchers": { "w": { "type": "poll-command", "schedule": { "intervalSeconds": 5 }, "fetch": { "command": "x" }, "dedup": { "mode": "presence", "fifo": { "maxEntries": 2 } } } } }""", "watchers.w.dedup.fifo", "not allowed")]
	[InlineData("""{ "watchers": { "w": { "type": "poll-command", "schedule": { "intervalSeconds": 5 }, "fetch": { "command": "x" }, "dedup": { "fifo": { "maxEntries": 0 } } } } }""", "watchers.w.dedup.fifo.maxEntries", "positive")]
	[InlineData("""{ "watchers": { "w": { "type": "poll-command", "schedule": { "intervalSeconds": 5 }, "fetch": { "command": "x", "timeoutSeconds": 0 } } } }""", "watchers.w.fetch.timeoutSeconds", "positive")]
	[InlineData("""{ "watchers": { "w": { "type": "poll-command", "schedule": { "intervalSeconds": 5 }, "fetch": { "command": "x" }, "items": { "jsonPath": "items..x" } } } }""", "watchers.w.items.jsonPath", "must start with")]
	[InlineData("""{ "watchers": { "w": { "type": "poll-command", "fetch": { "command": "x" } } } }""", "watchers.w", "schedule")]
	[InlineData("""{ "watchers": { "w": { "type": "poll-command", "schedule": { "intervalSeconds": 5 } } } }""", "watchers.w", "fetch")]
	[InlineData("""{ "watchers": { "w": { "schedule": { "intervalSeconds": 5 }, "fetch": { "command": "x" } } } }""", "watchers.w.type", "required")]
	[InlineData("""{ "watchers": { "w": { "type": "poll-http", "schedule": { "intervalSeconds": 5 }, "fetch": { "command": "x" } } } }""", "watchers.w.type", "poll-http")]
	[InlineData("""{ "watchers": { "w": { "type": "poll-command", "schedule": { "intervalSeconds": 5 }, "fetch": { "command": "x" }, "failurePolicy": "explode" } } }""", "watchers.w", "failurePolicy")]
	[InlineData("""{ "watchers": [] }""", "watchers", "object")]
	[InlineData("""{ "watcher": {} }""", "watcher", "unknown top-level")]
	[InlineData("""not json""", "$", "not valid JSON")]
	public void Parse_InvalidLibrary_FailsWithPathAndReason(string json, string expectedPath, string expectedFragment)
	{
		var act = () => WatcherConfigLoader.Parse(json);

		var ex = act.Should().Throw<WatcherValidationException>().Which;
		ex.Path.Should().Be(expectedPath);
		ex.Message.Should().ContainEquivalentOf(expectedFragment);
	}

	[Fact]
	public void ParseInlineDefinition_RequiresName_WithTheDocumentedMessage()
	{
		var definition = JsonNode.Parse("""{ "type": "poll-command", "schedule": { "intervalSeconds": 5 }, "fetch": { "command": "x" } }""")!.AsObject();

		var act = () => WatcherConfigLoader.ParseInlineDefinition(definition, _tempDir);

		act.Should().Throw<WatcherValidationException>()
			.Which.Message.Should().Contain("inline watcher definitions require a `name` field to be referenced for state and logging.");
	}

	[Fact]
	public void ParseInlineDefinition_StripsNameAndSetsBaseDirectory()
	{
		var definition = JsonNode.Parse("""{ "name": "local", "type": "poll-script", "schedule": { "intervalSeconds": 5 }, "fetch": { "shell": "pwsh", "script": "exit 0" }, "dedup": { "key": "{{item.id}}" } }""")!.AsObject();

		var entry = WatcherConfigLoader.ParseInlineDefinition(definition, _tempDir);

		entry.Should().BeOfType<PollScriptWatcher>();
		entry.Name.Should().Be("local");
		entry.BaseDirectory.Should().Be(_tempDir);
		definition.ContainsKey("name").Should().BeTrue("the caller's node must not be mutated");
	}

	[Fact]
	public void Parse_DiscriminatorMayAppearAfterOtherProperties()
	{
		var json = """{ "watchers": { "w": { "schedule": { "intervalSeconds": 5 }, "fetch": { "command": "x" }, "type": "poll-command" } } }""";

		WatcherConfigLoader.Parse(json).Watchers["w"].Should().BeOfType<PollCommandWatcher>();
	}

	[Fact]
	public void Parse_SixFieldCron_IsAccepted()
	{
		var json = """{ "watchers": { "w": { "type": "poll-command", "schedule": { "cron": "*/15 * * * * *" }, "fetch": { "command": "x" } } } }""";

		WatcherConfigLoader.Parse(json).Watchers["w"].Schedule.Cron.Should().Be("*/15 * * * * *");
	}
}
