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

	private sealed class FakeFactory(FakeOpenCodeClient client) : IOpenCodeClientFactory
	{
		public IOpenCodeClient Create(string baseUrl, string? username, string? password) => client;
	}

	private sealed class FakeOpenCodeClient(string sessionId) : IOpenCodeClient
	{
		public string BaseUrl => "http://fake-opencode";
		public List<OpenCodeServerEvent> Events { get; init; } = [];
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
}
