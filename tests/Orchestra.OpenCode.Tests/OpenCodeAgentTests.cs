using System.Runtime.CompilerServices;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Orchestra.Engine;

namespace Orchestra.OpenCode.Tests;

/// <summary>
/// End-to-end-ish tests for <see cref="OpenCodeAgent"/> driven through the real builder/pool
/// against a fake <see cref="IOpenCodeClient"/> (no real <c>opencode serve</c>). Validates the
/// create-session → subscribe → prompt → translate → result flow, model mapping, and permission
/// replies. The pool is put in connect mode (ServerUrl set) so nothing is spawned, and the
/// engine-tool bridge is disabled so no Kestrel host starts.
/// </summary>
public class OpenCodeAgentTests
{
	private const string Sid = "ses_1";

	private static OpenCodeAgentPoolOptions ConnectOptions() => new()
	{
		ServerUrl = "http://fake-opencode",
		EngineToolBridgeEnabled = false,
		DefaultMinInstances = 0,
		FallbackProvider = "github-copilot",
	};

	private static async Task<(AgentResult Result, List<AgentEvent> Events)> RunAsync(
		FakeOpenCodeClient client, AgentBuildConfig config)
	{
		var builder = new OpenCodeAgentBuilder(NullLoggerFactory.Instance, ConnectOptions(), new FakeFactory(client));
		await using var scope = await builder.CreateRunScopeAsync();
		var agent = await builder.BuildAgentAsync(config);

		var task = agent.SendAsync("do the thing", CancellationToken.None);
		var events = new List<AgentEvent>();
		await foreach (var e in task)
			events.Add(e);
		var result = await task.GetResultAsync();
		return (result, events);
	}

	[Fact]
	public void GetCapabilities_DeclaresSupportedAndUnsupportedFeatures()
	{
		var builder = new OpenCodeAgentBuilder(NullLoggerFactory.Instance, ConnectOptions(), new FakeFactory(new FakeOpenCodeClient(Sid)));

		var caps = builder.GetCapabilities();

		caps.Provider.Should().Be("opencode");
		// Honored by the OpenCode adapter.
		caps.Mcps.Should().BeTrue();
		caps.Subagents.Should().BeTrue();
		caps.ReasoningLevel.Should().BeTrue();
		caps.WorkingDirectory.Should().BeTrue();
		caps.SkillDirectories.Should().BeTrue();
		caps.EngineTools.Should().BeTrue();
		caps.Attachments.Should().BeTrue();
		caps.HumanInput.Should().BeTrue();
		caps.PermissionPolicy.Should().BeTrue();
		caps.ExcludedTools.Should().BeTrue();
		caps.InfiniteSession.Should().BeTrue();
		// Not yet supported — must be declared false so steps using them get a warning/error.
		caps.SandboxPolicy.Should().BeFalse();
		caps.SystemPromptMode.Should().BeFalse();
		caps.SystemPromptSections.Should().BeFalse();
		caps.ReasoningSummary.Should().BeFalse();
		caps.ContextTier.Should().BeFalse();
		caps.GitHubToken.Should().BeFalse();
	}

