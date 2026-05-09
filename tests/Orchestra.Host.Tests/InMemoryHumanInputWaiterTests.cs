using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Orchestra.Engine;
using Orchestra.Host.Persistence;
using Xunit;

namespace Orchestra.Host.Tests;

/// <summary>
/// Unit tests for <see cref="InMemoryHumanInputWaiter"/>.
/// </summary>
public class InMemoryHumanInputWaiterTests
{
	[Fact]
	public async Task TryComplete_ResolvesWaitingTask()
	{
		var waiter = new InMemoryHumanInputWaiter(NullLogger<InMemoryHumanInputWaiter>.Instance);

		var waitTask = waiter.WaitAsync("orch", "run", "step", CancellationToken.None);

		var completed = waiter.TryComplete("orch", "run", "step", new UserInputResponse
		{
			Reply = "ok",
			RespondedAt = DateTimeOffset.UtcNow,
		});

		completed.Should().BeTrue();
		var response = await waitTask;
		response.Reply.Should().Be("ok");
	}

	[Fact]
	public void TryComplete_ReturnsFalse_WhenNoWaitRegistered()
	{
		var waiter = new InMemoryHumanInputWaiter(NullLogger<InMemoryHumanInputWaiter>.Instance);

		var completed = waiter.TryComplete("orch", "run", "step", new UserInputResponse
		{
			Reply = "ok",
			RespondedAt = DateTimeOffset.UtcNow,
		});

		completed.Should().BeFalse();
	}

	[Fact]
	public async Task WaitAsync_ThrowsWhenCancelled()
	{
		var waiter = new InMemoryHumanInputWaiter(NullLogger<InMemoryHumanInputWaiter>.Instance);
		using var cts = new CancellationTokenSource();

		var waitTask = waiter.WaitAsync("orch", "run", "step", cts.Token);
		cts.Cancel();

		var act = async () => await waitTask;
		await act.Should().ThrowAsync<OperationCanceledException>();
	}

	[Fact]
	public async Task DifferentRuns_DoNotInterfere()
	{
		var waiter = new InMemoryHumanInputWaiter(NullLogger<InMemoryHumanInputWaiter>.Instance);

		var task1 = waiter.WaitAsync("orch", "run-1", "s", CancellationToken.None);
		var task2 = waiter.WaitAsync("orch", "run-2", "s", CancellationToken.None);

		waiter.TryComplete("orch", "run-1", "s", new UserInputResponse { Reply = "first", RespondedAt = DateTimeOffset.UtcNow });
		var r1 = await task1;
		r1.Reply.Should().Be("first");

		// Second run still pending.
		task2.IsCompleted.Should().BeFalse();
		waiter.TryComplete("orch", "run-2", "s", new UserInputResponse { Reply = "second", RespondedAt = DateTimeOffset.UtcNow });
		var r2 = await task2;
		r2.Reply.Should().Be("second");
	}

	[Fact]
	public async Task TryCancel_CompletesWaitWithCancellation()
	{
		var waiter = new InMemoryHumanInputWaiter(NullLogger<InMemoryHumanInputWaiter>.Instance);

		var waitTask = waiter.WaitAsync("orch", "run", "step", CancellationToken.None);
		var cancelled = waiter.TryCancel("orch", "run", "step");

		cancelled.Should().BeTrue();
		var act = async () => await waitTask;
		await act.Should().ThrowAsync<OperationCanceledException>();
	}

	[Fact]
	public async Task SecondWaitOnSameKey_AfterCompletion_GetsFreshTcs()
	{
		var waiter = new InMemoryHumanInputWaiter(NullLogger<InMemoryHumanInputWaiter>.Instance);

		var t1 = waiter.WaitAsync("orch", "run", "step", CancellationToken.None);
		waiter.TryComplete("orch", "run", "step", new UserInputResponse { Reply = "1", RespondedAt = DateTimeOffset.UtcNow });
		await t1;

		// Allow the continuation that removes the entry to run.
		await Task.Delay(50);

		var t2 = waiter.WaitAsync("orch", "run", "step", CancellationToken.None);
		t2.IsCompleted.Should().BeFalse();

		waiter.TryComplete("orch", "run", "step", new UserInputResponse { Reply = "2", RespondedAt = DateTimeOffset.UtcNow });
		var r = await t2;
		r.Reply.Should().Be("2");
	}
}
