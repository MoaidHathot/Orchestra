using System.Runtime.CompilerServices;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Orchestra.Engine;

namespace Orchestra.OpenCode.Tests;

public class OpenCodeReviewLifecycleTests
{
	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task EngineToolCatalog_MustBeReadyBeforeSubmittingTheFirstPrompt(bool fails)
	{
		var client = new Client { BlockEngineMcp = true, FailEngineMcp = fails };
		await using var builder = new OpenCodeAgentBuilder(NullLoggerFactory.Instance,
			new OpenCodeAgentPoolOptions { ServerUrl = "http://test", DefaultMinInstances = 0, SwapBudgetPerStep = 0, EngineToolBridgeEnabled = true }, new Factory(client));
		await using var scope = await builder.CreateRunScopeAsync();
		var agent = await builder.BuildAgentAsync(new AgentBuildConfig { Model = "model" });
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
		var task = agent.SendAsync("go", timeout.Token);
		await client.EngineMcpWaitStarted.Task.WaitAsync(timeout.Token);
		client.AdmissionStarted.Task.IsCompleted.Should().BeFalse();
		client.ReleaseEngineMcp.TrySetResult();
		await Drain(task).WaitAsync(timeout.Token);
		if (fails)
		{
			await ((Func<Task>)(async () => await task.GetResultAsync())).Should().ThrowAsync<OpenCodeSessionFailedException>().WithMessage("*engine-tool MCP server failed to load*");
			client.AdmissionStarted.Task.IsCompleted.Should().BeFalse();
		}
		else
		{
			(await task.GetResultAsync()).Content.Should().Be("ok");
			client.AdmissionStarted.Task.IsCompleted.Should().BeTrue();
		}
	}

	[Fact]
	public async Task ChildPlanOwnershipLookup_DoesNotBlockTheTransportEventReader()
	{
		static OpenCodeServerEvent Event(string type, object data) => new() { Type = type, Properties = JsonSerializer.SerializeToElement(data) };
		var client = new Client
		{
			BlockWait = true,
			BlockOwnership = true,
			Events = [
				Event("session.tool.input.started", new { sessionID = "child", id = "todo", name = "todowrite" }),
				Event("session.tool.called", new { sessionID = "child", id = "todo", input = new { todos = new[] { new { content = "Child task", status = "pending" } } } }),
				Event("session.tool.success", new { sessionID = "child", id = "todo" }),
				Event("session.text.delta", new { sessionID = "session", delta = "transport still responsive" }),
			],
		};
		await using var builder = Builder(client);
		await using var scope = await builder.CreateRunScopeAsync();
		var agent = await builder.BuildAgentAsync(new AgentBuildConfig { Model = "model" });
		using var cancellation = new CancellationTokenSource();
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
		var task = agent.SendAsync("go", cancellation.Token);
		await client.OwnershipStarted.Task.WaitAsync(timeout.Token);
		var sawContent = false;
		await foreach (var evt in task.WithCancellation(timeout.Token))
		{
			if (evt.Content != "transport still responsive") continue;
			sawContent = true;
			break;
		}
		sawContent.Should().BeTrue();
		client.ReleaseOwnership.Task.IsCompleted.Should().BeFalse();
		client.ReleaseOwnership.TrySetResult();
		await cancellation.CancelAsync();
		await Drain(task).WaitAsync(timeout.Token);
		await ((Func<Task>)(async () => await task.GetResultAsync())).Should().ThrowAsync<OperationCanceledException>();
	}

	[Fact]
	public async Task Cancellation_ReconcilesDurablePlanBeforeDeletingTheSession()
	{
		var client = new Client
		{
			BlockWait = true,
			DurablePlans = [JsonSerializer.SerializeToElement(new
		{
			type = "assistant", content = new[] { new { type = "tool", id = "plan", name = "todowrite",
				state = new { status = "completed", input = new { todos = new[] { new { content = "Verify work", status = "in_progress" } } } } } },
		})]
		};
		await using var builder = Builder(client);
		await using var scope = await builder.CreateRunScopeAsync();
		var agent = await builder.BuildAgentAsync(new AgentBuildConfig { Model = "model" });
		using var cancellation = new CancellationTokenSource();
		var task = agent.SendAsync("go", cancellation.Token);
		await client.WaitStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
		await cancellation.CancelAsync();
		var events = new List<AgentEvent>();
		await foreach (var evt in task)
			events.Add(evt);
		await ((Func<Task>)(async () => await task.GetResultAsync())).Should().ThrowAsync<OperationCanceledException>();
		events.Single(e => e.Type == AgentEventType.PlanUpdated).Plan!.Tasks.Single().Title.Should().Be("Verify work");
		client.PlanReadAfterAbortBeforeDelete.Should().BeTrue();
		client.Deleted.Should().BeTrue();
	}

	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public async Task PlanReadFailure_IsDiagnosticOnly_AndCaptureOptOutSkipsReads(bool capture)
	{
		var client = new Client { FailPlanRead = true };
		await using var builder = Builder(client);
		await using var scope = await builder.CreateRunScopeAsync();
		var agent = await builder.BuildAgentAsync(new AgentBuildConfig { Model = "model", CapturePlans = capture });
		var task = agent.SendAsync("go");
		var events = new List<AgentEvent>();
		await foreach (var evt in task)
			events.Add(evt);
		(await task.GetResultAsync()).Content.Should().Be("ok");
		client.PlanReads.Should().Be(capture ? 1 : 0);
		events.Any(e => e.DiagnosticType == "plan_capture_failed").Should().Be(capture);
	}

