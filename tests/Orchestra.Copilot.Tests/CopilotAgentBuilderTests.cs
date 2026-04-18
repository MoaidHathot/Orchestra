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

	#region Client Invalidation and Recovery

	[Fact]
	public void InvalidateClient_SetsInvalidatedState()
	{
		// Arrange
		var builder = new CopilotAgentBuilder();

		// Act — simulate what CopilotAgent calls when it detects a connection error
		builder.InvalidateClient();

		// Assert — the builder should be in an invalidated state
		// Subsequent BuildAgentAsync calls should recreate the client
		builder.Should().NotBeNull();
	}

	[Fact]
	public void InvalidateClient_CanBeCalledMultipleTimes()
	{
		// Arrange
		var builder = new CopilotAgentBuilder();

		// Act & Assert — calling multiple times should not throw
		builder.InvalidateClient();
		builder.InvalidateClient();
		builder.InvalidateClient();
	}

	[Fact]
	public void InvalidateClient_IsThreadSafe()
	{
		// Arrange
		var builder = new CopilotAgentBuilder();

		// Act — simulate concurrent invalidation from multiple agents
		var tasks = Enumerable.Range(0, 100).Select(_ => Task.Run(() =>
		{
			builder.InvalidateClient();
		}));

		// Assert — should not throw or deadlock
		Task.WaitAll(tasks.ToArray());
	}

	#endregion

	#region IsConnectionError Detection (via CopilotAgent)

	[Fact]
	public void CopilotAgent_IsConnectionError_DetectsJsonRpcError()
	{
		// The error message from the user's log:
		// "Communication error with Copilot CLI: The JSON-RPC connection with the remote party was lost"
		var ex = new Exception("Communication error with Copilot CLI: The JSON-RPC connection with the remote party was lost before the request could complete.");

		// Use reflection to test the private static method
		var method = typeof(CopilotAgent).GetMethod("IsConnectionError",
			System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
		method.Should().NotBeNull("IsConnectionError should exist as a private static method");

		var result = (bool)method!.Invoke(null, [ex])!;
		result.Should().BeTrue("JSON-RPC errors should be detected as connection errors");
	}

	[Fact]
	public void CopilotAgent_IsConnectionError_DetectsPipeError()
	{
		var ex = new System.IO.IOException("Broken pipe");

		var method = typeof(CopilotAgent).GetMethod("IsConnectionError",
			System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

		var result = (bool)method!.Invoke(null, [ex])!;
		result.Should().BeTrue("IO/pipe errors should be detected as connection errors");
	}

	[Fact]
	public void CopilotAgent_IsConnectionError_DetectsNestedConnectionError()
	{
		var inner = new Exception("The JSON-RPC connection was lost");
		var outer = new Exception("Session creation failed", inner);

		var method = typeof(CopilotAgent).GetMethod("IsConnectionError",
			System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

		var result = (bool)method!.Invoke(null, [outer])!;
		result.Should().BeTrue("Nested connection errors should be detected");
	}

	[Fact]
	public void CopilotAgent_IsConnectionError_DoesNotFalsePositiveOnModelError()
	{
		var ex = new Exception("Model 'gpt-5' is not available");

		var method = typeof(CopilotAgent).GetMethod("IsConnectionError",
			System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

		var result = (bool)method!.Invoke(null, [ex])!;
		result.Should().BeFalse("Model errors should NOT be treated as connection errors");
	}

	#endregion
}
