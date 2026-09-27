using FluentAssertions;
using Orchestra.Cli.Commands;
using Orchestra.Host.Hosting;
using Spectre.Console.Cli;
using Spectre.Console.Testing;
using Xunit;

namespace Orchestra.Cli.Tests.Commands;

/// <summary>
/// <c>orchestra validate</c> and <c>run --dry-run</c> run without a host, but must accept what the
/// host accepts: a step that references an MCP declared globally in <c>orchestra.mcp.json</c>
/// (beside the effective <c>orchestra.json</c>). Before this catalog existed every such
/// orchestration failed validation with "not defined in MCP configuration", which also hid every
/// <c>when:</c> / template diagnostic behind it.
/// </summary>
[Collection(OrchestraEnvironmentCollection.Name)]
public sealed class ValidateGlobalMcpTests : IDisposable
{
	private readonly string _tempDir;
	private readonly Dictionary<string, string?> _saved = new();

	public ValidateGlobalMcpTests()
	{
		_tempDir = Path.Combine(Path.GetTempPath(), $"orchestra-validate-mcp-{Guid.NewGuid():N}");
		Directory.CreateDirectory(_tempDir);
		foreach (var name in new[] { "ORCHESTRA_CONFIG_PATH", "XDG_CONFIG_HOME" })
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

	private const string OrchestrationUsingGlobalMcp = """
		name: uses-global-mcp
		description: References an MCP declared only in orchestra.mcp.json.
		defaultModel: claude-opus-4.8
		steps:
		  - name: ask
		    type: Prompt
		    mcps: [foo]
		    systemPrompt: You are terse.
		    userPrompt: Say hi.
		  - name: gated
		    type: Transform
		    dependsOn: [ask]
		    when: "!empty({{ask.output}})"
		    template: "{{ask.output}}"
		""";

	private string PointDiscoveryAt(string mcpJson)
	{
		var configPath = Path.Combine(_tempDir, "orchestra.json");
		File.WriteAllText(configPath, "{}");
		File.WriteAllText(Path.Combine(_tempDir, OrchestraConfigLoader.McpConfigFileName), mcpJson);
		Environment.SetEnvironmentVariable("ORCHESTRA_CONFIG_PATH", configPath);
		var orchestrationPath = Path.Combine(_tempDir, "uses-global-mcp.yaml");
		File.WriteAllText(orchestrationPath, OrchestrationUsingGlobalMcp);
		return orchestrationPath;
	}

	[Fact]
	public void Validate_ResolvesMcpsDeclaredInTheGlobalSidecar()
	{
		var path = PointDiscoveryAt("""{ "mcps": [ { "name": "foo", "type": "remote", "endpoint": "http://127.0.0.1:1/mcp" } ] }""");

		var result = NewTester().Run("validate", path);

		result.ExitCode.Should().Be(0, result.Output);
		result.Output.Should().Contain("valid");
	}

	[Fact]
	public void Validate_WithoutTheGlobalMcp_StillReportsTheMissingReference_AndReachesWhenChecks()
	{
		var path = PointDiscoveryAt("""{ "mcps": [] }""");

		var result = NewTester().Run("validate", path);

		result.ExitCode.Should().Be(1);
		result.Output.Should().Contain("MCP 'foo'");
	}

	[Fact]
	public void Validate_BrokenGlobalSidecar_IsReportedAsAnMcpError()
	{
		var path = PointDiscoveryAt("{ not json");

		var result = NewTester().Run("validate", path);

		result.ExitCode.Should().Be(1);
		result.Output.Should().Contain("[mcp]").And.Contain(OrchestraConfigLoader.McpConfigFileName);
	}

	[Fact]
	public void Validate_WithGlobalMcp_StillReportsWhenSyntaxErrors()
	{
		var path = PointDiscoveryAt("""{ "mcps": [ { "name": "foo", "type": "remote", "endpoint": "http://127.0.0.1:1/mcp" } ] }""");
		File.WriteAllText(path, OrchestrationUsingGlobalMcp.Replace("when: \"!empty({{ask.output}})\"", "when: \"{{ask.output}} !== 'x'\""));

		var result = NewTester().Run("validate", path);

		result.ExitCode.Should().Be(1);
		result.Output.Should().Contain("gated");
	}
}