	[Fact]
	public async Task ConfigurationFailure_ClosesEventReaderBeforeAnyTransportWork()
	{
		var client = new Client();
		await using var builder = Builder(client);
		await using var scope = await builder.CreateRunScopeAsync();
		var agent = await builder.BuildAgentAsync(new AgentBuildConfig { Model = "model", InstructionDirectories = [Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))] });
		var task = agent.SendAsync("go");
		await Drain(task).WaitAsync(TimeSpan.FromSeconds(2));
		Func<Task> result = async () => await task.GetResultAsync();
		await result.Should().ThrowAsync<DirectoryNotFoundException>();
		client.Created.Should().BeFalse();
	}

	[Fact]
	public async Task CancellationDuringAdmission_AbortsDeletesAndJoinsStream()
	{
		var client = new Client { BlockAdmission = true };
		await using var builder = Builder(client);
		await using var scope = await builder.CreateRunScopeAsync();
		var agent = await builder.BuildAgentAsync(new AgentBuildConfig { Model = "model" });
		using var cancellation = new CancellationTokenSource();
		var task = agent.SendAsync("go", cancellation.Token);
		await client.AdmissionStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
		await cancellation.CancelAsync();
		await Drain(task).WaitAsync(TimeSpan.FromSeconds(2));
		Func<Task> result = async () => await task.GetResultAsync();
		await result.Should().ThrowAsync<OperationCanceledException>();
		client.Aborted.Should().BeTrue();
		client.Deleted.Should().BeTrue();
		client.StreamStopped.Should().BeTrue();
	}

	[Fact]
	public async Task SubscriptionFailureBeforeReady_FailsPromptWithoutHanging()
	{
		var client = new Client { FailSubscription = true };
		await using var builder = Builder(client);
		await using var scope = await builder.CreateRunScopeAsync();
		var agent = await builder.BuildAgentAsync(new AgentBuildConfig { Model = "model" });
		var task = agent.SendAsync("go");
		await Drain(task).WaitAsync(TimeSpan.FromSeconds(2));
		Func<Task> result = async () => await task.GetResultAsync();
		await result.Should().ThrowAsync<OpenCodeClientUnhealthyException>();
		client.AdmissionStarted.Task.IsCompleted.Should().BeFalse();
		client.Deleted.Should().BeTrue();
	}

	[Fact]
	public async Task CleanupFailure_DoesNotReplaceSuccessfulResult()
	{
		var client = new Client { FailDelete = true };
		await using var builder = Builder(client);
		await using var scope = await builder.CreateRunScopeAsync();
		var agent = await builder.BuildAgentAsync(new AgentBuildConfig { Model = "model" });
		var task = agent.SendAsync("go");
		await Drain(task).WaitAsync(TimeSpan.FromSeconds(2));
		(await task.GetResultAsync()).Content.Should().Be("ok");
		client.CleanupWasBounded.Should().BeTrue();
		client.StreamStopped.Should().BeTrue();
	}

	private static OpenCodeAgentBuilder Builder(Client client) => new(NullLoggerFactory.Instance,
		new OpenCodeAgentPoolOptions { ServerUrl = "http://test", DefaultMinInstances = 0, SwapBudgetPerStep = 0, EngineToolBridgeEnabled = false }, new Factory(client));
	private static async Task Drain(AgentTask task) { await foreach (var _ in task) { } }
	private sealed class Factory(IOpenCodeClient client) : IOpenCodeClientFactory { public IOpenCodeClient Create(string url, string? username, string? password) => client; }
	private sealed class Client : IOpenCodeClient
	{
		public Task<IReadOnlyList<JsonElement>> ReadMessagesAsync(string sessionId, CancellationToken token)
		{
			PlanReads++;
			PlanReadAfterAbortBeforeDelete = Aborted && !Deleted && token.CanBeCanceled;
			if (FailPlanRead)
				throw new IOException("Plan message read failed");
			return Task.FromResult(DurablePlans);
		}
		public IReadOnlyList<JsonElement> DurablePlans { get; init; } = [];
		public bool FailPlanRead { get; init; }
		public int PlanReads { get; private set; }
		public bool PlanReadAfterAbortBeforeDelete { get; private set; }
		public bool BlockWait { get; init; }
		public TaskCompletionSource WaitStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public Task EnsureReadyAsync(OpenCodeModelDto model, string? agent, TimeSpan timeout, CancellationToken token) => Task.CompletedTask;
		public async Task<bool> IsSessionWithinAsync(string candidate, string root, CancellationToken token)
		{
			if (BlockOwnership && candidate == "child")
			{
				OwnershipStarted.TrySetResult();
				await ReleaseOwnership.Task.WaitAsync(token);
				return true;
			}
			return candidate == root;
		}
		public bool BlockOwnership { get; init; }
		public TaskCompletionSource OwnershipStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public TaskCompletionSource ReleaseOwnership { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public IReadOnlyList<OpenCodeServerEvent> Events { get; init; } = [];
		public Task<JsonElement> GetFormAsync(string session, string form, CancellationToken token) => throw new NotSupportedException();
		public Task ReplyFormAsync(string session, string form, object? answer, CancellationToken token) => throw new NotSupportedException();
		public async Task<IReadOnlyList<string>> WaitForMcpNamesAsync(IReadOnlyCollection<string> required, TimeSpan timeout, CancellationToken token)
		{
			if (BlockEngineMcp && required.Contains("orchestra-engine-tools"))
			{
				EngineMcpWaitStarted.TrySetResult();
				await ReleaseEngineMcp.Task.WaitAsync(token);
				return FailEngineMcp ? [] : ["orchestra-engine-tools"];
			}
			return await ListMcpNamesAsync(token);
		}
		public bool BlockEngineMcp { get; init; }
		public bool FailEngineMcp { get; init; }
		public TaskCompletionSource EngineMcpWaitStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public TaskCompletionSource ReleaseEngineMcp { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public string BaseUrl => "http://test";
		public bool BlockAdmission { get; init; }
		public bool FailSubscription { get; init; }
		public bool FailDelete { get; init; }
		public bool Created { get; private set; }
		public bool Aborted { get; private set; }
		public bool Deleted { get; private set; }
		public bool StreamStopped { get; private set; }
		public bool CleanupWasBounded { get; private set; }
		public TaskCompletionSource AdmissionStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public Task<bool> HealthAsync(CancellationToken token) => Task.FromResult(true);
		public Task<string> CreateSessionAsync(OpenCodeCreateSessionRequest request, CancellationToken token) { Created = true; return Task.FromResult("session"); }
		public async Task<string> PromptAsync(string session, OpenCodePromptRequest request, CancellationToken token)
		{
			AdmissionStarted.TrySetResult();
			if (BlockAdmission)
				await Task.Delay(Timeout.Infinite, token);
			return "request";
		}
		public async Task<IReadOnlyList<JsonElement>> WaitForMessagesAsync(string session, CancellationToken token)
		{
			WaitStarted.TrySetResult();
			if (BlockWait)
				await Task.Delay(Timeout.Infinite, token);
			return [JsonSerializer.SerializeToElement(new { type = "user", id = "request" }),
				JsonSerializer.SerializeToElement(new { type = "assistant", content = new[] { new { type = "text", text = "ok" } } }),
				JsonSerializer.SerializeToElement(new { type = "idle", outcome = "succeeded" })];
		}
		public Task DeleteSessionAsync(string session, CancellationToken token) { Deleted = true; CleanupWasBounded = token.CanBeCanceled; if (FailDelete) throw new IOException("delete failed"); return Task.CompletedTask; }
		public Task AbortSessionAsync(string session, CancellationToken token) { Aborted = true; return Task.CompletedTask; }
		public Task RespondPermissionAsync(string session, string permission, string response, CancellationToken token) => Task.CompletedTask;
		public Task AddMcpAsync(string name, object config, CancellationToken token) => Task.CompletedTask;
		public Task<IReadOnlyList<string>> ListAgentNamesAsync(CancellationToken token) => Task.FromResult<IReadOnlyList<string>>([]);
		public Task<IReadOnlyList<string>> ListMcpNamesAsync(CancellationToken token) => Task.FromResult<IReadOnlyList<string>>([]);
		public async IAsyncEnumerable<OpenCodeServerEvent> SubscribeAsync([EnumeratorCancellation] CancellationToken token)
		{
			try
			{
				if (FailSubscription)
					throw new IOException("connection closed before ready");
				yield return new() { Type = "server.connected", Properties = JsonSerializer.SerializeToElement(new { }) };
				foreach (var evt in Events) yield return evt;
				await Task.Delay(Timeout.Infinite, token);
			}
			finally { StreamStopped = true; }
		}
		public ValueTask DisposeAsync() => ValueTask.CompletedTask;
	}
}
