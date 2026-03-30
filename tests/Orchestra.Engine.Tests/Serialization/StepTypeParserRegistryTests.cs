using System.Text.Json;
using FluentAssertions;
using NSubstitute;

namespace Orchestra.Engine.Tests.Serialization;

public class StepTypeParserRegistryTests
{
	private static readonly StepParseContext s_context = new(BaseDirectory: null);
	#region Register

	[Fact]
	public void Register_Parser_CanBeLookedUp()
	{
		// Arrange
		var registry = new StepTypeParserRegistry();
		var parser = Substitute.For<IStepTypeParser>();
		parser.TypeName.Returns("Http");

		// Act
		registry.Register(parser);

		// Assert
		registry.IsRegistered("Http").Should().BeTrue();
	}

	[Fact]
	public void Register_FluentChaining_ReturnsRegistry()
	{
		// Arrange
		var registry = new StepTypeParserRegistry();
		var parser1 = Substitute.For<IStepTypeParser>();
		parser1.TypeName.Returns("Http");
		var parser2 = Substitute.For<IStepTypeParser>();
		parser2.TypeName.Returns("Transform");

		// Act
		var result = registry.Register(parser1).Register(parser2);

		// Assert
		result.Should().BeSameAs(registry);
		registry.IsRegistered("Http").Should().BeTrue();
		registry.IsRegistered("Transform").Should().BeTrue();
	}

	[Fact]
	public void Register_SameType_OverwritesPrevious()
	{
		// Arrange
		var registry = new StepTypeParserRegistry();
		var json = JsonSerializer.Deserialize<JsonElement>("""{"name": "test", "type": "Http"}""");

		var firstParser = Substitute.For<IStepTypeParser>();
		firstParser.TypeName.Returns("Http");
		var firstStep = new HttpOrchestrationStep
		{
			Name = "first",
			Type = OrchestrationStepType.Http,
			Method = "GET",
			Url = "http://first.com",
			DependsOn = [],
			Parameters = []
		};
		firstParser.Parse(Arg.Any<JsonElement>(), Arg.Any<StepParseContext>()).Returns(firstStep);

		var secondParser = Substitute.For<IStepTypeParser>();
		secondParser.TypeName.Returns("Http");
		var secondStep = new HttpOrchestrationStep
		{
			Name = "second",
			Type = OrchestrationStepType.Http,
			Method = "POST",
			Url = "http://second.com",
			DependsOn = [],
			Parameters = []
		};
		secondParser.Parse(Arg.Any<JsonElement>(), Arg.Any<StepParseContext>()).Returns(secondStep);

		// Act
		registry.Register(firstParser);
		registry.Register(secondParser);
		var result = registry.TryParse("Http", json, s_context);

		// Assert
		result.Should().BeSameAs(secondStep);
		result!.Name.Should().Be("second");
	}

	#endregion

	#region TryParse

	[Fact]
	public void TryParse_RegisteredType_ReturnsStep()
	{
		// Arrange
		var registry = new StepTypeParserRegistry();
		var parser = Substitute.For<IStepTypeParser>();
		parser.TypeName.Returns("Http");

		var json = JsonSerializer.Deserialize<JsonElement>("""{"name": "test", "type": "Http"}""");
		var mockStep = new HttpOrchestrationStep
		{
			Name = "test",
			Type = OrchestrationStepType.Http,
			Method = "GET",
			Url = "http://example.com",
			DependsOn = [],
			Parameters = []
		};
		parser.Parse(Arg.Any<JsonElement>(), Arg.Any<StepParseContext>()).Returns(mockStep);

		registry.Register(parser);

		// Act
		var result = registry.TryParse("Http", json, s_context);

		// Assert
		result.Should().NotBeNull();
		result.Should().BeSameAs(mockStep);
		result!.Name.Should().Be("test");
		parser.Received(1).Parse(Arg.Any<JsonElement>(), Arg.Any<StepParseContext>());
	}

	[Fact]
	public void TryParse_UnregisteredType_ReturnsNull()
	{
		// Arrange
		var registry = new StepTypeParserRegistry();
		var json = JsonSerializer.Deserialize<JsonElement>("""{"name": "test", "type": "Unknown"}""");

		// Act
		var result = registry.TryParse("Unknown", json, s_context);

		// Assert
		result.Should().BeNull();
	}

	[Fact]
	public void TryParse_CaseInsensitive_MatchesType()
	{
		// Arrange
		var registry = new StepTypeParserRegistry();
		var parser = Substitute.For<IStepTypeParser>();
		parser.TypeName.Returns("Http");

		var json = JsonSerializer.Deserialize<JsonElement>("""{"name": "test", "type": "Http"}""");
		var mockStep = new HttpOrchestrationStep
		{
			Name = "test",
			Type = OrchestrationStepType.Http,
			Method = "GET",
			Url = "http://example.com",
			DependsOn = [],
			Parameters = []
		};
		parser.Parse(Arg.Any<JsonElement>(), Arg.Any<StepParseContext>()).Returns(mockStep);

		registry.Register(parser);

		// Act & Assert — lowercase
		var resultLower = registry.TryParse("http", json, s_context);
		resultLower.Should().NotBeNull();
		resultLower.Should().BeSameAs(mockStep);

		// Act & Assert — uppercase
		var resultUpper = registry.TryParse("HTTP", json, s_context);
		resultUpper.Should().NotBeNull();
		resultUpper.Should().BeSameAs(mockStep);

		// Act & Assert — mixed case
		var resultMixed = registry.TryParse("hTtP", json, s_context);
		resultMixed.Should().NotBeNull();
		resultMixed.Should().BeSameAs(mockStep);
	}

	#endregion

	#region IsRegistered

	[Fact]
	public void IsRegistered_RegisteredType_ReturnsTrue()
	{
		// Arrange
		var registry = new StepTypeParserRegistry();
		var parser = Substitute.For<IStepTypeParser>();
		parser.TypeName.Returns("Http");

		registry.Register(parser);

		// Act
		var result = registry.IsRegistered("Http");

		// Assert
		result.Should().BeTrue();
	}

	[Fact]
	public void IsRegistered_UnregisteredType_ReturnsFalse()
	{
		// Arrange
		var registry = new StepTypeParserRegistry();

		// Act
		var result = registry.IsRegistered("NonExistent");

		// Assert
		result.Should().BeFalse();
	}

	#endregion

	#region RegisteredTypes

	[Fact]
	public void RegisteredTypes_ReturnsAllTypes()
	{
		// Arrange
		var registry = new StepTypeParserRegistry();

		var httpParser = Substitute.For<IStepTypeParser>();
		httpParser.TypeName.Returns("Http");

		var transformParser = Substitute.For<IStepTypeParser>();
		transformParser.TypeName.Returns("Transform");

		var promptParser = Substitute.For<IStepTypeParser>();
		promptParser.TypeName.Returns("Prompt");

		registry.Register(httpParser)
			.Register(transformParser)
			.Register(promptParser);

		// Act
		var types = registry.RegisteredTypes;

		// Assert
		types.Should().HaveCount(3);
		types.Should().Contain("Http");
		types.Should().Contain("Transform");
		types.Should().Contain("Prompt");
	}

	[Fact]
	public void RegisteredTypes_EmptyRegistry_ReturnsEmpty()
	{
		// Arrange
		var registry = new StepTypeParserRegistry();

		// Act
		var types = registry.RegisteredTypes;

		// Assert
		types.Should().BeEmpty();
	}

	#endregion
}
