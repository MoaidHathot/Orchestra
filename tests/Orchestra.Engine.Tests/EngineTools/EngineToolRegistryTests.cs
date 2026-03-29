using FluentAssertions;

namespace Orchestra.Engine.Tests.EngineTools;

public class EngineToolRegistryTests
{
	[Fact]
	public void CreateDefault_ContainsSetStatusTool()
	{
		var registry = EngineToolRegistry.CreateDefault();

		registry.TryGet("orchestra_set_status", out var tool).Should().BeTrue();
		tool.Should().BeOfType<SetStatusTool>();
	}

	[Fact]
	public void CreateDefault_ContainsCompleteTool()
	{
		var registry = EngineToolRegistry.CreateDefault();

		registry.TryGet("orchestra_complete", out var tool).Should().BeTrue();
		tool.Should().BeOfType<CompleteTool>();
	}

	[Fact]
	public void CreateDefault_ContainsTwoTools()
	{
		var registry = EngineToolRegistry.CreateDefault();

		registry.Count.Should().Be(2);
	}

	[Fact]
	public void Register_AddsTool()
	{
		var registry = new EngineToolRegistry();
		var tool = new SetStatusTool();

		registry.Register(tool);

		registry.Count.Should().Be(1);
	}

	[Fact]
	public void Register_SameNameReplaces()
	{
		var registry = new EngineToolRegistry();
		var tool1 = new SetStatusTool();
		var tool2 = new SetStatusTool();

		registry.Register(tool1).Register(tool2);

		registry.Count.Should().Be(1);
	}

	[Fact]
	public void GetAll_ReturnsAllTools()
	{
		var registry = EngineToolRegistry.CreateDefault();

		var tools = registry.GetAll();

		tools.Should().HaveCount(2);
	}

	[Fact]
	public void TryGet_NotFound_ReturnsFalse()
	{
		var registry = new EngineToolRegistry();

		registry.TryGet("nonexistent", out _).Should().BeFalse();
	}

	[Fact]
	public void TryGet_CaseInsensitive()
	{
		var registry = EngineToolRegistry.CreateDefault();

		registry.TryGet("ORCHESTRA_SET_STATUS", out var tool).Should().BeTrue();
		tool.Should().NotBeNull();
	}

	[Fact]
	public void Register_Fluent_ReturnsSameInstance()
	{
		var registry = new EngineToolRegistry();

		var result = registry.Register(new SetStatusTool());

		result.Should().BeSameAs(registry);
	}
}
