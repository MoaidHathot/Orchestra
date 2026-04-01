using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Orchestra.Engine;
using Orchestra.Host.Hosting;
using Orchestra.Host.Persistence;
using Xunit;

namespace Orchestra.Host.Tests;

/// <summary>
/// Tests for run retention policy (Work Item 4).
/// Covers ApplyRetentionAsync in FileSystemRunStore and RunRetentionService background service.
/// </summary>
public class RunRetentionTests : IDisposable
{
	private readonly string _tempDir;
	private readonly FileSystemRunStore _store;

	public RunRetentionTests()
	{
		_tempDir = Path.Combine(Path.GetTempPath(), $"orchestra-retention-tests-{Guid.NewGuid():N}");
		Directory.CreateDirectory(_tempDir);
		_store = new FileSystemRunStore(_tempDir);
	}

	public void Dispose()
	{
		if (Directory.Exists(_tempDir))
		{
			try { Directory.Delete(_tempDir, recursive: true); }
			catch { /* best-effort cleanup */ }
		}
	}

	private OrchestrationRunRecord CreateTestRecord(
		string? runId = null,
		string? orchestrationName = null,
		string? triggerId = null,
		ExecutionStatus status = ExecutionStatus.Succeeded,
		DateTimeOffset? startedAt = null)
	{
		var now = DateTimeOffset.UtcNow;
		var started = startedAt ?? now.AddMinutes(-5);
		return new OrchestrationRunRecord
		{
			RunId = runId ?? Guid.NewGuid().ToString("N")[..12],
			OrchestrationName = orchestrationName ?? "test-orchestration",
			OrchestrationVersion = "1.0.0",
			TriggeredBy = "manual",
			TriggerId = triggerId,
			StartedAt = started,
			CompletedAt = now,
			Status = status,
			FinalContent = "Test result content",
			StepRecords = new Dictionary<string, StepRunRecord>(),
			AllStepRecords = new Dictionary<string, StepRunRecord>()
		};
	}

	// ── ApplyRetentionAsync: IsForever policy ──

	[Fact]
	public async Task ApplyRetention_ForeverPolicy_DeletesNothing()
	{
		// Arrange
		await _store.SaveRunAsync(CreateTestRecord(), cancellationToken: default);
		await _store.SaveRunAsync(CreateTestRecord(), cancellationToken: default);
		await _store.SaveRunAsync(CreateTestRecord(), cancellationToken: default);

		var policy = new RetentionPolicy(); // defaults: both null → IsForever

		// Act
		var deleted = await _store.ApplyRetentionAsync(policy);

		// Assert
		deleted.Should().Be(0);
		var runs = await _store.ListAllRunsAsync();
		runs.Should().HaveCount(3);
	}

	[Fact]
	public async Task ApplyRetention_ForeverPolicy_NullValues_DeletesNothing()
	{
		// Arrange
		await _store.SaveRunAsync(CreateTestRecord(), cancellationToken: default);

		var policy = new RetentionPolicy { MaxRunsPerOrchestration = null, MaxRunAgeDays = null };

		// Act
		var deleted = await _store.ApplyRetentionAsync(policy);

		// Assert
		deleted.Should().Be(0);
	}

	[Fact]
	public async Task ApplyRetention_ForeverPolicy_ZeroValues_DeletesNothing()
	{
		// Arrange
		await _store.SaveRunAsync(CreateTestRecord(), cancellationToken: default);

		var policy = new RetentionPolicy { MaxRunsPerOrchestration = 0, MaxRunAgeDays = 0 };

		// Act
		var deleted = await _store.ApplyRetentionAsync(policy);

		// Assert
		deleted.Should().Be(0);
	}

	// ── ApplyRetentionAsync: MaxRunsPerOrchestration ──