	[Fact]
	public async Task SendAsync_StreamsDeltas_AndReturnsFinalContentAndUsage()
	{
		var client = new FakeOpenCodeClient(Sid)
		{
			Events =
			[
				TestEvents.Event("message.part.updated", $$"""{ "part": { "type": "text", "id": "p1", "sessionID": "{{Sid}}", "text": "Hello" } }"""),
				TestEvents.Event("message.part.updated", $$"""{ "part": { "type": "text", "id": "p1", "sessionID": "{{Sid}}", "text": "Hello world" } }"""),
				TestEvents.Event("message.updated", $$"""{ "info": { "role": "assistant", "sessionID": "{{Sid}}", "providerID": "github-copilot", "modelID": "claude-opus-4.8", "cost": 0.01, "tokens": { "input": 10, "output": 5 } } }"""),
				TestEvents.Event("session.idle", $$"""{ "sessionID": "{{Sid}}" }"""),
			],
		};

		var (result, events) = await RunAsync(client, new AgentBuildConfig { Model = "github-copilot/claude-opus-4.8" });

		result.Content.Should().Be("Hello world");
		result.ActualModel.Should().Be("github-copilot/claude-opus-4.8");
		result.SelectedModel.Should().Be("github-copilot/claude-opus-4.8");
		result.Usage!.InputTokens.Should().Be(10);
		result.Usage.OutputTokens.Should().Be(5);

		events.Should().Contain(e => e.Type == AgentEventType.SessionStart);
		events.Where(e => e.Type == AgentEventType.MessageDelta).Select(e => e.Content)
			.Should().Equal("Hello", " world");

		// The prompt was sent with the split provider/model and the system prompt.
		client.LastPrompt.Should().NotBeNull();
		client.LastPrompt!.Model.ProviderId.Should().Be("github-copilot");
		client.LastPrompt.Model.ModelId.Should().Be("claude-opus-4.8");
		client.SessionDeleted.Should().BeTrue();
	}

	[Fact]
	public async Task SendAsync_BareModel_UsesFallbackProvider()
	{
		var client = new FakeOpenCodeClient(Sid)
		{
			Events = [TestEvents.Event("session.idle", $$"""{ "sessionID": "{{Sid}}" }""")],
		};

		await RunAsync(client, new AgentBuildConfig { Model = "claude-opus-4.8" });

		client.LastPrompt!.Model.ProviderId.Should().Be("github-copilot");
		client.LastPrompt.Model.ModelId.Should().Be("claude-opus-4.8");
	}

	[Fact]
	public async Task SendAsync_SessionError_FaultsResult()
	{
		var client = new FakeOpenCodeClient(Sid)
		{
			Events = [TestEvents.Event("session.error", $$"""{ "sessionID": "{{Sid}}", "error": { "message": "kaboom" } }""")],
		};

		var builder = new OpenCodeAgentBuilder(NullLoggerFactory.Instance, ConnectOptions(), new FakeFactory(client));
		await using var scope = await builder.CreateRunScopeAsync();
		var agent = await builder.BuildAgentAsync(new AgentBuildConfig { Model = "github-copilot/claude-opus-4.8" });

		var task = agent.SendAsync("go", CancellationToken.None);
		await foreach (var _ in task) { }
		var act = async () => await task.GetResultAsync();

		await act.Should().ThrowAsync<OpenCodeSessionFailedException>().WithMessage("*kaboom*");
	}

	[Fact]
	public async Task SendAsync_DenyListPermission_RejectsMatchingRequest()
	{
		var client = new FakeOpenCodeClient(Sid)
		{
			Events =
			[
				TestEvents.Event("permission.updated", $$"""{ "sessionID": "{{Sid}}", "id": "perm1", "type": "bash", "title": "rm -rf /tmp" }"""),
				TestEvents.Event("session.idle", $$"""{ "sessionID": "{{Sid}}" }"""),
			],
		};

		var config = new AgentBuildConfig
		{
			Model = "github-copilot/claude-opus-4.8",
			PermissionPolicy = new PermissionPolicy { Mode = PermissionMode.DenyList, Deny = ["bash"] },
		};
		await RunAsync(client, config);

		client.PermissionResponses.Should().ContainSingle();
		client.PermissionResponses[0].Should().Be(("perm1", "reject"));
	}

	[Fact]
	public void IsDeniedByPolicy_MatchesKindAndGlobTarget()
	{
		OpenCodeAgent.IsDeniedByPolicy("bash", "rm -rf /", ["bash"]).Should().BeTrue();
		OpenCodeAgent.IsDeniedByPolicy("write", "/etc/passwd", ["*/passwd"]).Should().BeTrue();
		OpenCodeAgent.IsDeniedByPolicy("read", "/home/x", ["bash", "write"]).Should().BeFalse();
	}

