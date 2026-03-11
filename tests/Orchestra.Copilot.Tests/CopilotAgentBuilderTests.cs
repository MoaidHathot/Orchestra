using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Orchestra.Engine;

namespace Orchestra.Copilot.Tests;

public class CopilotAgentBuilderTests
{
	#region BuildAgentAsync

	[Fact]
	public async Task BuildAgentAsync_WithNullModel_ThrowsArgumentException()
	{
		// Arrange
		var builder = new CopilotAgentBuilder();
		// Model not set

		// Act
		var act = () => builder.BuildAgentAsync();

		// Assert
		await act.Should().ThrowAsync<ArgumentException>()
			.WithParameterName("Model");
	}

	[Fact]
	public async Task BuildAgentAsync_WithEmptyModel_ThrowsArgumentException()
	{
		// Arrange
		var builder = new CopilotAgentBuilder();
		builder.WithModel("");

		// Act
		var act = () => builder.BuildAgentAsync();

		// Assert
		await act.Should().ThrowAsync<ArgumentException>()
			.WithParameterName("Model");
	}

	[Fact]
	public async Task BuildAgentAsync_WithWhitespaceModel_ThrowsArgumentException()
	{
		// Arrange
		var builder = new CopilotAgentBuilder();
		builder.WithModel("   ");

		// Act
		var act = () => builder.BuildAgentAsync();

		// Assert
		await act.Should().ThrowAsync<ArgumentException>()
			.WithParameterName("Model");
	}

	#endregion

	#region Constructor

	[Fact]
	public void Constructor_WithNullLoggerFactory_UsesNullLoggerFactory()
	{
		// Arrange & Act
		var builder = new CopilotAgentBuilder(null);

		// Assert - Should not throw, uses NullLoggerFactory internally
		builder.Should().NotBeNull();
	}

	[Fact]
	public void Constructor_WithLoggerFactory_DoesNotThrow()
	{
		// Arrange
		var loggerFactory = NullLoggerFactory.Instance;

		// Act
		var builder = new CopilotAgentBuilder(loggerFactory);

		// Assert
		builder.Should().NotBeNull();
	}

	#endregion

	#region DisposeAsync

	[Fact]
	public async Task DisposeAsync_CanBeCalledMultipleTimes()
	{
		// Arrange
		var builder = new CopilotAgentBuilder();

		// Act & Assert - Should not throw on multiple dispose calls
		await builder.DisposeAsync();
		await builder.DisposeAsync();
	}

	#endregion

	#region Fluent API

	[Fact]
	public void WithModel_ReturnsSameBuilder()
	{
		// Arrange
		var builder = new CopilotAgentBuilder();

		// Act
		var result = builder.WithModel("claude-opus-4.5");

		// Assert
		result.Should().BeSameAs(builder);
	}

	[Fact]
	public void WithSystemPrompt_ReturnsSameBuilder()
	{
		// Arrange
		var builder = new CopilotAgentBuilder();

		// Act
		var result = builder.WithSystemPrompt("You are a helpful assistant.");

		// Assert
		result.Should().BeSameAs(builder);
	}

	[Fact]
	public void WithMcp_ReturnsSameBuilder()
	{
		// Arrange
		var builder = new CopilotAgentBuilder();
		var mcp = new LocalMcp
		{
			Name = "test",
			Type = McpType.Local,
			Command = "node",
			Arguments = ["server.js"]
		};

		// Act
		var result = builder.WithMcp(mcp);

		// Assert
		result.Should().BeSameAs(builder);
	}

	[Fact]
	public void WithReasoningLevel_ReturnsSameBuilder()
	{
		// Arrange
		var builder = new CopilotAgentBuilder();

		// Act
		var result = builder.WithReasoningLevel(ReasoningLevel.High);

		// Assert
		result.Should().BeSameAs(builder);
	}

	[Fact]
	public void WithSystemPromptMode_ReturnsSameBuilder()
	{
		// Arrange
		var builder = new CopilotAgentBuilder();

		// Act
		var result = builder.WithSystemPromptMode(SystemPromptMode.Replace);

		// Assert
		result.Should().BeSameAs(builder);
	}

	[Fact]
	public void WithReporter_ReturnsSameBuilder()
	{
		// Arrange
		var builder = new CopilotAgentBuilder();
		var reporter = NullOrchestrationReporter.Instance;

		// Act
		var result = builder.WithReporter(reporter);

		// Assert
		result.Should().BeSameAs(builder);
	}

	[Fact]
	public void FluentApi_CanChainAllMethods()
	{
		// Arrange & Act
		var builder = new CopilotAgentBuilder()
			.WithModel("claude-opus-4.5")
			.WithSystemPrompt("Test prompt")
			.WithReasoningLevel(ReasoningLevel.Medium)
			.WithSystemPromptMode(SystemPromptMode.Append)
			.WithReporter(NullOrchestrationReporter.Instance);

		// Assert
		builder.Should().NotBeNull();
	}

	[Fact]
	public void WithSubagents_ReturnsSameBuilder()
	{
		// Arrange
		var builder = new CopilotAgentBuilder();
		var subagents = new[]
		{
			new Subagent
			{
				Name = "researcher",
				Prompt = "You are a researcher"
			}
		};

		// Act
		var result = builder.WithSubagents(subagents);

		// Assert
		result.Should().BeSameAs(builder);
	}

	[Fact]
	public void WithSubagents_WithEmptyArray_ReturnsSameBuilder()
	{
		// Arrange
		var builder = new CopilotAgentBuilder();

		// Act
		var result = builder.WithSubagents([]);

		// Assert
		result.Should().BeSameAs(builder);
	}

	[Fact]
	public void WithSubagents_CanChainWithOtherMethods()
	{
		// Arrange
		var subagents = new[]
		{
			new Subagent
			{
				Name = "writer",
				DisplayName = "Writer Agent",
				Description = "Writes content",
				Prompt = "You are a writer",
				Tools = ["write_file"],
				Infer = true
			}
		};

		// Act
		var builder = new CopilotAgentBuilder()
			.WithModel("claude-opus-4.5")
			.WithSystemPrompt("You are a coordinator")
			.WithSubagents(subagents)
			.WithReporter(NullOrchestrationReporter.Instance);

		// Assert
		builder.Should().NotBeNull();
	}

	#endregion
}
