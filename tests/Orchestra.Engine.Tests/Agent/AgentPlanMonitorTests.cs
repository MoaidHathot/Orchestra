using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Orchestra.Engine.Tests.Agent;

public class AgentPlanMonitorTests
{
	[Fact]
	public async Task Refreshes_AreSerializedCoalescedAndFinalizedBeforeDisposal()
	{
		var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var refreshed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var reads = 0;
		var active = 0;
		var maximum = 0;
		var observed = new System.Collections.Concurrent.ConcurrentQueue<AgentPlanSnapshot>();
		var monitor = new AgentPlanMonitor(async token =>
		{
			var count = Interlocked.Increment(ref reads);
			maximum = Math.Max(maximum, Interlocked.Increment(ref active));
			try
			{
				if (count == 1) { entered.TrySetResult(); await release.Task.WaitAsync(token); }
				if (count == 2) refreshed.TrySetResult();
				return [new AgentPlanSnapshot { Provider = "test", SessionId = "session", Source = "tasks", Title = "read " + count }];
			}
			finally { Interlocked.Decrement(ref active); }
		}, observed.Enqueue, NullLogger.Instance);
		await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
		for (var i = 0; i < 20; i++) monitor.RequestRefresh();
		reads.Should().Be(1);
		release.TrySetResult();
		await refreshed.Task.WaitAsync(TimeSpan.FromSeconds(5));
		await monitor.DisposeAsync();
		var countAfter = reads;
		await monitor.DisposeAsync();
		maximum.Should().Be(1);
		reads.Should().Be(countAfter).And.BeGreaterThanOrEqualTo(3);
		observed.Last().Title.Should().Be("read " + reads);
	}
}