	[Fact]
	public async Task SendAsync_Reasoning_RoutesThroughDedicatedAgent()
	{
		// A reasoning step runs on a dedicated server configured with a per-step agent; the prompt
		// references that agent and the system prompt moves into the agent config (not the body).
		var client = new FakeOpenCodeClient(Sid)
		{
			Events = [TestEvents.Event("session.idle", $$"""{ "sessionID": "{{Sid}}" }""")],
		};

		await RunAsync(client, new AgentBuildConfig
		{
			Model = "github-copilot/claude-opus-4.8",
			SystemPrompt = "be terse",
			ReasoningLevel = ReasoningLevel.High,
		});

		client.LastPrompt!.Agent.Should().Be("orchestra-primary");
		client.LastPrompt.System.Should().BeNull();
	}

	[Fact]
	public async Task SendAsync_NoReasoningNoSubagents_SendsSystemInlineWithoutAgent()
	{
		var client = new FakeOpenCodeClient(Sid)
		{
			Events = [TestEvents.Event("session.idle", $$"""{ "sessionID": "{{Sid}}" }""")],
		};

		await RunAsync(client, new AgentBuildConfig { Model = "github-copilot/claude-opus-4.8", SystemPrompt = "plain" });

		client.LastPrompt!.Agent.Should().BeNull();
		client.LastPrompt.System.Should().Be("plain");
	}

	[Fact]
	public async Task SendAsync_DeclaredMcpMissingFromServer_EmitsFailedMcpStatus()
	{
		// "present-mcp" loaded on the server, "missing-mcp" not — the latter must be reported
		// Failed so the engine's post-turn check fails the step instead of running toolless.
		var client = new FakeOpenCodeClient(Sid)
		{
			McpNames = ["present-mcp"],
			Events = [TestEvents.Event("session.idle", $$"""{ "sessionID": "{{Sid}}" }""")],
		};

		var config = new AgentBuildConfig
		{
			Model = "github-copilot/claude-opus-4.8",
			Mcps =
			[
				new LocalMcp { Name = "present-mcp", Type = McpType.Local, Command = "x", Arguments = [] },
				new LocalMcp { Name = "missing-mcp", Type = McpType.Local, Command = "y", Arguments = [] },
			],
		};

		var (_, events) = await RunAsync(client, config);

		var loaded = events.Should().ContainSingle(e => e.Type == AgentEventType.McpServersLoaded).Subject;
		loaded.McpServerStatuses!.Single(s => s.Name == "present-mcp").Status.Should().Be("Connected");
		loaded.McpServerStatuses!.Single(s => s.Name == "missing-mcp").Status.Should().Be("Failed");
	}

	[Fact]
	public async Task SendAsync_NoDeclaredMcps_DoesNotEmitMcpServersLoaded()
	{
		var client = new FakeOpenCodeClient(Sid)
		{
			Events = [TestEvents.Event("session.idle", $$"""{ "sessionID": "{{Sid}}" }""")],
		};

		var (_, events) = await RunAsync(client, new AgentBuildConfig { Model = "github-copilot/claude-opus-4.8", SystemPrompt = "plain" });

		events.Should().NotContain(e => e.Type == AgentEventType.McpServersLoaded);
	}

	private sealed class FakeFactory(IOpenCodeClient client) : IOpenCodeClientFactory
	{
		public IOpenCodeClient Create(string baseUrl, string? username, string? password) => client;
	}

