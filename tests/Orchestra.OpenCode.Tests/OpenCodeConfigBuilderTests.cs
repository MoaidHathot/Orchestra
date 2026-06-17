using System.Text.Json;
using FluentAssertions;
using Orchestra.Engine;

namespace Orchestra.OpenCode.Tests;

public class OpenCodeConfigBuilderTests
{
	private static OpenCodeModelRef Model => OpenCodeModelRef.Parse("github-copilot/claude-opus-4.8", "github-copilot");

	private static OpenCodeStepPlan Build(
		string? system = "sys",
		ReasoningLevel? reasoning = null,
		IReadOnlyList<Subagent>? subagents = null,
		IReadOnlyList<Mcp>? mcps = null,
		IReadOnlyList<string>? excludedTools = null)
		=> OpenCodeConfigBuilder.Build(Model, system, reasoning, subagents ?? [], mcps ?? [], excludedTools ?? [], "github-copilot");

	private static JsonElement ConfigJson(OpenCodeStepPlan plan)
		=> JsonDocument.Parse(JsonSerializer.Serialize(plan.Config)).RootElement.Clone();

	[Fact]
	public void Build_Nothing_ProducesEmptyConfig()
	{
		var plan = Build();
		plan.HasConfig.Should().BeFalse();
		plan.PrimaryAgentName.Should().BeNull();
	}

	[Fact]
	public void Build_ReasoningOnly_DefinesPrimaryAgentWithReasoningEffort()
	{
		var plan = Build(system: "you are helpful", reasoning: ReasoningLevel.High);

		plan.HasConfig.Should().BeTrue();
		plan.PrimaryAgentName.Should().Be("orchestra-primary");

		var agent = ConfigJson(plan).GetProperty("agent").GetProperty("orchestra-primary");
		agent.GetProperty("mode").GetString().Should().Be("primary");
		agent.GetProperty("model").GetString().Should().Be("github-copilot/claude-opus-4.8");
		agent.GetProperty("prompt").GetString().Should().Be("you are helpful");
		agent.GetProperty("reasoningEffort").GetString().Should().Be("high");
	}

	[Fact]
	public void Build_ExcludedTools_DisablesNamedToolsOnPrimaryAgent()
	{
		var plan = Build(system: null, excludedTools: ["bash", "edit"]);

		plan.HasConfig.Should().BeTrue();
		plan.PrimaryAgentName.Should().Be("orchestra-primary");

		var tools = ConfigJson(plan).GetProperty("agent").GetProperty("orchestra-primary").GetProperty("tools");
		tools.GetProperty("bash").GetBoolean().Should().BeFalse();
		tools.GetProperty("edit").GetBoolean().Should().BeFalse();
	}

	[Fact]
	public void Build_Subagents_DefinesSubagentEntriesAndScopedTaskPermission()
	{
		var subagents = new[]
		{
			new Subagent { Name = "Data Researcher", Description = "Finds data", Prompt = "You research data." },
			new Subagent { Name = "Writer", Prompt = "You write.", Model = "anthropic/claude-3-5-sonnet", Tools = ["read", "grep"] },
		};

		var agentMap = ConfigJson(Build(subagents: subagents)).GetProperty("agent");

		var task = agentMap.GetProperty("orchestra-primary").GetProperty("permission").GetProperty("task");
		task.GetProperty("*").GetString().Should().Be("deny");
		task.GetProperty("orchestra-sub-data-researcher").GetString().Should().Be("allow");
		task.GetProperty("orchestra-sub-writer").GetString().Should().Be("allow");

		var researcher = agentMap.GetProperty("orchestra-sub-data-researcher");
		researcher.GetProperty("mode").GetString().Should().Be("subagent");
		researcher.GetProperty("description").GetString().Should().Be("Finds data");
		researcher.GetProperty("model").GetString().Should().Be("github-copilot/claude-opus-4.8");

		var writer = agentMap.GetProperty("orchestra-sub-writer");
		writer.GetProperty("model").GetString().Should().Be("anthropic/claude-3-5-sonnet");
		writer.GetProperty("tools").GetProperty("read").GetBoolean().Should().BeTrue();
	}

	[Fact]
	public void Build_LocalMcp_MapsToOpenCodeLocalEntry()
	{
		var mcp = new LocalMcp
		{
			Name = "filesystem",
			Type = McpType.Local,
			Command = "npx",
			Arguments = ["-y", "@modelcontextprotocol/server-filesystem", "/data"],
			Environment = new Dictionary<string, string> { ["API_KEY"] = "secret" },
			Timeout = TimeSpan.FromSeconds(30),
		};

		var entry = ConfigJson(Build(mcps: [mcp])).GetProperty("mcp").GetProperty("filesystem");
		entry.GetProperty("type").GetString().Should().Be("local");
		entry.GetProperty("command").EnumerateArray().Select(e => e.GetString())
			.Should().Equal("npx", "-y", "@modelcontextprotocol/server-filesystem", "/data");
		entry.GetProperty("environment").GetProperty("API_KEY").GetString().Should().Be("secret");
		entry.GetProperty("enabled").GetBoolean().Should().BeTrue();
		entry.GetProperty("timeout").GetInt64().Should().Be(30000);
	}

	[Fact]
	public void Build_RemoteMcp_MapsToOpenCodeRemoteEntry()
	{
		var mcp = new RemoteMcp
		{
			Name = "orchestra",
			Type = McpType.Remote,
			Endpoint = "https://host/mcp/data",
			Headers = new Dictionary<string, string> { ["Authorization"] = "Bearer x" },
		};

		var entry = ConfigJson(Build(mcps: [mcp])).GetProperty("mcp").GetProperty("orchestra");
		entry.GetProperty("type").GetString().Should().Be("remote");
		entry.GetProperty("url").GetString().Should().Be("https://host/mcp/data");
		entry.GetProperty("headers").GetProperty("Authorization").GetString().Should().Be("Bearer x");
	}

	[Fact]
	public void Build_McpOnly_HasConfigButNoPrimaryAgent()
	{
		var plan = Build(mcps: [new LocalMcp { Name = "fs", Type = McpType.Local, Command = "x", Arguments = [] }]);
		plan.HasConfig.Should().BeTrue();
		plan.PrimaryAgentName.Should().BeNull("MCPs don't require a custom agent");
	}

	[Theory]
	[InlineData("Data Researcher", "data-researcher")]
	[InlineData("  weird__name!! ", "weird-name")]
	[InlineData("***", "agent")]
	public void Slugify_NormalizesNames(string input, string expected)
		=> OpenCodeConfigBuilder.Slugify(input).Should().Be(expected);
}
