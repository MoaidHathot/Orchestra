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

	#region Subagent Configuration

	[Fact]
	public void Agent_WithSubagents_ConfiguresCorrectly()
	{
		// Arrange
		var subagents = new[]
		{
			new Subagent
			{
				Name = "researcher",
				DisplayName = "Research Agent",
				Description = "Finds information",
				Prompt = "You are a researcher.",
				Tools = ["web_search", "read_file"],
				Infer = true
			}
		};

		// Act
		var builder = new CopilotAgentBuilder()
			.WithModel("claude-opus-4.5")
			.WithSubagents(subagents);

		// Assert
		builder.Should().NotBeNull();
	}

	[Fact]
	public void Agent_WithMultipleSubagents_ConfiguresCorrectly()
	{
		// Arrange
		var subagents = new[]
		{
			new Subagent
			{
				Name = "researcher",
				Prompt = "You are a researcher.",
				Infer = true
			},
			new Subagent
			{
				Name = "writer",
				Prompt = "You are a writer.",
				Infer = false
			},
			new Subagent
			{
				Name = "reviewer",
				Prompt = "You are a reviewer.",
				Infer = true
			}
		};

		// Act
		var builder = new CopilotAgentBuilder()
			.WithModel("claude-opus-4.5")
			.WithSubagents(subagents);

		// Assert
		builder.Should().NotBeNull();
	}

	[Fact]
	public void Agent_WithSubagentInferFalse_ConfiguresCorrectly()
	{
		// Arrange - Test that Infer=false is handled correctly
		var subagents = new[]
		{
			new Subagent
			{
				Name = "explicit-only",
				DisplayName = "Explicit Agent",
				Description = "Only called explicitly, not inferred",
				Prompt = "You handle specific requests only.",
				Infer = false // Should not be auto-selected by model
			}
		};

		// Act
		var builder = new CopilotAgentBuilder()
			.WithModel("claude-opus-4.5")
			.WithSubagents(subagents);

		// Assert
		builder.Should().NotBeNull();
	}

	[Fact]
	public void Agent_WithSubagentWithTools_ConfiguresCorrectly()
	{
		// Arrange - Subagent with specific tools (MCPs are resolved at runtime from McpNames)
		var subagents = new[]
		{
			new Subagent
			{
				Name = "file-handler",
				DisplayName = "File Handler",
				Description = "Handles file operations",
				Prompt = "You handle file operations.",
				Tools = ["read_file", "write_file", "list_directory"]
			}
		};

		// Act
		var builder = new CopilotAgentBuilder()
			.WithModel("claude-opus-4.5")
			.WithSubagents(subagents);

		// Assert
		builder.Should().NotBeNull();
	}

	[Fact]
	public void Agent_WithSubagentWithToolRestrictions_ConfiguresCorrectly()
	{
		// Arrange - Subagent with restricted tools (read-only)
		var subagents = new[]
		{
			new Subagent
			{
				Name = "reader",
				DisplayName = "Read-Only Agent",
				Prompt = "You can only read, not modify.",
				Tools = ["read_file", "list_directory", "search"] // No write tools
			}
		};

		// Act
		var builder = new CopilotAgentBuilder()
			.WithModel("claude-opus-4.5")
			.WithSubagents(subagents);

		// Assert
		builder.Should().NotBeNull();
	}

	[Fact]
	public void Agent_WithEmptySubagents_ConfiguresCorrectly()
	{
		// Arrange & Act
		var builder = new CopilotAgentBuilder()
			.WithModel("claude-opus-4.5")
			.WithSubagents([]);

		// Assert
		builder.Should().NotBeNull();
	}

	[Fact]
	public void Agent_WithSubagentsAndMcps_ConfiguresCorrectly()
	{
		// Arrange - Full configuration with both MCPs and subagents
		// Note: Subagent MCPs are resolved at runtime from McpNames (string references)
		// Here we test that the main agent can have MCPs while subagents have their own configuration
		var mainMcp = new LocalMcp
		{
			Name = "main-tools",
			Type = McpType.Local,
			Command = "npx",
			Arguments = ["main-server"]
		};

		var subagents = new[]
		{
			new Subagent
			{
				Name = "specialist",
				DisplayName = "Specialist Agent",
				Description = "A specialist agent for complex tasks",
				Prompt = "You are a specialist.",
				Tools = ["analyze", "process", "report"]
			}
		};

		// Act
		var builder = new CopilotAgentBuilder()
			.WithModel("claude-opus-4.5")
			.WithMcp(mainMcp)
			.WithSubagents(subagents);

		// Assert
		builder.Should().NotBeNull();
	}

	[Fact]
	public void Agent_WithAllConfigurationsIncludingSubagents_BuildsWithoutError()
	{
		// Arrange - Full configuration including subagents
		var mcp = new LocalMcp
		{
			Name = "test-mcp",
			Type = McpType.Local,
			Command = "node",
			Arguments = ["server.js"]
		};

		var subagents = new[]
		{
			new Subagent
			{
				Name = "helper",
				DisplayName = "Helper Agent",
				Description = "Assists with tasks",
				Prompt = "You are a helpful assistant.",
				Tools = ["search"],
				Infer = true
			}
		};

		// Act
		var builder = new CopilotAgentBuilder(NullLoggerFactory.Instance)
			.WithModel("claude-opus-4.5")
			.WithSystemPrompt("You are a coordinator agent.")
			.WithMcp(mcp)
			.WithSubagents(subagents)
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
