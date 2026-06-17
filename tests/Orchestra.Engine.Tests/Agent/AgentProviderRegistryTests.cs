using FluentAssertions;
using Orchestra.Engine.Tests.TestHelpers;

namespace Orchestra.Engine.Tests.Agent;

public class AgentProviderRegistryTests
{
	[Fact]
	public void Resolve_NullOrEmpty_ReturnsDefaultProvider()
	{
		var copilot = new MockAgentBuilder();
		var opencode = new MockAgentBuilder();
		var registry = new AgentProviderRegistry(
			new Dictionary<string, AgentBuilder> { ["copilot"] = copilot, ["opencode"] = opencode },
			defaultProviderName: "copilot");

		registry.Resolve(null).Should().BeSameAs(copilot);
		registry.Resolve("").Should().BeSameAs(copilot);
		registry.Resolve("   ").Should().BeSameAs(copilot);
		registry.DefaultProviderName.Should().Be("copilot");
	}

	[Fact]
	public void Resolve_KnownName_IsCaseInsensitive()
	{
		var copilot = new MockAgentBuilder();
		var opencode = new MockAgentBuilder();
		var registry = new AgentProviderRegistry(
			new Dictionary<string, AgentBuilder> { ["copilot"] = copilot, ["opencode"] = opencode },
			defaultProviderName: "copilot");

		registry.Resolve("opencode").Should().BeSameAs(opencode);
		registry.Resolve("OpenCode").Should().BeSameAs(opencode);
		registry.Resolve("  OPENCODE ").Should().BeSameAs(opencode);
	}

	[Fact]
	public void Resolve_UnknownName_Throws()
	{
		var registry = new AgentProviderRegistry(
			new Dictionary<string, AgentBuilder> { ["copilot"] = new MockAgentBuilder() },
			defaultProviderName: "copilot");

		var act = () => registry.Resolve("does-not-exist");

		act.Should().Throw<InvalidOperationException>()
			.WithMessage("*Unknown agent provider 'does-not-exist'*copilot*");
	}

	[Fact]
	public void Constructor_DefaultNotInMap_Throws()
	{
		var act = () => new AgentProviderRegistry(
			new Dictionary<string, AgentBuilder> { ["copilot"] = new MockAgentBuilder() },
			defaultProviderName: "opencode");

		act.Should().Throw<ArgumentException>().WithMessage("*opencode*not among*");
	}

	[Fact]
	public void Constructor_EmptyMap_Throws()
	{
		var act = () => new AgentProviderRegistry(
			new Dictionary<string, AgentBuilder>(),
			defaultProviderName: "copilot");

		act.Should().Throw<ArgumentException>();
	}

	[Fact]
	public void Builders_AreReferenceDistinct()
	{
		var shared = new MockAgentBuilder();
		var registry = new AgentProviderRegistry(
			new Dictionary<string, AgentBuilder> { ["copilot"] = shared, ["alias"] = shared },
			defaultProviderName: "copilot");

		registry.Builders.Should().HaveCount(1);
		registry.ProviderNames.Should().BeEquivalentTo(["copilot", "alias"]);
	}

	[Fact]
	public void Single_ResolvesAnyNameToTheOneBuilder()
	{
		var only = new MockAgentBuilder();
		var registry = new SingleAgentProviderRegistry(only, "copilot");

		registry.Resolve(null).Should().BeSameAs(only);
		registry.Resolve("opencode").Should().BeSameAs(only);
		registry.Resolve("literally-anything").Should().BeSameAs(only);
		registry.Builders.Should().ContainSingle().Which.Should().BeSameAs(only);
		registry.DefaultProviderName.Should().Be("copilot");
	}
}
