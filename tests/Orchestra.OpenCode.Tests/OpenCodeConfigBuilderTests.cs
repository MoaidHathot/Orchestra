using System.Text.Json;
using FluentAssertions;
using Orchestra.Engine;

namespace Orchestra.OpenCode.Tests;

public class OpenCodeConfigBuilderTests
{
	private static OpenCodeModelRef Model => OpenCodeModelRef.Parse("github-copilot/claude-opus-4.8", "github-copilot");

	private static JsonElement PatchJson(OpenCodeAgentPlan plan)
	{
		var json = JsonSerializer.Serialize(plan.ConfigPatch);
		return JsonDocument.Parse(json).RootElement.Clone();
	}

	[Fact]
	public void Build_NoReasoningNoSubagents_ReturnsNull()
	{
		OpenCodeConfigBuilder.Build(Model, "sys", reasoningLevel: null, subagents: [], "github-copilot")
			.Should().BeNull();
	}

	[Fact]
	public void Build_ReasoningOnly_DefinesPrimaryAgentWithReasoningEffort()
	{
		var plan = OpenCodeConfigBuilder.Build(Model, "you are helpful", ReasoningLevel.High, subagents: [], "github-copilot");

		plan.Should().NotBeNull();
		plan!.PrimaryAgentName.Should().Be("orchestra-primary");

		var agent = PatchJson(plan).GetProperty("agent").GetProperty("orchestra-primary");
		agent.GetProperty("mode").GetString().Should().Be("primary");
		agent.GetProperty("model").GetString().Should().Be("github-copilot/claude-opus-4.8");
		agent.GetProperty("prompt").GetString().Should().Be("you are helpful");
		agent.GetProperty("reasoningEffort").GetString().Should().Be("high");
		agent.TryGetProperty("permission", out _).Should().BeFalse("no sub-agents means no task permission gate");
	}

	[Fact]
	public void Build_Subagents_DefinesSubagentEntriesAndScopedTaskPermission()
	{
		var subagents = new[]
		{
			new Subagent { Name = "Data Researcher", Description = "Finds data", Prompt = "You research data." },
			new Subagent { Name = "Writer", Prompt = "You write.", Model = "anthropic/claude-3-5-sonnet", Tools = ["read", "grep"] },
		};

		var plan = OpenCodeConfigBuilder.Build(Model, "coordinator", reasoningLevel: null, subagents, "github-copilot");
		plan.Should().NotBeNull();

		var agentMap = PatchJson(plan!).GetProperty("agent");

		// Primary agent gates delegation to exactly these sub-agents.
		var task = agentMap.GetProperty("orchestra-primary").GetProperty("permission").GetProperty("task");
		task.GetProperty("*").GetString().Should().Be("deny");
		task.GetProperty("orchestra-sub-data-researcher").GetString().Should().Be("allow");
		task.GetProperty("orchestra-sub-writer").GetString().Should().Be("allow");

		var researcher = agentMap.GetProperty("orchestra-sub-data-researcher");
		researcher.GetProperty("mode").GetString().Should().Be("subagent");
		researcher.GetProperty("description").GetString().Should().Be("Finds data");
		researcher.GetProperty("model").GetString().Should().Be("github-copilot/claude-opus-4.8", because: "no explicit model inherits the main model");

		var writer = agentMap.GetProperty("orchestra-sub-writer");
		writer.GetProperty("model").GetString().Should().Be("anthropic/claude-3-5-sonnet");
		writer.GetProperty("tools").GetProperty("read").GetBoolean().Should().BeTrue();
		writer.GetProperty("tools").GetProperty("grep").GetBoolean().Should().BeTrue();
	}

	[Fact]
	public void Build_SubagentWithoutDescription_FallsBackToDisplayNameOrName()
	{
		var plan = OpenCodeConfigBuilder.Build(Model, null, null,
			[new Subagent { Name = "helper", DisplayName = "Helper Bot", Prompt = "help" }], "github-copilot");

		PatchJson(plan!).GetProperty("agent").GetProperty("orchestra-sub-helper")
			.GetProperty("description").GetString().Should().Be("Helper Bot");
	}

	[Theory]
	[InlineData("Data Researcher", "data-researcher")]
	[InlineData("  weird__name!! ", "weird-name")]
	[InlineData("ALLCAPS", "allcaps")]
	[InlineData("***", "agent")]
	public void Slugify_NormalizesNames(string input, string expected)
	{
		OpenCodeConfigBuilder.Slugify(input).Should().Be(expected);
	}
}