	[Fact]
	public async Task ApplyRetention_MaxCount_DeletesOldestBeyondLimit()
	{
		// Arrange — 5 runs, keep only 2
		var now = DateTimeOffset.UtcNow;
		for (var i = 0; i < 5; i++)
		{
			await _store.SaveRunAsync(
				CreateTestRecord(startedAt: now.AddHours(-5 + i)),
				cancellationToken: default);
		}

		var policy = new RetentionPolicy { MaxRunsPerOrchestration = 2 };

		// Act
		var deleted = await _store.ApplyRetentionAsync(policy);

		// Assert
		deleted.Should().Be(3);
		var remaining = await _store.ListAllRunsAsync();
		remaining.Should().HaveCount(2);
	}

	[Fact]
	public async Task ApplyRetention_MaxCount_KeepsNewestRuns()
	{
		// Arrange — create runs with known timestamps
		var now = DateTimeOffset.UtcNow;
		var oldestId = "oldest-run1";
		var middleId = "middle-run2";
		var newestId = "newest-run3";

		await _store.SaveRunAsync(
			CreateTestRecord(runId: oldestId, startedAt: now.AddHours(-3)),
			cancellationToken: default);
		await _store.SaveRunAsync(
			CreateTestRecord(runId: middleId, startedAt: now.AddHours(-2)),
			cancellationToken: default);
		await _store.SaveRunAsync(
			CreateTestRecord(runId: newestId, startedAt: now.AddHours(-1)),
			cancellationToken: default);

		var policy = new RetentionPolicy { MaxRunsPerOrchestration = 2 };

		// Act
		await _store.ApplyRetentionAsync(policy);

		// Assert
		var remaining = await _store.ListAllRunsAsync();
		remaining.Should().HaveCount(2);
		remaining.Select(r => r.RunId).Should().Contain(newestId);
		remaining.Select(r => r.RunId).Should().Contain(middleId);
		remaining.Select(r => r.RunId).Should().NotContain(oldestId);
	}

	[Fact]
	public async Task ApplyRetention_MaxCount_AppliesToEachOrchestrationIndependently()
	{
		// Arrange — 3 runs for orch-a, 2 runs for orch-b, keep 2
		var now = DateTimeOffset.UtcNow;
		for (var i = 0; i < 3; i++)
		{
			await _store.SaveRunAsync(
				CreateTestRecord(orchestrationName: "orch-a", startedAt: now.AddHours(-3 + i)),
				cancellationToken: default);
		}
		for (var i = 0; i < 2; i++)
		{
			await _store.SaveRunAsync(
				CreateTestRecord(orchestrationName: "orch-b", startedAt: now.AddHours(-2 + i)),
				cancellationToken: default);
		}

		var policy = new RetentionPolicy { MaxRunsPerOrchestration = 2 };

		// Act
		var deleted = await _store.ApplyRetentionAsync(policy);

		// Assert — should delete 1 from orch-a, 0 from orch-b
		deleted.Should().Be(1);
		var orchA = await _store.ListRunsAsync("orch-a");
		orchA.Should().HaveCount(2);
		var orchB = await _store.ListRunsAsync("orch-b");
		orchB.Should().HaveCount(2);
	}

	[Fact]
	public async Task ApplyRetention_MaxCount_WhenUnderLimit_DeletesNothing()
	{
		// Arrange — 2 runs, limit 5
		await _store.SaveRunAsync(CreateTestRecord(), cancellationToken: default);
		await _store.SaveRunAsync(CreateTestRecord(), cancellationToken: default);

		var policy = new RetentionPolicy { MaxRunsPerOrchestration = 5 };

		// Act
		var deleted = await _store.ApplyRetentionAsync(policy);

		// Assert
		deleted.Should().Be(0);
	}

	// ── ApplyRetentionAsync: MaxRunAgeDays ──

