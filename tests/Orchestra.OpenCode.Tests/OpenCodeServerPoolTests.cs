using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Orchestra.Engine;

namespace Orchestra.OpenCode.Tests;

/// <summary>
/// Unit tests for <see cref="OpenCodeServerPool"/> dedicated-slot accounting. Dedicated
/// (per-step, config-having) servers spawned directly by <c>OpenCodeAgent.RunDedicatedAsync</c>
/// reserve a capacity slot through the pool so they stay bounded by <c>maxInstances</c> and are
/// counted in <see cref="OpenCodeServerPool.GetSnapshot"/> — one instance and one session each —
/// until released. The accounting is exercised without spawning a real <c>opencode serve</c>
/// (the client factory is never invoked on this path).
/// </summary>
public class OpenCodeServerPoolTests
{
	/// <summary>Fails loudly if the dedicated-slot accounting ever tries to create a client.</summary>
	private sealed class ThrowingFactory : IOpenCodeClientFactory
	{
		public IOpenCodeClient Create(string baseUrl, string? username, string? password)
			=> throw new InvalidOperationException("dedicated-slot accounting must not create a client");
	}

	private static OpenCodeServerPool NewPool(int maxInstances)
		=> new(
			new AgentPoolConfig { MinInstances = 0, MaxInstances = maxInstances, MaxSessionsPerInstance = 1 },
			new OpenCodeAgentPoolOptions { DefaultMinInstances = 0, EngineToolBridgeEnabled = false },
			new ThrowingFactory(),
			NullLoggerFactory.Instance);

	[Fact]
	public async Task GetSnapshot_IdlePool_ReportsZeroInstancesAndSessions()
	{
		await using var pool = NewPool(maxInstances: 4);

		var snapshot = pool.GetSnapshot();

		snapshot.Instances.Should().Be(0);
		snapshot.ActiveSessions.Should().Be(0);
		snapshot.MaxInstances.Should().Be(4);
	}

	[Fact]
	public async Task AcquireDedicatedSlot_CountsAsOneInstanceAndOneSession()
	{
		await using var pool = NewPool(maxInstances: 4);

		await using var slot = await pool.AcquireDedicatedSlotAsync(CancellationToken.None);

		var snapshot = pool.GetSnapshot();
		snapshot.Instances.Should().Be(1, "a dedicated server is reported as one live instance");
		snapshot.ActiveSessions.Should().Be(1, "each dedicated server hosts exactly one session");
	}

	[Fact]
	public async Task DedicatedSlots_Release_RestoresZero()
	{
		await using var pool = NewPool(maxInstances: 4);

		var first = await pool.AcquireDedicatedSlotAsync(CancellationToken.None);
		var second = await pool.AcquireDedicatedSlotAsync(CancellationToken.None);

		var busy = pool.GetSnapshot();
		busy.Instances.Should().Be(2);
		busy.ActiveSessions.Should().Be(2);

		await first.DisposeAsync();
		await second.DisposeAsync();

		var released = pool.GetSnapshot();
		released.Instances.Should().Be(0, "dedicated servers are dropped from accounting once the step finishes");
		released.ActiveSessions.Should().Be(0);
	}

	[Fact]
	public async Task DedicatedSlot_DoubleDispose_ReleasesCapacityOnce()
	{
		await using var pool = NewPool(maxInstances: 2);

		var slot = await pool.AcquireDedicatedSlotAsync(CancellationToken.None);
		await slot.DisposeAsync();
		await slot.DisposeAsync(); // idempotent — must not double-release capacity or underflow the count

		pool.GetSnapshot().Instances.Should().Be(0);
		pool.GetSnapshot().ActiveSessions.Should().Be(0);
	}

	[Fact]
	public async Task AcquireDedicatedSlot_AtCapacity_WaitsUntilReleased()
	{
		// capacity = maxInstances * maxSessionsPerInstance = 1, so the second acquire must block
		// until the first slot is released (dedicated servers honor the maxInstances cap).
		await using var pool = NewPool(maxInstances: 1);

		var first = await pool.AcquireDedicatedSlotAsync(CancellationToken.None);
		var secondAcquire = pool.AcquireDedicatedSlotAsync(CancellationToken.None);

		await Task.Delay(100);
		secondAcquire.IsCompleted.Should().BeFalse("the pool is at its instance cap");
		pool.GetSnapshot().Instances.Should().Be(1);

		await first.DisposeAsync();

		var second = await secondAcquire;
		pool.GetSnapshot().Instances.Should().Be(1);
		await second.DisposeAsync();
		pool.GetSnapshot().Instances.Should().Be(0);
	}
}
