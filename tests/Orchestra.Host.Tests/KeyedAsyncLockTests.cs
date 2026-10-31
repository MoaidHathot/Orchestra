using FluentAssertions;
using Orchestra.Host.Persistence;
using Xunit;

namespace Orchestra.Host.Tests;

public class KeyedAsyncLockTests
{
	[Fact]
	public async Task SameKeySerializes_WhileDifferentRunsProceedIndependently()
	{
		var gates = new KeyedAsyncLock();
		using var first = await gates.AcquireAsync("a", CancellationToken.None);
		var waiting = gates.AcquireAsync("a", CancellationToken.None).AsTask();
		try
		{
			waiting.IsCompleted.Should().BeFalse();
			using var independent = await gates.AcquireAsync("b", CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
			waiting.IsCompleted.Should().BeFalse();
		}
		finally
		{
			first.Dispose();
			(await waiting.WaitAsync(TimeSpan.FromSeconds(5))).Dispose();
		}
	}

	[Fact]
	public async Task CancelledWaiter_DoesNotRemoveAGateThatAnotherCallerStillHolds()
	{
		var gates = new KeyedAsyncLock();
		using var first = await gates.AcquireAsync("a", CancellationToken.None);
		using var cancellation = new CancellationTokenSource();
		var cancelled = gates.AcquireAsync("a", cancellation.Token).AsTask();
		await cancellation.CancelAsync();
		var wait = () => cancelled;
		await wait.Should().ThrowAsync<OperationCanceledException>();

		var next = gates.AcquireAsync("a", CancellationToken.None).AsTask();
		try
		{
			next.IsCompleted.Should().BeFalse("cancelling one waiter must not open a second gate for the same run");
		}
		finally
		{
			first.Dispose();
			(await next.WaitAsync(TimeSpan.FromSeconds(5))).Dispose();
		}
		using var fresh = await gates.AcquireAsync("a", CancellationToken.None);
	}
}