	[Fact]
	public async Task ApplyRetention_MaxAge_DeletesOldRuns()
	{
		// Arrange — 1 recent run, 2 old runs (90 days old)
		var now = DateTimeOffset.UtcNow;
		await _store.SaveRunAsync(
			CreateTestRecord(runId: "recent-one", startedAt: now.AddDays(-1)),
			cancellationToken: default);
		await _store.SaveRunAsync(
			CreateTestRecord(runId: "old-one", startedAt: now.AddDays(-90)),
			cancellationToken: default);
		await _store.SaveRunAsync(
			CreateTestRecord(runId: "old-two", startedAt: now.AddDays(-91)),
			cancellationToken: default);

		var policy = new RetentionPolicy { MaxRunAgeDays = 30 };

		// Act
		var deleted = await _store.ApplyRetentionAsync(policy);

		// Assert
		deleted.Should().Be(2);
		var remaining = await _store.ListAllRunsAsync();
		remaining.Should().HaveCount(1);
		remaining[0].RunId.Should().Be("recent-one");
	}

	[Fact]
	public async Task ApplyRetention_MaxAge_KeepsRunsWithinLimit()
	{
		// Arrange — all runs within the age limit
		var now = DateTimeOffset.UtcNow;
		await _store.SaveRunAsync(CreateTestRecord(startedAt: now.AddDays(-5)), cancellationToken: default);
		await _store.SaveRunAsync(CreateTestRecord(startedAt: now.AddDays(-10)), cancellationToken: default);

		var policy = new RetentionPolicy { MaxRunAgeDays = 30 };

		// Act
		var deleted = await _store.ApplyRetentionAsync(policy);

		// Assert
		deleted.Should().Be(0);
	}

	// ── ApplyRetentionAsync: Combined MaxCount + MaxAge ──

	[Fact]
	public async Task ApplyRetention_CombinedPolicy_DeletesRunsViolatingEitherRule()
	{
		// Arrange — 5 runs with varying ages
		var now = DateTimeOffset.UtcNow;
		await _store.SaveRunAsync(
			CreateTestRecord(runId: "new-1", startedAt: now.AddDays(-1)),
			cancellationToken: default);
		await _store.SaveRunAsync(
			CreateTestRecord(runId: "new-2", startedAt: now.AddDays(-2)),
			cancellationToken: default);
		await _store.SaveRunAsync(
			CreateTestRecord(runId: "new-3", startedAt: now.AddDays(-3)),
			cancellationToken: default);
		await _store.SaveRunAsync(
			CreateTestRecord(runId: "old-1", startedAt: now.AddDays(-60)),
			cancellationToken: default);
		await _store.SaveRunAsync(
			CreateTestRecord(runId: "old-2", startedAt: now.AddDays(-90)),
			cancellationToken: default);

		// Keep max 3 AND max 30 days
		var policy = new RetentionPolicy { MaxRunsPerOrchestration = 3, MaxRunAgeDays = 30 };

		// Act
		var deleted = await _store.ApplyRetentionAsync(policy);

		// Assert — old-1 and old-2 deleted by age, AND exceed count
		deleted.Should().Be(2);
		var remaining = await _store.ListAllRunsAsync();
		remaining.Should().HaveCount(3);
		remaining.Select(r => r.RunId).Should().Contain("new-1");
		remaining.Select(r => r.RunId).Should().Contain("new-2");
		remaining.Select(r => r.RunId).Should().Contain("new-3");
	}

	[Fact]
	public async Task ApplyRetention_CombinedPolicy_AgeDeletesMoreThanCount()
	{
		// Arrange — 4 runs, all old but keep 10 by count
		var now = DateTimeOffset.UtcNow;
		for (var i = 0; i < 4; i++)
		{
			await _store.SaveRunAsync(
				CreateTestRecord(startedAt: now.AddDays(-60 - i)),
				cancellationToken: default);
		}

		// Keep max 10 by count, but max 30 days by age
		var policy = new RetentionPolicy { MaxRunsPerOrchestration = 10, MaxRunAgeDays = 30 };

		// Act
		var deleted = await _store.ApplyRetentionAsync(policy);

		// Assert — all 4 deleted by age (even though under count limit)
		deleted.Should().Be(4);
		var remaining = await _store.ListAllRunsAsync();
		remaining.Should().BeEmpty();
	}

