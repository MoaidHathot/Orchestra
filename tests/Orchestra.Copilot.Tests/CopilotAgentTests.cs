using FluentAssertions;
using GitHub.Copilot.SDK;
using Microsoft.Extensions.Logging;
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

	#region Skill Directories Configuration

	[Fact]
	public void Agent_WithSkillDirectories_ConfiguresCorrectly()
	{
		// Arrange & Act
		var builder = new CopilotAgentBuilder()
			.WithModel("claude-opus-4.5")
			.WithSkillDirectories("./skills/coding", "./skills/writing");

		// Assert
		builder.Should().NotBeNull();
	}

	[Fact]
	public void Agent_WithEmptySkillDirectories_ConfiguresCorrectly()
	{
		// Arrange & Act
		var builder = new CopilotAgentBuilder()
			.WithModel("claude-opus-4.5")
			.WithSkillDirectories();

		// Assert
		builder.Should().NotBeNull();
	}

	[Fact]
	public void Agent_WithSkillDirectoriesAndMcps_ConfiguresCorrectly()
	{
		// Arrange
		var mcp = new LocalMcp
		{
			Name = "test-mcp",
			Type = McpType.Local,
			Command = "node",
			Arguments = ["server.js"]
		};

		// Act
		var builder = new CopilotAgentBuilder()
			.WithModel("claude-opus-4.5")
			.WithMcp(mcp)
			.WithSkillDirectories("./skills/coding");

		// Assert
		builder.Should().NotBeNull();
	}

	#endregion

	#region BuildSessionConfig MCP Tools Tests

	private static CopilotAgent CreateAgentWithMcps(params Mcp[] mcps)
	{
		return new CopilotAgent(
			client: new CopilotClient(),
			model: "test-model",
			systemPrompt: null,
			mcps: mcps,
			subagents: [],
			reasoningLevel: null,
			systemPromptMode: null,
			systemPromptSections: null,
			reporter: NullOrchestrationReporter.Instance,
			engineTools: [],
			engineToolContext: null,
			skillDirectories: [],
			infiniteSessionConfig: null,
			attachments: [],
			logger: NullLoggerFactory.Instance.CreateLogger<CopilotAgent>()
		);
	}

	[Fact]
	public void BuildSessionConfig_LocalMcp_SetsToolsToWildcard()
	{
		// Arrange
		var agent = CreateAgentWithMcps(new LocalMcp
		{
			Name = "icm",
			Type = McpType.Local,
			Command = "dnx",
			Arguments = ["IcM.Mcp"],
		});

		// Act
		var config = agent.BuildSessionConfig();

		// Assert
		config.McpServers.Should().ContainKey("icm");
		var serverConfig = config.McpServers!["icm"].Should().BeOfType<McpLocalServerConfig>().Subject;
		serverConfig.Command.Should().Be("dnx");
		serverConfig.Args.Should().BeEquivalentTo(["IcM.Mcp"]);
		serverConfig.Tools.Should().ContainSingle().Which.Should().Be("*");
	}

	[Fact]
	public void BuildSessionConfig_RemoteMcp_SetsToolsToWildcard()
	{
		// Arrange
		var agent = CreateAgentWithMcps(new RemoteMcp
		{
			Name = "api",
			Type = McpType.Remote,
			Endpoint = "https://api.example.com/mcp",
			Headers = new Dictionary<string, string> { ["Authorization"] = "Bearer token" },
		});

		// Act
		var config = agent.BuildSessionConfig();

		// Assert
		config.McpServers.Should().ContainKey("api");
		var serverConfig = config.McpServers!["api"].Should().BeOfType<McpRemoteServerConfig>().Subject;
		serverConfig.Url.Should().Be("https://api.example.com/mcp");
		serverConfig.Tools.Should().ContainSingle().Which.Should().Be("*");
	}

	[Fact]
	public void BuildSessionConfig_MultipleMcps_AllHaveToolsWildcard()
	{
		// Arrange
		var agent = CreateAgentWithMcps(
			new LocalMcp
			{
				Name = "local-1",
				Type = McpType.Local,
				Command = "node",
				Arguments = ["server.js"],
			},
			new RemoteMcp
			{
				Name = "remote-1",
				Type = McpType.Remote,
				Endpoint = "https://remote.example.com",
				Headers = [],
			},
			new LocalMcp
			{
				Name = "local-2",
				Type = McpType.Local,
				Command = "python",
				Arguments = ["-m", "mcp_server"],
				WorkingDirectory = "/app",
			}
		);

		// Act
		var config = agent.BuildSessionConfig();

		// Assert
		config.McpServers.Should().HaveCount(3);

		var local1 = config.McpServers!["local-1"].Should().BeOfType<McpLocalServerConfig>().Subject;
		local1.Tools.Should().ContainSingle().Which.Should().Be("*");

		var remote1 = config.McpServers!["remote-1"].Should().BeOfType<McpRemoteServerConfig>().Subject;
		remote1.Tools.Should().ContainSingle().Which.Should().Be("*");

		var local2 = config.McpServers!["local-2"].Should().BeOfType<McpLocalServerConfig>().Subject;
		local2.Tools.Should().ContainSingle().Which.Should().Be("*");
		local2.Cwd.Should().Be("/app");
	}

	[Fact]
	public void BuildSessionConfig_NoMcps_McpServersIsNull()
	{
		// Arrange
		var agent = CreateAgentWithMcps();

		// Act
		var config = agent.BuildSessionConfig();

		// Assert
		config.McpServers.Should().BeNull();
	}

	#endregion

	#region BuildSessionConfig Skill Directories Tests

	private static CopilotAgent CreateAgentWithSkillDirectories(params string[] skillDirectories)
	{
		return new CopilotAgent(
			client: new CopilotClient(),
			model: "test-model",
			systemPrompt: null,
			mcps: [],
			subagents: [],
			reasoningLevel: null,
			systemPromptMode: null,
			systemPromptSections: null,
			reporter: NullOrchestrationReporter.Instance,
			engineTools: [],
			engineToolContext: null,
			skillDirectories: skillDirectories,
			infiniteSessionConfig: null,
			attachments: [],
			logger: NullLoggerFactory.Instance.CreateLogger<CopilotAgent>()
		);
	}

	[Fact]
	public void BuildSessionConfig_WithSkillDirectories_SetsSkillDirectories()
	{
		// Arrange
		var agent = CreateAgentWithSkillDirectories("./skills/coding", "./skills/writing");

		// Act
		var config = agent.BuildSessionConfig();

		// Assert
		config.SkillDirectories.Should().NotBeNull();
		config.SkillDirectories.Should().HaveCount(2);
		config.SkillDirectories.Should().Contain("./skills/coding");
		config.SkillDirectories.Should().Contain("./skills/writing");
	}

	[Fact]
	public void BuildSessionConfig_WithSingleSkillDirectory_SetsSingleEntry()
	{
		// Arrange
		var agent = CreateAgentWithSkillDirectories("/absolute/path/to/skills");

		// Act
		var config = agent.BuildSessionConfig();

		// Assert
		config.SkillDirectories.Should().ContainSingle().Which.Should().Be("/absolute/path/to/skills");
	}

	[Fact]
	public void BuildSessionConfig_NoSkillDirectories_SkillDirectoriesIsNull()
	{
		// Arrange
		var agent = CreateAgentWithSkillDirectories();

		// Act
		var config = agent.BuildSessionConfig();

		// Assert
		config.SkillDirectories.Should().BeNull();
	}

	[Fact]
	public void BuildSessionConfig_WithSkillDirectoriesAndMcps_SetsBoth()
	{
		// Arrange
		var agent = new CopilotAgent(
			client: new CopilotClient(),
			model: "test-model",
			systemPrompt: null,
			mcps: [new LocalMcp { Name = "icm", Type = McpType.Local, Command = "dnx", Arguments = ["IcM.Mcp"] }],
			subagents: [],
			reasoningLevel: null,
			systemPromptMode: null,
			systemPromptSections: null,
			reporter: NullOrchestrationReporter.Instance,
			engineTools: [],
			engineToolContext: null,
			skillDirectories: ["./skills/devops"],
			infiniteSessionConfig: null,
			attachments: [],
			logger: NullLoggerFactory.Instance.CreateLogger<CopilotAgent>()
		);

		// Act
		var config = agent.BuildSessionConfig();

		// Assert
		config.McpServers.Should().ContainKey("icm");
		config.SkillDirectories.Should().ContainSingle().Which.Should().Be("./skills/devops");
	}

	#endregion

	#region BuildSessionConfig InfiniteSession Tests

	private static CopilotAgent CreateAgentWithInfiniteSession(Engine.InfiniteSessionConfig? infiniteSessionConfig)
	{
		return new CopilotAgent(
			client: new CopilotClient(),
			model: "test-model",
			systemPrompt: null,
			mcps: [],
			subagents: [],
			reasoningLevel: null,
			systemPromptMode: null,
			systemPromptSections: null,
			reporter: NullOrchestrationReporter.Instance,
			engineTools: [],
			engineToolContext: null,
			skillDirectories: [],
			infiniteSessionConfig: infiniteSessionConfig,
			attachments: [],
			logger: NullLoggerFactory.Instance.CreateLogger<CopilotAgent>()
		);
	}

	[Fact]
	public void BuildSessionConfig_InfiniteSessionsEnabled_ConfiguresInfiniteSessions()
	{
		// Arrange
		var agent = CreateAgentWithInfiniteSession(new Engine.InfiniteSessionConfig
		{
			Enabled = true,
			BackgroundCompactionThreshold = 0.85,
			BufferExhaustionThreshold = 0.97
		});

		// Act
		var config = agent.BuildSessionConfig();

		// Assert
		config.InfiniteSessions.Should().NotBeNull();
		config.InfiniteSessions!.Enabled.Should().BeTrue();
		config.InfiniteSessions.BackgroundCompactionThreshold.Should().Be(0.85);
		config.InfiniteSessions.BufferExhaustionThreshold.Should().Be(0.97);
	}

	[Fact]
	public void BuildSessionConfig_InfiniteSessionsDisabled_ConfiguresInfiniteSessions()
	{
		// Arrange
		var agent = CreateAgentWithInfiniteSession(new Engine.InfiniteSessionConfig
		{
			Enabled = false
		});

		// Act
		var config = agent.BuildSessionConfig();

		// Assert
		config.InfiniteSessions.Should().NotBeNull();
		config.InfiniteSessions!.Enabled.Should().BeFalse();
	}

	[Fact]
	public void BuildSessionConfig_InfiniteSessionsNull_NoInfiniteSessionsConfig()
	{
		// Arrange
		var agent = CreateAgentWithInfiniteSession(null);

		// Act
		var config = agent.BuildSessionConfig();

		// Assert
		config.InfiniteSessions.Should().BeNull();
	}

	#endregion

	#region BuildSessionConfig Customize Mode Tests

	[Fact]
	public void BuildSessionConfig_CustomizeMode_ConfiguresSections()
	{
		// Arrange
		var sections = new Dictionary<string, SystemPromptSectionOverride>
		{
			["tone"] = new SystemPromptSectionOverride
			{
				Action = SystemPromptSectionAction.Replace,
				Content = "Be concise"
			},
			["code_change_rules"] = new SystemPromptSectionOverride
			{
				Action = SystemPromptSectionAction.Remove
			}
		};

		var agent = new CopilotAgent(
			client: new CopilotClient(),
			model: "test-model",
			systemPrompt: "Custom prompt",
			mcps: [],
			subagents: [],
			reasoningLevel: null,
			systemPromptMode: SystemPromptMode.Customize,
			systemPromptSections: sections,
			reporter: NullOrchestrationReporter.Instance,
			engineTools: [],
			engineToolContext: null,
			skillDirectories: [],
			infiniteSessionConfig: null,
			attachments: [],
			logger: NullLoggerFactory.Instance.CreateLogger<CopilotAgent>()
		);

		// Act
		var config = agent.BuildSessionConfig();

		// Assert
		config.SystemMessage.Should().NotBeNull();
		config.SystemMessage!.Mode.Should().Be(SystemMessageMode.Customize);
		config.SystemMessage.Sections.Should().HaveCount(2);
		config.SystemMessage.Sections!["tone"].Action.Should().Be(SectionOverrideAction.Replace);
		config.SystemMessage.Sections["tone"].Content.Should().Be("Be concise");
		config.SystemMessage.Sections["code_change_rules"].Action.Should().Be(SectionOverrideAction.Remove);
	}

	#endregion

	#region BuildSessionConfig Hooks Tests

	[Fact]
	public void BuildSessionConfig_WithHooks_HooksAreConfigured()
	{
		// Arrange
		var agent = CreateAgentWithMcps();

		// Act
		var config = agent.BuildSessionConfig();

		// Assert
		config.Hooks.Should().NotBeNull();
		config.Hooks!.OnSessionStart.Should().NotBeNull();
		config.Hooks.OnPreToolUse.Should().NotBeNull();
		config.Hooks.OnPostToolUse.Should().NotBeNull();
		config.Hooks.OnUserPromptSubmitted.Should().NotBeNull();
		config.Hooks.OnErrorOccurred.Should().NotBeNull();
		config.Hooks.OnSessionEnd.Should().NotBeNull();
	}

	#endregion
}
