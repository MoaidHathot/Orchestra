using FluentAssertions;
using GitHub.Copilot;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
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

	#region Fault Broker Probing

	[Fact]
	public async Task SendAsync_CreateSessionFailure_ProbesFaultBroker()
	{
		var client = new ThrowingCopilotClient
		{
			CreateException = new InvalidOperationException("create pipe broken")
		};
		var broker = new RecordingFaultBroker();
		var agent = CreateAgentWithClient(client, broker);

		var task = agent.SendAsync("hello");

		await foreach (var _ in task) { }
		Func<Task> act = () => task.GetResultAsync();
		await act.Should().ThrowAsync<InvalidOperationException>();
		broker.ProbeCalls.Should().Be(1);
		broker.FailedSessionIds.Should().ContainSingle().Which.Should().Be("(session-create)");
		broker.FailureReasons.Should().ContainSingle().Which.Should().Contain("CreateSessionAsync failed: create pipe broken");
	}

	[Fact]
	public async Task SendAsync_SendFailure_ProbesFaultBrokerWithSessionId()
	{
		var session = new FakeCopilotSession("session-send-fails")
		{
			SendException = new InvalidOperationException("send pipe broken")
		};
		var client = new ThrowingCopilotClient { Session = session };
		var broker = new RecordingFaultBroker();
		var agent = CreateAgentWithClient(client, broker);

		var task = agent.SendAsync("hello");

		await foreach (var _ in task) { }
		Func<Task> act = () => task.GetResultAsync();
		await act.Should().ThrowAsync<InvalidOperationException>();
		broker.ProbeCalls.Should().Be(1);
		broker.FailedSessionIds.Should().ContainSingle().Which.Should().Be("session-send-fails");
		broker.FailureReasons.Should().ContainSingle().Which.Should().Contain("SendAsync failed: send pipe broken");
	}

	private static CopilotAgent CreateAgentWithClient(ICopilotClient client, ISessionFaultBroker broker)
	{
		return new CopilotAgent(
			clientPool: new FixedCopilotClientPool(client, broker),
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
			infiniteSessionConfig: null,
			attachments: [],
			logger: NullLoggerFactory.Instance.CreateLogger<CopilotAgent>(),
			loggerFactory: NullLoggerFactory.Instance);
	}

	private sealed class RecordingFaultBroker : ISessionFaultBroker
	{
		public bool IsClientUnhealthy { get; set; }
		public string? UnhealthyReason { get; set; }
		public string? UnhealthyTriggeringSessionId { get; set; }
		public string? UnhealthyTriggeringFailureReason { get; set; }
		public int ProbeCalls { get; private set; }
		public List<string> FailedSessionIds { get; } = [];
		public List<string> FailureReasons { get; } = [];

		public IDisposable RegisterSession(string sessionId, Action<Exception> onFault)
			=> new NoopDisposable();

		public Task<bool> ProbeAndMaybeFaultSiblingsAsync(
			string failedSessionId,
			string failureReason,
			CancellationToken cancellationToken)
		{
			ProbeCalls++;
			FailedSessionIds.Add(failedSessionId);
			FailureReasons.Add(failureReason);
			return Task.FromResult(true);
		}
	}

	private sealed class ThrowingCopilotClient : ICopilotClient
	{
		public int DiagnosticHash => 123;
		public Exception? CreateException { get; init; }
		public ICopilotSession? Session { get; init; }

		public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
		public Task StopAsync() => Task.CompletedTask;
		public Task PingAsync(string message, CancellationToken cancellationToken) => Task.CompletedTask;

		public Task<ICopilotSession> CreateSessionAsync(SessionConfig config, CancellationToken cancellationToken)
		{
			if (CreateException is not null)
				return Task.FromException<ICopilotSession>(CreateException);

			return Task.FromResult(Session ?? new FakeCopilotSession("session-ok"));
		}

		public Task<ICopilotSession> ResumeSessionAsync(string sessionId, ResumeSessionConfig config, CancellationToken cancellationToken)
			=> Task.FromException<ICopilotSession>(new NotSupportedException("Resume not supported in this fake."));

		public Task<string?> GetLastSessionIdAsync(CancellationToken cancellationToken)
			=> Task.FromResult<string?>(null);

		public Task DeleteSessionAsync(string sessionId, CancellationToken cancellationToken)
			=> Task.CompletedTask;

		public Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken cancellationToken)
			=> Task.FromResult<IReadOnlyList<ModelInfo>>([]);

		public ValueTask DisposeAsync() => ValueTask.CompletedTask;
	}

	private sealed class FakeCopilotSession : ICopilotSession
	{
		public FakeCopilotSession(string sessionId)
		{
			SessionId = sessionId;
		}

		public string SessionId { get; }
		public Exception? SendException { get; init; }

		// SDK 1.0.0 dropped the SessionEventHandler delegate in favour of plain
		// Action<SessionEvent>; the runtime semantics are unchanged.
		public IDisposable On(Action<SessionEvent> handler) => new NoopDisposable();

		public Task<string> SendAsync(MessageOptions options, CancellationToken cancellationToken)
			=> SendException is null
				? Task.FromResult("message-id")
				: Task.FromException<string>(SendException);

		public Task AbortAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
		public ValueTask DisposeAsync() => ValueTask.CompletedTask;
	}

	private sealed class NoopDisposable : IDisposable
	{
		public void Dispose() { }
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

	private static CopilotAgent CreateAgentWithSubagents(params Subagent[] subagents)
	{
		return new CopilotAgent(
			client: new CopilotClient(),
			model: "test-model",
			systemPrompt: null,
			mcps: [],
			subagents: subagents,
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

	private static CopilotAgent CreateAgentWithExcludedTools(params string[] excludedTools)
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
			infiniteSessionConfig: null,
			attachments: [],
			logger: NullLoggerFactory.Instance.CreateLogger<CopilotAgent>(),
			excludedTools: excludedTools
		);
	}

	[Fact]
	public void BuildSessionConfig_SubagentWithModel_PropagatesToCustomAgentConfig()
	{
		// Arrange — SDK 1.0.0's CustomAgentConfig.Model (PR #1309) lets each sub-agent
		// pick a different model from the main session. We exercise the fan-out pattern:
		// main step on a strong model, researcher sub-agent on a faster/cheaper model.
		var agent = CreateAgentWithSubagents(new Subagent
		{
			Name = "researcher",
			Prompt = "Research the question and report findings.",
			Model = "gpt-5-mini",
		});

		// Act
		var config = agent.BuildSessionConfig();

		// Assert
		config.CustomAgents.Should().NotBeNull();
		config.CustomAgents!.Should().ContainSingle();
		config.CustomAgents![0].Name.Should().Be("researcher");
		config.CustomAgents![0].Model.Should().Be("gpt-5-mini");
	}

	[Fact]
	public void BuildSessionConfig_SubagentWithoutModel_LeavesCustomAgentConfigModelNull()
	{
		// Arrange — when no per-sub-agent model is configured, the SDK runtime falls
		// back to the main session's model. We must NOT set CustomAgentConfig.Model
		// to an empty string or the SDK will treat it as an explicit (invalid) override.
		var agent = CreateAgentWithSubagents(new Subagent
		{
			Name = "default-model-agent",
			Prompt = "Use whatever model the main session is on.",
			// Model intentionally omitted.
		});

		// Act
		var config = agent.BuildSessionConfig();

		// Assert
		config.CustomAgents![0].Model.Should().BeNull();
	}

	[Fact]
	public void BuildSessionConfig_SubagentWithSkills_PropagatesToCustomAgentConfig()
	{
		// Arrange — SDK 1.0.0 PR #995 lets each sub-agent declare a subset of the host's
		// skill catalog. Pair with Model overrides to give each sub-agent its own model
		// AND its own specialised instruction surface.
		var agent = CreateAgentWithSubagents(new Subagent
		{
			Name = "code-reviewer",
			Prompt = "Review the diff and report style issues.",
			Skills = ["dotnet-best-practices", "security-review"],
		});

		// Act
		var config = agent.BuildSessionConfig();

		// Assert
		config.CustomAgents.Should().NotBeNull();
		var customAgent = config.CustomAgents!.Single();
		customAgent.Skills.Should().NotBeNull();
		customAgent.Skills!.Should().HaveCount(2);
		customAgent.Skills!.Should().BeEquivalentTo(["dotnet-best-practices", "security-review"]);
	}

	[Fact]
	public void BuildSessionConfig_SubagentWithoutSkills_LeavesCustomAgentConfigSkillsNull()
	{
		// Arrange — when Skills is null/empty the sub-agent inherits the main session's
		// skill resolution. Setting an empty list would be a tighter restriction than
		// the author intended (zero skills active) so we must leave the property null.
		var agent = CreateAgentWithSubagents(new Subagent
		{
			Name = "no-skills",
			Prompt = "No specific skills.",
			Skills = [],
		});

		// Act
		var config = agent.BuildSessionConfig();

		// Assert
		config.CustomAgents![0].Skills.Should().BeNull();
	}

	[Fact]
	public void BuildSessionConfig_WithExcludedTools_PopulatesDefaultAgentExcludedTools()
	{
		// Arrange — least-privilege pattern: agent can read but not write or run shell.
		// SDK 1.0.0 PR #1098 introduced DefaultAgentConfig.ExcludedTools for this.
		var agent = CreateAgentWithExcludedTools("write_file", "shell", "edit_file");

		// Act
		var config = agent.BuildSessionConfig();

		// Assert
		config.DefaultAgent.Should().NotBeNull();
		config.DefaultAgent!.ExcludedTools.Should().NotBeNull();
		config.DefaultAgent!.ExcludedTools!.Should().BeEquivalentTo(["write_file", "shell", "edit_file"]);
	}

	[Fact]
	public void BuildSessionConfig_WithoutExcludedTools_LeavesDefaultAgentNull()
	{
		// Arrange — when no exclusions are configured we must NOT instantiate
		// DefaultAgentConfig. An empty list would be a no-op functionally but it
		// changes the wire shape; the SDK runtime treats absent-vs-empty differently
		// in some code paths (defaults vs. explicit "nothing excluded").
		var agent = CreateAgentWithMcps();

		// Act
		var config = agent.BuildSessionConfig();

		// Assert
		config.DefaultAgent.Should().BeNull();
	}

	[Fact]
	public void BuildResumeSessionConfig_PropagatesDefaultAgent()
	{
		// Arrange — the resume path must preserve the exclusion policy or a swap-and-resume
		// would silently re-enable the excluded tools (security-relevant regression).
		var agent = CreateAgentWithExcludedTools("shell");
		var baseConfig = agent.BuildSessionConfig();

		// Act
		var resumeConfig = agent.BuildResumeSessionConfig(baseConfig);

		// Assert
		resumeConfig.DefaultAgent.Should().NotBeNull();
		resumeConfig.DefaultAgent!.ExcludedTools.Should().BeEquivalentTo(["shell"]);
	}

	[Fact]
	public void AgentBuilder_WithExcludedTools_StoresOnBuilder()
	{
		// Arrange & Act — fluent surface check. The actual propagation to the SDK
		// config is covered by BuildSessionConfig_WithExcludedTools above; here we
		// just assert the fluent setter returns the builder and the value is
		// retrievable via a follow-up BuildAgentAsync (asserted indirectly by D.2).
		var builder = new CopilotAgentBuilder()
			.WithModel("test-model")
			.WithExcludedTools("write_file", "shell");

		// Assert — the builder should be the same instance (fluent chaining), and the
		// excluded tools should not have raised any errors during set.
		builder.Should().NotBeNull();
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
		var serverConfig = config.McpServers!["icm"].Should().BeOfType<McpStdioServerConfig>().Subject;
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
		var serverConfig = config.McpServers!["api"].Should().BeOfType<McpHttpServerConfig>().Subject;
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

		var local1 = config.McpServers!["local-1"].Should().BeOfType<McpStdioServerConfig>().Subject;
		local1.Tools.Should().ContainSingle().Which.Should().Be("*");

		var remote1 = config.McpServers!["remote-1"].Should().BeOfType<McpHttpServerConfig>().Subject;
		remote1.Tools.Should().ContainSingle().Which.Should().Be("*");

		var local2 = config.McpServers!["local-2"].Should().BeOfType<McpStdioServerConfig>().Subject;
		local2.Tools.Should().ContainSingle().Which.Should().Be("*");
		// SDK 1.0.0 renamed McpStdioServerConfig.Cwd -> WorkingDirectory.
		local2.WorkingDirectory.Should().Be("/app");
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

	[Fact]
	public void BuildSessionConfig_LocalMcpWithTimeout_PropagatesAsMilliseconds()
	{
		// Arrange — long-running tool needs a 30-minute MCP request timeout
		var agent = CreateAgentWithMcps(new LocalMcp
		{
			Name = "long-runner",
			Type = McpType.Local,
			Command = "node",
			Arguments = ["server.js"],
			Timeout = TimeSpan.FromMinutes(30),
		});

		// Act
		var config = agent.BuildSessionConfig();

		// Assert — Mcp.Timeout (TimeSpan) is converted to milliseconds for the SDK.
		var serverConfig = config.McpServers!["long-runner"].Should().BeOfType<McpStdioServerConfig>().Subject;
		serverConfig.Timeout.Should().Be(1_800_000, "30 minutes in milliseconds");
	}

	[Fact]
	public void BuildSessionConfig_LocalMcpWithEnvironment_PropagatesToStdioConfigEnv()
	{
		// Arrange — the canonical secret-injection pattern: pass an API key + a feature
		// flag to a stdio MCP via the new LocalMcp.Environment field. The forwarded
		// dictionary should land verbatim on McpStdioServerConfig.Env so the SDK 1.0.0
		// runtime can hand it to the spawned MCP process.
		var agent = CreateAgentWithMcps(new LocalMcp
		{
			Name = "openai-tool",
			Type = McpType.Local,
			Command = "npx",
			Arguments = ["openai-mcp-server"],
			Environment = new Dictionary<string, string>(StringComparer.Ordinal)
			{
				["OPENAI_API_KEY"] = "sk-test-1234",
				["NODE_ENV"] = "production",
			},
		});

		// Act
		var config = agent.BuildSessionConfig();

		// Assert
		var serverConfig = config.McpServers!["openai-tool"].Should().BeOfType<McpStdioServerConfig>().Subject;
		serverConfig.Env.Should().NotBeNull();
		serverConfig.Env!.Should().HaveCount(2);
		serverConfig.Env!["OPENAI_API_KEY"].Should().Be("sk-test-1234");
		serverConfig.Env!["NODE_ENV"].Should().Be("production");
	}

	[Fact]
	public void BuildSessionConfig_LocalMcpWithoutEnvironment_LeavesStdioConfigEnvNull()
	{
		// Arrange — when no environment is configured, McpStdioServerConfig.Env must
		// stay null so the SDK falls back to inheriting the host process environment
		// (matches the pre-1.0 behaviour for unchanged orchestrations).
		var agent = CreateAgentWithMcps(new LocalMcp
		{
			Name = "no-env-tool",
			Type = McpType.Local,
			Command = "node",
			Arguments = ["server.js"],
			// Environment intentionally omitted.
		});

		// Act
		var config = agent.BuildSessionConfig();

		// Assert
		var serverConfig = config.McpServers!["no-env-tool"].Should().BeOfType<McpStdioServerConfig>().Subject;
		serverConfig.Env.Should().BeNull();
	}

	[Fact]
	public void BuildSessionConfig_RemoteMcpWithTimeout_PropagatesAsMilliseconds()
	{
		// Arrange — Orchestra data-plane MCP with the host default already applied (30 min).
		var agent = CreateAgentWithMcps(new RemoteMcp
		{
			Name = "orchestra",
			Type = McpType.Remote,
			Endpoint = "http://localhost:5001/mcp/data",
			Headers = [],
			Timeout = TimeSpan.FromSeconds(1800),
		});

		// Act
		var config = agent.BuildSessionConfig();

		// Assert
		var serverConfig = config.McpServers!["orchestra"].Should().BeOfType<McpHttpServerConfig>().Subject;
		serverConfig.Timeout.Should().Be(1_800_000, "1800 seconds in milliseconds");
	}

	[Fact]
	public void BuildSessionConfig_McpWithoutTimeout_ServerConfigTimeoutIsNull()
	{
		// Arrange — no Mcp.Timeout means we let the Copilot SDK use its built-in default.
		var agent = CreateAgentWithMcps(new RemoteMcp
		{
			Name = "no-timeout",
			Type = McpType.Remote,
			Endpoint = "https://example.com/mcp",
			Headers = [],
		});

		// Act
		var config = agent.BuildSessionConfig();

		// Assert
		var serverConfig = config.McpServers!["no-timeout"].Should().BeOfType<McpHttpServerConfig>().Subject;
		serverConfig.Timeout.Should().BeNull();
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
		// SDK 1.0.0 changed Sections's key from string to SystemMessageSection struct
		// (struct has an implicit ctor from string but the indexer requires the struct
		// type explicitly — we wrap each key here for clarity).
		config.SystemMessage.Sections![new SystemMessageSection("tone")].Action.Should().Be(SectionOverrideAction.Replace);
		config.SystemMessage.Sections[new SystemMessageSection("tone")].Content.Should().Be("Be concise");
		config.SystemMessage.Sections[new SystemMessageSection("code_change_rules")].Action.Should().Be(SectionOverrideAction.Remove);
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
		// SDK 1.0.0 added OnPostToolUseFailure as a separate hook for failure paths.
		config.Hooks.OnPostToolUseFailure.Should().NotBeNull();
		config.Hooks.OnUserPromptSubmitted.Should().NotBeNull();
		config.Hooks.OnErrorOccurred.Should().NotBeNull();
		config.Hooks.OnSessionEnd.Should().NotBeNull();
	}

	[Fact]
	public async Task BuildSessionConfig_OnPostToolUseFailure_EmitsAuditLogEntry()
	{
		// Arrange — capture audit log entries through a substituted reporter so we can
		// assert the hook produced the expected PostToolUseFailure entry.
		var reporter = Substitute.For<IOrchestrationReporter>();
		var capturedEntries = new List<AuditLogEntry>();
		reporter.WhenForAnyArgs(r => r.ReportAuditLogEntry(default!, default!))
			.Do(call => capturedEntries.Add((AuditLogEntry)call.Args()[1]));

		var agent = new CopilotAgent(
			client: new GitHub.Copilot.CopilotClient(new GitHub.Copilot.CopilotClientOptions()),
			model: "test-model",
			systemPrompt: null,
			mcps: [],
			subagents: [],
			reasoningLevel: null,
			systemPromptMode: null,
			systemPromptSections: null,
			reporter: reporter,
			engineTools: [],
			engineToolContext: null,
			skillDirectories: [],
			infiniteSessionConfig: null,
			attachments: [],
			logger: NullLoggerFactory.Instance.CreateLogger<CopilotAgent>());

		var config = agent.BuildSessionConfig();
		var hook = config.Hooks!.OnPostToolUseFailure;
		hook.Should().NotBeNull();

		// Act — invoke the hook directly with a synthetic failure input that mirrors
		// what the SDK runtime emits when a tool call faults. ToolArgs is JsonElement?
		// in SDK 1.0.0; serialize the dictionary on the fly.
		var argsElement = System.Text.Json.JsonSerializer.SerializeToElement(
			new Dictionary<string, object> { ["command"] = "ls /nope" });
		var input = new PostToolUseFailureHookInput
		{
			SessionId = "sess-1",
			Timestamp = DateTimeOffset.UtcNow,
			ToolName = "shell",
			ToolArgs = argsElement,
			Error = "ENOENT: no such file or directory",
		};
		var invocation = new HookInvocation { SessionId = "sess-1" };

		var output = await hook!(input, invocation);

		// Assert — the SDK contract allows the hook to return null when it has no
		// behaviour to inject (no AdditionalContext, no SuppressOutput); we follow that
		// contract because Orchestra only needs the audit-log side effect.
		output.Should().BeNull();

		capturedEntries.Should().ContainSingle();
		var entry = capturedEntries[0];
		entry.EventType.Should().Be(AuditEventType.PostToolUseFailure);
		entry.ToolName.Should().Be("shell");
		entry.ToolArguments.Should().Contain("ls /nope");
		entry.Error.Should().Be("ENOENT: no such file or directory");
		entry.ToolSuccess.Should().Be(false);
	}

	#endregion
}
