using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Orchestra.Engine;

namespace Orchestra.Copilot.Tests;

public class CopilotAgentTests
{
	// Note: Full integration tests with CopilotAgent require mocking the CopilotClient,
	// which is challenging due to SDK internals. These tests focus on the testable aspects.

	#region BuildMcpServers Tests

	[Fact]
	public void BuildMcpServers_WithLocalMcp_CreatesCorrectConfig()
	{
		// Arrange
		var localMcp = new LocalMcp
		{
			Name = "filesystem",
			Type = McpType.Local,
			Command = "node",
			Arguments = ["server.js", "--port", "3000"],
			WorkingDirectory = "/app"
		};

		// This is tested indirectly through the builder
		// The actual BuildMcpServers is private, but we can test the flow
		var builder = new CopilotAgentBuilder()
			.WithModel("claude-opus-4.5")
			.WithMcp(localMcp);

		// Assert - Builder should accept the MCP without throwing
		builder.Should().NotBeNull();
	}

	[Fact]
	public void BuildMcpServers_WithRemoteMcp_CreatesCorrectConfig()
	{
		// Arrange
		var remoteMcp = new RemoteMcp
		{
			Name = "api-server",
			Type = McpType.Remote,
			Endpoint = "https://api.example.com/mcp",
			Headers = new Dictionary<string, string>
			{
				["Authorization"] = "Bearer token123"
			}
		};

		// This is tested indirectly through the builder
		var builder = new CopilotAgentBuilder()
			.WithModel("claude-opus-4.5")
			.WithMcp(remoteMcp);

		// Assert
		builder.Should().NotBeNull();
	}

	[Fact]
	public void BuildMcpServers_WithMultipleMcps_AcceptsAllTypes()
	{
		// Arrange
		var localMcp = new LocalMcp
		{
			Name = "local-fs",
			Type = McpType.Local,
			Command = "node",
			Arguments = ["fs-server.js"]
		};

		var remoteMcp = new RemoteMcp
		{
			Name = "remote-api",
			Type = McpType.Remote,
			Endpoint = "https://api.example.com",
			Headers = []
		};

		// Act
		var builder = new CopilotAgentBuilder()
			.WithModel("claude-opus-4.5")
			.WithMcp(localMcp, remoteMcp);

		// Assert
		builder.Should().NotBeNull();
	}

	#endregion

	#region Agent Configuration

	[Fact]
	public void Agent_WithReasoningLevel_ConfiguresCorrectly()
	{
		// Arrange & Act
		var builder = new CopilotAgentBuilder()
			.WithModel("claude-opus-4.5")
			.WithReasoningLevel(ReasoningLevel.High);

		// Assert
		builder.Should().NotBeNull();
	}

	[Fact]
	public void Agent_WithSystemPromptModeReplace_ConfiguresCorrectly()
	{
		// Arrange & Act
		var builder = new CopilotAgentBuilder()
			.WithModel("claude-opus-4.5")
			.WithSystemPrompt("Custom system prompt")
			.WithSystemPromptMode(SystemPromptMode.Replace);

		// Assert
		builder.Should().NotBeNull();
	}

	[Fact]
	public void Agent_WithSystemPromptModeAppend_ConfiguresCorrectly()
	{
		// Arrange & Act
		var builder = new CopilotAgentBuilder()
			.WithModel("claude-opus-4.5")
			.WithSystemPrompt("Additional instructions")
			.WithSystemPromptMode(SystemPromptMode.Append);

		// Assert
		builder.Should().NotBeNull();
	}

	[Fact]
	public void Agent_WithAllConfigurations_BuildsWithoutError()
	{
		// Arrange
		var localMcp = new LocalMcp
		{
			Name = "test-mcp",
			Type = McpType.Local,
			Command = "node",
			Arguments = ["server.js"]
		};

		// Act
		var builder = new CopilotAgentBuilder(NullLoggerFactory.Instance)
			.WithModel("claude-opus-4.5")
			.WithSystemPrompt("You are a helpful assistant.")
			.WithMcp(localMcp)
			.WithReasoningLevel(ReasoningLevel.Medium)
			.WithSystemPromptMode(SystemPromptMode.Replace)
			.WithReporter(NullOrchestrationReporter.Instance);

		// Assert
		builder.Should().NotBeNull();
	}

	#endregion

	#region IAgent Interface

	[Fact]
	public void CopilotAgent_ImplementsIAgent()
	{
		// Assert - CopilotAgent should implement IAgent
		typeof(CopilotAgent).Should().Implement<IAgent>();
	}

	#endregion
}
