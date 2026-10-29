using System.Runtime.CompilerServices;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Orchestra.Engine;

namespace Orchestra.OpenCode.Tests;

public class OpenCodeReviewLifecycleTests
{
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
		public Task EnsureReadyAsync(OpenCodeModelDto model, string? agent, TimeSpan timeout, CancellationToken token) => Task.CompletedTask;
		public Task<bool> IsSessionWithinAsync(string candidate, string root, CancellationToken token) => Task.FromResult(candidate == root);
		public Task<JsonElement> GetFormAsync(string session, string form, CancellationToken token) => throw new NotSupportedException();
		public Task ReplyFormAsync(string session, string form, object? answer, CancellationToken token) => throw new NotSupportedException();
		public Task<IReadOnlyList<string>> WaitForMcpNamesAsync(IReadOnlyCollection<string> required, TimeSpan timeout, CancellationToken token) => ListMcpNamesAsync(token);
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
		public Task<IReadOnlyList<JsonElement>> WaitForMessagesAsync(string session, CancellationToken token) => Task.FromResult<IReadOnlyList<JsonElement>>([
			JsonSerializer.SerializeToElement(new { type = "user", id = "request" }),
			JsonSerializer.SerializeToElement(new { type = "assistant", content = new[] { new { type = "text", text = "ok" } } }),
			JsonSerializer.SerializeToElement(new { type = "idle", outcome = "succeeded" })]);
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
				await Task.Delay(Timeout.Infinite, token);
			}
			finally { StreamStopped = true; }
		}
		public ValueTask DisposeAsync() => ValueTask.CompletedTask;
	}
}