	// ── ApplyRetentionAsync: Trigger index cleanup ──

	[Fact]
	public async Task ApplyRetention_RemovesFromTriggerIndex()
	{
		// Arrange
		var now = DateTimeOffset.UtcNow;
		await _store.SaveRunAsync(
			CreateTestRecord(runId: "keep-it", triggerId: "trigger-x", startedAt: now.AddHours(-1)),
			cancellationToken: default);
		await _store.SaveRunAsync(
			CreateTestRecord(runId: "delete-it", triggerId: "trigger-x", startedAt: now.AddDays(-60)),
			cancellationToken: default);

		var policy = new RetentionPolicy { MaxRunAgeDays = 30 };

		// Act
		await _store.ApplyRetentionAsync(policy);

		// Assert — trigger index should only have the kept run
		var triggerRuns = await _store.ListRunsByTriggerAsync("trigger-x");
		triggerRuns.Should().HaveCount(1);
		triggerRuns[0].RunId.Should().Be("keep-it");
	}

	// ── ApplyRetentionAsync: Disk cleanup ──

	[Fact]
	public async Task ApplyRetention_DeletesFoldersFromDisk()
	{
		// Arrange
		var now = DateTimeOffset.UtcNow;
		await _store.SaveRunAsync(
			CreateTestRecord(startedAt: now.AddDays(-60)),
			cancellationToken: default);

		// Verify folder exists
		var dirs = Directory.GetDirectories(_store.RootPath);
		dirs.Should().HaveCount(1);
		var orchDir = dirs[0];
		var runDirs = Directory.GetDirectories(orchDir);
		runDirs.Should().HaveCount(1);

		var policy = new RetentionPolicy { MaxRunAgeDays = 30 };

		// Act
		await _store.ApplyRetentionAsync(policy);

		// Assert — run folder should be deleted
		Directory.GetDirectories(orchDir).Should().BeEmpty();
	}

	// ── ApplyRetentionAsync: Idempotent ──

	[Fact]
	public async Task ApplyRetention_CalledTwice_SecondCallDeletesNothing()
	{
		// Arrange
		var now = DateTimeOffset.UtcNow;
		for (var i = 0; i < 5; i++)
		{
			await _store.SaveRunAsync(
				CreateTestRecord(startedAt: now.AddHours(-5 + i)),
				cancellationToken: default);
		}

		var policy = new RetentionPolicy { MaxRunsPerOrchestration = 2 };

		// Act
		var first = await _store.ApplyRetentionAsync(policy);
		var second = await _store.ApplyRetentionAsync(policy);

		// Assert
		first.Should().Be(3);
		second.Should().Be(0);
	}

	// ── ApplyRetentionAsync: Empty store ──

	[Fact]
	public async Task ApplyRetention_EmptyStore_ReturnsZero()
	{
		var policy = new RetentionPolicy { MaxRunsPerOrchestration = 5, MaxRunAgeDays = 30 };

		var deleted = await _store.ApplyRetentionAsync(policy);

		deleted.Should().Be(0);
	}

	// ── ApplyRetentionAsync: MaxCount = 1 ──

	[Fact]
	public async Task ApplyRetention_MaxCountOne_KeepsOnlyNewest()
	{
		// Arrange
		var now = DateTimeOffset.UtcNow;
		await _store.SaveRunAsync(
			CreateTestRecord(runId: "oldest", startedAt: now.AddHours(-3)),
			cancellationToken: default);
		await _store.SaveRunAsync(
			CreateTestRecord(runId: "newest", startedAt: now.AddHours(-1)),
			cancellationToken: default);

		var policy = new RetentionPolicy { MaxRunsPerOrchestration = 1 };

		// Act
		await _store.ApplyRetentionAsync(policy);

		// Assert
		var remaining = await _store.ListAllRunsAsync();
		remaining.Should().HaveCount(1);
		remaining[0].RunId.Should().Be("newest");
	}

