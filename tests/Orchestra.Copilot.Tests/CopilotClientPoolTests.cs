using FluentAssertions;
using GitHub.Copilot.SDK;
using Microsoft.Extensions.Logging.Abstractions;
using Orchestra.Engine;

namespace Orchestra.Copilot.Tests;

public class CopilotClientPoolTests
{
	[Fact]
	public async Task AcquireAsync_ConcurrentRequests_ScalesUpToMaxInstances()
	{
		var factory = new FakeCopilotClientFactory();
		await using var pool = CreatePool(factory, maxInstances: 3, maxSessionsPerInstance: 1);

		await using var lease1 = await pool.AcquireAsync(CancellationToken.None);
		await using var lease2 = await pool.AcquireAsync(CancellationToken.None);
		await using var lease3 = await pool.AcquireAsync(CancellationToken.None);

		factory.CreatedClients.Should().HaveCount(3);
		new[] { lease1.Client.DiagnosticHash, lease2.Client.DiagnosticHash, lease3.Client.DiagnosticHash }
			.Should().OnlyHaveUniqueItems();
	}

	[Fact]
	public async Task AcquireAsync_AtCapacity_WaitsUntilLeaseIsReleased()
	{
		var factory = new FakeCopilotClientFactory();
		await using var pool = CreatePool(factory, maxInstances: 1, maxSessionsPerInstance: 1);

		var firstLease = await pool.AcquireAsync(CancellationToken.None);
		var secondAcquire = pool.AcquireAsync(CancellationToken.None).AsTask();

		await Task.Delay(100);
		secondAcquire.IsCompleted.Should().BeFalse();

		await firstLease.DisposeAsync();
		await using var secondLease = await secondAcquire;

		secondLease.Client.DiagnosticHash.Should().Be(firstLease.Client.DiagnosticHash);
		factory.CreatedClients.Should().HaveCount(1);
	}

	[Fact]
	public async Task PrewarmAsync_StartsMinInstances()
	{
		var factory = new FakeCopilotClientFactory();
		await using var pool = CreatePool(factory, minInstances: 2, maxInstances: 4, maxSessionsPerInstance: 1);

		await pool.PrewarmAsync(CancellationToken.None);

		factory.CreatedClients.Should().HaveCount(2);
		factory.CreatedClients.Should().AllSatisfy(client => client.StartCalls.Should().Be(1));
	}

	[Fact]
	public async Task GetSnapshot_ReturnsCurrentCliAndSessionCounts()
	{
		var factory = new FakeCopilotClientFactory();
		await using var pool = CreatePool(factory, maxInstances: 2, maxSessionsPerInstance: 1);

		await using var lease1 = await pool.AcquireAsync(CancellationToken.None);
		await using var lease2 = await pool.AcquireAsync(CancellationToken.None);

		var busySnapshot = pool.GetSnapshot();
		busySnapshot.CliInstances.Should().Be(2);
		busySnapshot.ActiveSessions.Should().Be(2);

		await lease1.DisposeAsync();

		var releasedSnapshot = pool.GetSnapshot();
		releasedSnapshot.CliInstances.Should().Be(2);
		releasedSnapshot.ActiveSessions.Should().Be(1);
	}

	[Fact]
	public async Task DisposeAsync_StopsAndDisposesAllWorkers()
	{
		var factory = new FakeCopilotClientFactory();
		var pool = CreatePool(factory, maxInstances: 2, maxSessionsPerInstance: 1);

		await using var lease1 = await pool.AcquireAsync(CancellationToken.None);
		await using var lease2 = await pool.AcquireAsync(CancellationToken.None);

		await pool.DisposeAsync();

		factory.CreatedClients.Should().HaveCount(2);
		factory.CreatedClients.Should().AllSatisfy(client =>
		{
			client.StopCalls.Should().Be(1);
			client.DisposeCalls.Should().Be(1);
		});
	}

	[Fact]
	public async Task RecordSwapTriggered_IncrementsCounterOnSnapshot()
	{
		// CopilotAgent calls _clientPool.RecordSwapTriggered() each time it abandons a CLI
		// worker. The snapshot exposes the counter for AgentRuntimeStatus / observability.
		var factory = new FakeCopilotClientFactory();
		await using var pool = CreatePool(factory, maxInstances: 4, maxSessionsPerInstance: 1);

		pool.GetSnapshot().TotalSwapsTriggered.Should().Be(0);

		pool.RecordSwapTriggered();
		pool.RecordSwapTriggered();
		pool.RecordSwapTriggered();

		pool.GetSnapshot().TotalSwapsTriggered.Should().Be(3);
	}

	private static CopilotClientPool CreatePool(
		FakeCopilotClientFactory factory,
		int minInstances = 0,
		int maxInstances = 4,
		int maxSessionsPerInstance = 1,
		int idleTimeoutSeconds = 0)
	{
		return new CopilotClientPool(
			new AgentPoolConfig
			{
				MinInstances = minInstances,
				MaxInstances = maxInstances,
				MaxSessionsPerInstance = maxSessionsPerInstance,
				IdleTimeoutSeconds = idleTimeoutSeconds,
			},
			new CopilotAgentPoolOptions(),
			factory,
			NullLoggerFactory.Instance);
	}

	internal sealed class FakeCopilotClientFactory : ICopilotClientFactory
	{
		private int _nextHash;
		public List<FakeCopilotClient> CreatedClients { get; } = [];

		public ICopilotClient CreateClient()
		{
			var client = new FakeCopilotClient(Interlocked.Increment(ref _nextHash));
			CreatedClients.Add(client);
			return client;
		}
	}

	internal sealed class FakeCopilotClient : ICopilotClient
	{
		public FakeCopilotClient(int diagnosticHash)
		{
			DiagnosticHash = diagnosticHash;
		}

		public int DiagnosticHash { get; }
		public ConnectionState State { get; private set; } = ConnectionState.Disconnected;
		public int StartCalls { get; private set; }
		public int StopCalls { get; private set; }
		public int DisposeCalls { get; private set; }

		public Task StartAsync(CancellationToken cancellationToken)
		{
			StartCalls++;
			State = ConnectionState.Connected;
			return Task.CompletedTask;
		}

		public Task StopAsync()
		{
			StopCalls++;
			State = ConnectionState.Disconnected;
			return Task.CompletedTask;
		}

		public Task PingAsync(string message, CancellationToken cancellationToken) => Task.CompletedTask;
		public Task<ICopilotSession> CreateSessionAsync(SessionConfig config, CancellationToken cancellationToken) => throw new NotSupportedException();
		public Task<ICopilotSession> ResumeSessionAsync(string sessionId, ResumeSessionConfig config, CancellationToken cancellationToken) => throw new NotSupportedException();
		public Task<string?> GetLastSessionIdAsync(CancellationToken cancellationToken) => Task.FromResult<string?>(null);
		public Task DeleteSessionAsync(string sessionId, CancellationToken cancellationToken) => Task.CompletedTask;
		public Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

		public ValueTask DisposeAsync()
		{
			DisposeCalls++;
			return ValueTask.CompletedTask;
		}
	}
}