	[Fact]
	public async Task SendAsync_TransportLost_Swaps_ResumesPriorSession_AndSucceeds()
	{
		// First attempt: the event stream dies → OpenCodeClientUnhealthyException (transport-class).
		// The shared swap loop resumes the prior session (budget defaults to 1, resume on by
		// default) and the second attempt — a clean session.idle — succeeds without creating a new
		// session.
		var client = new SwapScriptedClient(Sid);
		var builder = new OpenCodeAgentBuilder(NullLoggerFactory.Instance, ConnectOptions(), new FakeFactory(client));
		await using var scope = await builder.CreateRunScopeAsync();
		var agent = await builder.BuildAgentAsync(new AgentBuildConfig { Model = "github-copilot/claude-opus-4.8" });

		var task = agent.SendAsync("go", CancellationToken.None);
		var events = new List<AgentEvent>();
		await foreach (var e in task)
		{
			events.Add(e);
		}

		var result = await task.GetResultAsync();

		result.Should().NotBeNull();
		client.CreateCount.Should().Be(1, "a resume reuses the prior session instead of creating a new one");
		client.PromptSessionIds.Should().OnlyContain(id => id == Sid);

		var swaps = events.Where(e => e.Type == AgentEventType.CliInstanceSwapped).ToList();
		swaps.Should().ContainSingle();
		swaps[0].SwapReason.Should().Be("transport_lost");
		swaps[0].SwapMode.Should().Be("resume");
		swaps[0].SwapAttempt.Should().Be(1);
	}

	[Fact]
	public async Task SendAsync_TransportLost_WithResumeDisabled_ColdRestarts_AndSucceeds()
	{
		var client = new SwapScriptedClient(Sid);
		var options = ConnectOptions();
		options.ResumeOnSwapEnabled = false;
		var builder = new OpenCodeAgentBuilder(NullLoggerFactory.Instance, options, new FakeFactory(client));
		await using var scope = await builder.CreateRunScopeAsync();
		var agent = await builder.BuildAgentAsync(new AgentBuildConfig { Model = "github-copilot/claude-opus-4.8" });

		var task = agent.SendAsync("go", CancellationToken.None);
		var events = new List<AgentEvent>();
		await foreach (var e in task)
		{
			events.Add(e);
		}

		var result = await task.GetResultAsync();

		result.Should().NotBeNull();
		client.CreateCount.Should().Be(2, "with resume disabled, the swap cold-restarts on a fresh session");
		events.Should().ContainSingle(e => e.Type == AgentEventType.CliInstanceSwapped)
			.Which.SwapMode.Should().Be("cold_restart");
	}

	[Fact]
	public async Task SendAsync_TransportLost_WithZeroSwapBudget_DoesNotSwap_AndFails()
	{
		var client = new SwapScriptedClient(Sid);
		var options = ConnectOptions();
		options.SwapBudgetPerStep = 0;
		var builder = new OpenCodeAgentBuilder(NullLoggerFactory.Instance, options, new FakeFactory(client));
		await using var scope = await builder.CreateRunScopeAsync();
		var agent = await builder.BuildAgentAsync(new AgentBuildConfig { Model = "github-copilot/claude-opus-4.8" });

		var task = agent.SendAsync("go", CancellationToken.None);
		await foreach (var _ in task)
		{
		}

		var act = async () => await task.GetResultAsync();

		await act.Should().ThrowAsync<OpenCodeClientUnhealthyException>();
		client.CreateCount.Should().Be(1, "a zero budget must not retry");
	}

	private sealed class FakeOpenCodeClient(string sessionId) : IOpenCodeClient
	{
		public string BaseUrl => "http://fake-opencode";
		public List<OpenCodeServerEvent> Events { get; init; } = [];
		public List<string> McpNames { get; init; } = ["build", "general"];
		public OpenCodePromptRequest? LastPrompt { get; private set; }
		public List<(string PermissionId, string Response)> PermissionResponses { get; } = [];
		public bool SessionDeleted { get; private set; }

		public Task<bool> HealthAsync(CancellationToken cancellationToken) => Task.FromResult(true);

		public Task<string> CreateSessionAsync(string? title, CancellationToken cancellationToken) => Task.FromResult(sessionId);

		public Task DeleteSessionAsync(string sessionIdArg, CancellationToken cancellationToken)
		{
			SessionDeleted = true;
			return Task.CompletedTask;
		}

		public Task PromptAsync(string sessionIdArg, OpenCodePromptRequest request, CancellationToken cancellationToken)
		{
			LastPrompt = request;
			return Task.CompletedTask;
		}