	// ── RunRetentionService tests ──

	[Fact]
	public async Task RetentionService_RunsSweepAndDeletesOldRuns()
	{
		// Arrange — save some old runs
		var now = DateTimeOffset.UtcNow;
		await _store.SaveRunAsync(
			CreateTestRecord(runId: "keep", startedAt: now.AddDays(-1)),
			cancellationToken: default);
		await _store.SaveRunAsync(
			CreateTestRecord(runId: "delete", startedAt: now.AddDays(-60)),
			cancellationToken: default);

		var policy = new RetentionPolicy { MaxRunAgeDays = 30 };
		var logger = Substitute.For<ILogger<RunRetentionService>>();

		// Use a very short interval and override the initial delay
		var service = new RunRetentionService(_store, policy, logger, TimeSpan.FromMilliseconds(50));

		using var cts = new CancellationTokenSource();

		// Act — start the service and let it run one sweep
		var task = service.StartAsync(cts.Token);
		await Task.Delay(TimeSpan.FromSeconds(8)); // enough for the initial 5s delay + sweep
		await cts.CancelAsync();
		await service.StopAsync(CancellationToken.None);

		// Assert
		var remaining = await _store.ListAllRunsAsync();
		remaining.Should().HaveCount(1);
		remaining[0].RunId.Should().Be("keep");
	}

	[Fact]
	public async Task RetentionService_StopsGracefullyOnCancellation()
	{
		// Arrange
		var policy = new RetentionPolicy { MaxRunsPerOrchestration = 5 };
		var logger = Substitute.For<ILogger<RunRetentionService>>();
		var service = new RunRetentionService(_store, policy, logger, TimeSpan.FromMilliseconds(50));

		using var cts = new CancellationTokenSource();

		// Act — start and immediately stop
		await service.StartAsync(cts.Token);
		await cts.CancelAsync();

		// Assert — should not throw
		var act = () => service.StopAsync(CancellationToken.None);
		await act.Should().NotThrowAsync();
	}

	[Fact]
	public async Task RetentionService_SweepsMultipleTimes()
	{
		// Arrange — start with runs that exceed the limit
		var now = DateTimeOffset.UtcNow;
		for (var i = 0; i < 4; i++)
		{
			await _store.SaveRunAsync(
				CreateTestRecord(startedAt: now.AddMinutes(-10 + i)),
				cancellationToken: default);
		}

		var policy = new RetentionPolicy { MaxRunsPerOrchestration = 2 };
		var logger = Substitute.For<ILogger<RunRetentionService>>();
		var service = new RunRetentionService(_store, policy, logger, TimeSpan.FromMilliseconds(100));

		using var cts = new CancellationTokenSource();

		// Act — run initial sweep
		await service.StartAsync(cts.Token);
		await Task.Delay(TimeSpan.FromSeconds(8)); // initial 5s delay + sweep

		// Verify first sweep
		var afterFirst = await _store.ListAllRunsAsync();
		afterFirst.Should().HaveCount(2);

		// Add more runs to exceed the limit again
		await _store.SaveRunAsync(CreateTestRecord(startedAt: now.AddMinutes(1)), cancellationToken: default);
		await _store.SaveRunAsync(CreateTestRecord(startedAt: now.AddMinutes(2)), cancellationToken: default);
		await _store.SaveRunAsync(CreateTestRecord(startedAt: now.AddMinutes(3)), cancellationToken: default);

		// Wait for another sweep
		await Task.Delay(TimeSpan.FromMilliseconds(300));

		await cts.CancelAsync();
		await service.StopAsync(CancellationToken.None);

		// Assert — should have only 2 remaining after the second sweep
		var remaining = await _store.ListAllRunsAsync();
		remaining.Should().HaveCount(2);
	}
}
