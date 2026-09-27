using System.Text.Json;
using FluentAssertions;
using Orchestra.Cli.Commands;
using Orchestra.Host.Hosting;
using Spectre.Console.Cli;
using Spectre.Console.Testing;
using Xunit;

namespace Orchestra.Cli.Tests.Commands;

/// <summary>
/// <c>orchestra config show</c>: which orchestra.json is in effect, its sidecars and the
/// resolved settings. Uses ORCHESTRA_CONFIG_PATH to point discovery at a temp file.
/// </summary>
[Collection(OrchestraEnvironmentCollection.Name)]
public sealed class ConfigShowCommandTests : IDisposable
{
	private readonly string _tempDir;
	private readonly Dictionary<string, string?> _saved = new();

	public ConfigShowCommandTests()
	{
		_tempDir = Path.Combine(Path.GetTempPath(), $"orchestra-config-show-{Guid.NewGuid():N}");
		Directory.CreateDirectory(_tempDir);
		foreach (var name in new[] { "ORCHESTRA_CONFIG_PATH", "XDG_CONFIG_HOME", "ORCHESTRA_URL" })
		{
			_saved[name] = Environment.GetEnvironmentVariable(name);
			Environment.SetEnvironmentVariable(name, null);
		}
	}

	public void Dispose()
	{
		foreach (var (name, value) in _saved)
			Environment.SetEnvironmentVariable(name, value);
		try { Directory.Delete(_tempDir, recursive: true); }
		catch { /* best-effort */ }
	}

	private static CommandAppTester NewTester()
	{
		var tester = new CommandAppTester();
		tester.Configure(Program.Configure);
		return tester;
	}

	[Fact]
	public void Show_ReportsTheConfigInEffect_ItsSidecars_AndResolvedSettings()
	{
		var configPath = Path.Combine(_tempDir, "orchestra.json");
		File.WriteAllText(configPath, """
			{
				"dataPath": "./data",
				"defaultModel": "claude-opus-4.8",
				"defaultMaxConcurrency": 4,
				"retention": { "maxRunsPerOrchestration": 50 },
				"logging": { "file": { "enabled": false } }
			}
			""");
		File.WriteAllText(Path.Combine(_tempDir, OrchestraConfigLoader.McpConfigFileName), "[]");
		Environment.SetEnvironmentVariable("ORCHESTRA_CONFIG_PATH", configPath);

		var result = NewTester().Run("config", "show");

		result.ExitCode.Should().Be(0);
		result.Output.Should().Contain("orchestra.json").And.Contain("from ORCHESTRA_CONFIG_PATH");
		result.Output.Should().Contain(Path.Combine(_tempDir, OrchestraConfigLoader.McpConfigFileName));
		result.Output.Should().Contain("claude-opus-4.8");
		result.Output.Should().Contain("defaultMaxConcurrency").And.Contain("4");
		result.Output.Should().Contain("maxRunsPerOrchestration=50");
		result.Output.Should().Contain("logging.file").And.Contain("off");
		result.Output.Should().Contain("ORCHESTRA_CONFIG_PATH = ");
	}

	[Fact]
	public void Show_Json_IsMachineReadable()
	{
		var configPath = Path.Combine(_tempDir, "orchestra.json");
		File.WriteAllText(configPath, """{ "defaultModel": "claude-opus-4.8" }""");
		Environment.SetEnvironmentVariable("ORCHESTRA_CONFIG_PATH", configPath);

		var result = NewTester().Run("config", "show", "--format", "json");

		result.ExitCode.Should().Be(0);
		using var doc = JsonDocument.Parse(result.Output);
		doc.RootElement.GetProperty("configPath").GetString().Should().Be(configPath);
		doc.RootElement.GetProperty("source").GetString().Should().Be("EnvironmentVariable");
		doc.RootElement.GetProperty("effective").EnumerateArray()
			.Should().Contain(e => e.GetProperty("key").GetString() == "defaultModel" && e.GetProperty("value").GetString() == "claude-opus-4.8");
	}

	[Fact]
	public void Show_UnparseableConfig_ExitsOne_AndNamesTheProblem()
	{
		var configPath = Path.Combine(_tempDir, "orchestra.json");
		File.WriteAllText(configPath, "{ not json");
		Environment.SetEnvironmentVariable("ORCHESTRA_CONFIG_PATH", configPath);

		var result = NewTester().Run("config", "show");

		result.ExitCode.Should().Be(1);
		result.Output.Should().Contain("error:");
	}

	[Fact]
	public void Show_NoConfigAnywhere_SaysSo_AndPointsAtInit()
	{
		// An explicit path to a file that does not exist -> loader falls back to defaults.
		Environment.SetEnvironmentVariable("ORCHESTRA_CONFIG_PATH", Path.Combine(_tempDir, "missing.json"));
		Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Path.Combine(_tempDir, "xdg-empty"));

		var result = NewTester().Run("config", "show");

		result.Output.Should().Contain("orchestra init");
	}

	[Fact]
	public void Show_InvalidFormat_IsRejected()
	{
		Action act = () => NewTester().Run("config", "show", "--format", "yaml");

		act.Should().Throw<CommandRuntimeException>().WithMessage("*expected text or json*");
	}

	[Fact]
	public void DescribeSource_CoversEveryValue()
	{
		foreach (var source in Enum.GetValues<OrchestraConfigSource>())
			ConfigReport.DescribeSource(source).Should().NotBeNullOrWhiteSpace();
	}
}
