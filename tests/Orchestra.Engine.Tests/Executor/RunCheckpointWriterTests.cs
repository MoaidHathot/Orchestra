using FluentAssertions;

namespace Orchestra.Engine.Tests.Executor;

public class RunCheckpointWriterTests
{
	private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);

	[Fact]
	public async Task ParallelCompletions_ShareSnapshotsAndWaitForTheirOwnDurableRevision()
	{
		var releaseFirst = Gate();
		var coveringWriteStarted = Gate();
		var releaseCoveringWrite = Gate();
		var snapshots = new List<Dictionary<string, CheckpointStepResult>>();
		await using var writer = new RunCheckpointWriter(null, async (snapshot, _) =>
		{
			snapshots.Add(snapshot);
			if (snapshots.Count == 1)
				await releaseFirst.Task;
			else
			{
				coveringWriteStarted.TrySetResult();
				await releaseCoveringWrite.Task;
			}
		});

		var first = writer.SaveAsync("a", ExecutionResult.Succeeded("a"), null, CancellationToken.None);
		var second = writer.SaveAsync("b", ExecutionResult.Succeeded("b"), null, CancellationToken.None);
		var third = writer.SaveAsync("c", ExecutionResult.Succeeded("c"), null, CancellationToken.None);
		try
		{
			snapshots.Should().ContainSingle("writes for one run are serialized");
			snapshots[0].Keys.Should().Equal("a");
			releaseFirst.TrySetResult();
			(await first.WaitAsync(TimeSpan.FromSeconds(5))).Should().Be(1);
			await coveringWriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
			second.IsCompleted.Should().BeFalse("the first snapshot does not contain b");
			third.IsCompleted.Should().BeFalse("the covering snapshot is not durable yet");
			snapshots[1].Keys.Should().BeEquivalentTo(new[] { "a", "b", "c" });
		}
		finally
		{
			releaseFirst.TrySetResult();
			releaseCoveringWrite.TrySetResult();
			await Task.WhenAll(first, second, third).WaitAsync(TimeSpan.FromSeconds(5));
		}
		snapshots.Should().HaveCount(2, "b and c share a durable snapshot instead of rewriting the same history twice");
		(await second).Should().Be(3);
		(await third).Should().Be(3);
	}

	[Fact]
	public async Task FailedWrite_DoesNotAdvanceTheDurableRevisionOrLosePendingResults()
	{
		var calls = 0;
		Dictionary<string, CheckpointStepResult>? saved = null;
		await using var writer = new RunCheckpointWriter(null, (snapshot, _) =>
		{
			if (++calls == 1)
				throw new IOException("temporary disk failure");
			saved = snapshot;
			return Task.CompletedTask;
		});
		var first = () => writer.SaveAsync("a", ExecutionResult.Succeeded("a"), null, CancellationToken.None);
		await first.Should().ThrowAsync<IOException>();

		await writer.SaveAsync("b", ExecutionResult.Succeeded("b"), null, CancellationToken.None);

		saved!.Keys.Should().BeEquivalentTo(new[] { "a", "b" });
	}

	[Fact]
	public async Task Closing_JoinsAnInFlightWriteAndRejectsLateWrites()
	{
		var release = Gate();
		var writes = 0;
		await using var writer = new RunCheckpointWriter(null, async (_, _) =>
		{
			writes++;
			await release.Task;
		});
		var save = writer.SaveAsync("a", ExecutionResult.Succeeded("a"), null, CancellationToken.None);
		var close = writer.CloseAsync();
		try
		{
			close.IsCompleted.Should().BeFalse("deleting the checkpoint before the write ends can resurrect it");
			(await writer.SaveAsync("late", ExecutionResult.Succeeded("late"), null, CancellationToken.None)).Should().BeNull();
		}
		finally
		{
			release.TrySetResult();
			await Task.WhenAll(save, close).WaitAsync(TimeSpan.FromSeconds(5));
		}
		writes.Should().Be(1);
	}

	[Fact]
	public async Task LoopOutcome_UpdatesCheckerAndTargetAtomically_AndRemovesFailedRevisions()
	{
		var snapshots = new List<Dictionary<string, CheckpointStepResult>>();
		await using var writer = new RunCheckpointWriter(null, (snapshot, _) =>
		{
			snapshots.Add(snapshot);
			return Task.CompletedTask;
		});
		await writer.SaveAsync("draft", ExecutionResult.Succeeded("v1"), null, CancellationToken.None);
		await writer.SaveAsync("check", ExecutionResult.Succeeded("PASS"),
			new("draft", ExecutionResult.Succeeded("v2")), CancellationToken.None);
		snapshots[1]["draft"].Content.Should().Be("v2");
		snapshots[1]["check"].Content.Should().Be("PASS");

		await writer.SaveAsync("check", ExecutionResult.Skipped("draft failed"),
			new("draft", ExecutionResult.Failed("failed revision")), CancellationToken.None);
		snapshots[2].Should().BeEmpty("old successful revisions cannot mask a later loop failure on resume");
	}
}