		public Task AbortSessionAsync(string sessionIdArg, CancellationToken cancellationToken) => Task.CompletedTask;

		public Task RespondPermissionAsync(string sessionIdArg, string permissionId, string response, CancellationToken cancellationToken)
		{
			lock (PermissionResponses)
				PermissionResponses.Add((permissionId, response));
			return Task.CompletedTask;
		}

		public Task AddMcpAsync(string name, object config, CancellationToken cancellationToken) => Task.CompletedTask;

		public Task<IReadOnlyList<string>> ListAgentNamesAsync(CancellationToken cancellationToken)
			=> Task.FromResult<IReadOnlyList<string>>(["build", "general"]);

		public Task<IReadOnlyList<string>> ListMcpNamesAsync(CancellationToken cancellationToken)
			=> Task.FromResult<IReadOnlyList<string>>([.. McpNames]);

		public async IAsyncEnumerable<OpenCodeServerEvent> SubscribeAsync([EnumeratorCancellation] CancellationToken cancellationToken)
		{
			foreach (var e in Events)
			{
				cancellationToken.ThrowIfCancellationRequested();
				yield return e;
				await Task.Yield();
			}
		}

		public ValueTask DisposeAsync() => ValueTask.CompletedTask;
	}

	/// <summary>
	/// Stateful fake whose event stream dies on the first attempt (forcing a transport-class
	/// failure) and completes cleanly on the second — used to exercise the cold-restart swap loop.
	/// </summary>
	private sealed class SwapScriptedClient(string sessionId) : IOpenCodeClient
	{
		public int CreateCount { get; private set; }
		public int SubscribeCount { get; private set; }
		public List<string> PromptSessionIds { get; } = [];

		public string BaseUrl => "http://fake-opencode";

		public Task<bool> HealthAsync(CancellationToken cancellationToken) => Task.FromResult(true);

		public Task<string> CreateSessionAsync(string? title, CancellationToken cancellationToken)
		{
			CreateCount++;
			return Task.FromResult(sessionId);
		}

		public Task DeleteSessionAsync(string sessionIdArg, CancellationToken cancellationToken) => Task.CompletedTask;

		public Task PromptAsync(string sessionIdArg, OpenCodePromptRequest request, CancellationToken cancellationToken)
		{
			PromptSessionIds.Add(sessionIdArg);
			return Task.CompletedTask;
		}

		public Task AbortSessionAsync(string sessionIdArg, CancellationToken cancellationToken) => Task.CompletedTask;

		public Task RespondPermissionAsync(string sessionIdArg, string permissionId, string response, CancellationToken cancellationToken) => Task.CompletedTask;

		public Task AddMcpAsync(string name, object config, CancellationToken cancellationToken) => Task.CompletedTask;

		public Task<IReadOnlyList<string>> ListAgentNamesAsync(CancellationToken cancellationToken)
			=> Task.FromResult<IReadOnlyList<string>>(["build"]);

		public Task<IReadOnlyList<string>> ListMcpNamesAsync(CancellationToken cancellationToken)
			=> Task.FromResult<IReadOnlyList<string>>([]);

		public async IAsyncEnumerable<OpenCodeServerEvent> SubscribeAsync([EnumeratorCancellation] CancellationToken cancellationToken)
		{
			SubscribeCount++;
			if (SubscribeCount == 1)
			{
				// First attempt: simulate a lost event stream. PumpEventsAsync converts this into
				// an OpenCodeClientUnhealthyException("event_stream_lost") → "transport_lost".
				await Task.Yield();
				throw new IOException("event stream lost");
			}

			// Recovery attempt: a clean idle completes the turn.
			yield return TestEvents.Event("session.idle", $$"""{ "sessionID": "{{sessionId}}" }""");
			await Task.Yield();
		}

		public ValueTask DisposeAsync() => ValueTask.CompletedTask;
	}
}
