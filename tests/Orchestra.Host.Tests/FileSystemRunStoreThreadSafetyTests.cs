using FluentAssertions;
using Orchestra.Engine;
using Orchestra.Host.Persistence;
using Xunit;

namespace Orchestra.Host.Tests;

/// <summary>
/// Tests for FileSystemRunStore thread safety — concurrent reads, writes, and deletes.
/// Validates that the _indexWriteLock protects inner List&lt;RunIndex&gt; mutations correctly.
/// </summary>
public class FileSystemRunStoreThreadSafetyTests : IDisposable
{
	private readonly string _tempDir;
	private readonly FileSystemRunStore _store;
	private IRunStore Store => _store; // Use IRunStore to avoid ambiguous SaveRunAsync overloads

	public FileSystemRunStoreThreadSafetyTests()
	{
		_tempDir = Path.Combine(Path.GetTempPath(), $"orchestra-runstore-thread-tests-{Guid.NewGuid():N}");
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

	private static OrchestrationRunRecord CreateTestRecord(
		string? runId = null,
		string orchestrationName = "test-orchestration",
		string triggeredBy = "manual",
		string? triggerId = null)
	{
		var id = runId ?? Guid.NewGuid().ToString("N")[..12];
		var now = DateTimeOffset.UtcNow;
		return new OrchestrationRunRecord
		{
			RunId = id,
			OrchestrationName = orchestrationName,
			StartedAt = now.AddSeconds(-5),
			CompletedAt = now,
			Status = ExecutionStatus.Succeeded,
			TriggeredBy = triggeredBy,
			TriggerId = triggerId,
			FinalContent = $"Result for {id}",
			StepRecords = new Dictionary<string, StepRunRecord>
			{
				["step-1"] = new StepRunRecord
				{
					StepName = "step-1",
					Status = ExecutionStatus.Succeeded,
					StartedAt = now.AddSeconds(-5),
					CompletedAt = now,
					Content = "output",
				}
			},
			AllStepRecords = new Dictionary<string, StepRunRecord>
			{
				["step-1"] = new StepRunRecord
				{
					StepName = "step-1",
					Status = ExecutionStatus.Succeeded,
					StartedAt = now.AddSeconds(-5),
					CompletedAt = now,
					Content = "output",
				}
			},
		};
	}

	// ─── Basic CRUD ────────────────────────────────────────────────

	[Fact]
	public async Task SaveRunAsync_CreatesRunFiles()
	{
		var record = CreateTestRecord(runId: "basic-save");

		await Store.SaveRunAsync(record);

		var runs = await Store.ListRunsAsync("test-orchestration");
		runs.Should().HaveCount(1);
		runs[0].RunId.Should().Be("basic-save");
	}

	[Fact]
	public async Task GetRunAsync_ReturnsCorrectRecord()
	{
		var record = CreateTestRecord(runId: "get-test");
		await Store.SaveRunAsync(record);

		var loaded = await _store.GetRunAsync("test-orchestration", "get-test");

		loaded.Should().NotBeNull();
		loaded!.RunId.Should().Be("get-test");
		loaded.OrchestrationName.Should().Be("test-orchestration");
	}

	[Fact]
	public async Task GetRunAsync_NonexistentOrchestration_ReturnsNull()
	{
		var result = await _store.GetRunAsync("nonexistent", "any-id");
		result.Should().BeNull();
	}

	[Fact]
	public async Task GetRunAsync_NonexistentRunId_ReturnsNull()
	{
		var record = CreateTestRecord(runId: "exists");
		await Store.SaveRunAsync(record);

		var result = await _store.GetRunAsync("test-orchestration", "does-not-exist");
		result.Should().BeNull();
	}

	[Fact]
	public async Task DeleteRunAsync_RemovesFromIndex()
	{
		var record = CreateTestRecord(runId: "delete-me");
		await Store.SaveRunAsync(record);

		var deleted = await _store.DeleteRunAsync("test-orchestration", "delete-me");
		deleted.Should().BeTrue();

		var loaded = await _store.GetRunAsync("test-orchestration", "delete-me");
		loaded.Should().BeNull();
	}

	[Fact]
	public async Task DeleteRunAsync_NonexistentRun_ReturnsFalse()
	{
		var result = await _store.DeleteRunAsync("test-orchestration", "nonexistent");
		result.Should().BeFalse();
	}

	[Fact]
	public async Task ListAllRunsAsync_ReturnsAllOrchestrations()
	{
		await Store.SaveRunAsync(CreateTestRecord(runId: "r1", orchestrationName: "orch-a"));
		await Store.SaveRunAsync(CreateTestRecord(runId: "r2", orchestrationName: "orch-b"));
		await Store.SaveRunAsync(CreateTestRecord(runId: "r3", orchestrationName: "orch-a"));

		var all = await _store.ListAllRunsAsync();
		all.Should().HaveCount(3);
	}

	[Fact]
	public async Task ListAllRunsAsync_WithLimit_RespectsLimit()
	{
		for (var i = 0; i < 5; i++)
			await Store.SaveRunAsync(CreateTestRecord(runId: $"limited-{i}"));

		var limited = await _store.ListAllRunsAsync(limit: 3);
		limited.Should().HaveCount(3);
	}

	[Fact]
	public async Task ListRunsByTriggerAsync_FiltersByTriggerId()
	{
		await Store.SaveRunAsync(CreateTestRecord(runId: "t1", triggerId: "trigger-A"));
		await Store.SaveRunAsync(CreateTestRecord(runId: "t2", triggerId: "trigger-B"));
		await Store.SaveRunAsync(CreateTestRecord(runId: "t3", triggerId: "trigger-A"));

		var triggerA = await _store.ListRunsByTriggerAsync("trigger-A");
		triggerA.Should().HaveCount(2);
		triggerA.Should().AllSatisfy(r => r.TriggerId.Should().Be("trigger-A"));
	}

	[Fact]
	public async Task ListRunsByTriggerAsync_NonexistentTrigger_ReturnsEmpty()
	{
		var result = await _store.ListRunsByTriggerAsync("nonexistent");
		result.Should().BeEmpty();
	}

	[Fact]
	public async Task GetRunSummariesAsync_ReturnsLightweightIndexEntries()
	{
		await Store.SaveRunAsync(CreateTestRecord(runId: "s1"));
		await Store.SaveRunAsync(CreateTestRecord(runId: "s2"));

		var summaries = await _store.GetRunSummariesAsync();
		summaries.Should().HaveCount(2);
		summaries.Should().AllSatisfy(s => s.OrchestrationName.Should().Be("test-orchestration"));
	}

	[Fact]
	public async Task GetRunSummariesAsync_ByOrchestration_FiltersCorrectly()
	{
		await Store.SaveRunAsync(CreateTestRecord(runId: "sa1", orchestrationName: "orch-x"));
		await Store.SaveRunAsync(CreateTestRecord(runId: "sa2", orchestrationName: "orch-y"));

		var summaries = await _store.GetRunSummariesAsync("orch-x");
		summaries.Should().HaveCount(1);
		summaries[0].RunId.Should().Be("sa1");
	}

	[Fact]
	public async Task GetRunSummariesAsync_NonexistentOrchestration_ReturnsEmpty()
	{
		var summaries = await _store.GetRunSummariesAsync("nonexistent");
		summaries.Should().BeEmpty();
	}

	// ─── Concurrent writes ─────────────────────────────────────────

	[Fact]
	public async Task ConcurrentSaves_SameOrchestration_AllRecordsPresent()
	{
		const int concurrency = 20;
		var tasks = Enumerable.Range(0, concurrency)
			.Select(i => Store.SaveRunAsync(
				CreateTestRecord(runId: $"concurrent-{i}", orchestrationName: "shared-orch")))
			.ToArray();

		await Task.WhenAll(tasks);

		var runs = await Store.ListRunsAsync("shared-orch");
		runs.Should().HaveCount(concurrency);
		runs.Select(r => r.RunId).Distinct().Should().HaveCount(concurrency);
	}

	[Fact]
	public async Task ConcurrentSaves_DifferentOrchestrations_AllRecordsPresent()
	{
		const int concurrency = 20;
		var tasks = Enumerable.Range(0, concurrency)
			.Select(i => Store.SaveRunAsync(
				CreateTestRecord(runId: $"diff-{i}", orchestrationName: $"orch-{i % 5}")))
			.ToArray();

		await Task.WhenAll(tasks);

		var all = await _store.ListAllRunsAsync();
		all.Should().HaveCount(concurrency);
	}

	[Fact]
	public async Task ConcurrentSaves_WithTriggerIds_AllIndexedCorrectly()
	{
		const int concurrency = 20;
		var tasks = Enumerable.Range(0, concurrency)
			.Select(i => Store.SaveRunAsync(
				CreateTestRecord(
					runId: $"trig-{i}",
					orchestrationName: "triggered-orch",
					triggerId: $"trigger-{i % 3}")))
			.ToArray();

		await Task.WhenAll(tasks);

		// Each trigger group should have correct count
		var trigger0 = await _store.ListRunsByTriggerAsync("trigger-0");
		var trigger1 = await _store.ListRunsByTriggerAsync("trigger-1");
		var trigger2 = await _store.ListRunsByTriggerAsync("trigger-2");

		// 20 items, mod 3: trigger-0 gets indices 0,3,6,9,12,15,18 (7); trigger-1 gets 1,4,7,10,13,16,19 (7); trigger-2 gets 2,5,8,11,14,17 (6)
		(trigger0.Count + trigger1.Count + trigger2.Count).Should().Be(concurrency);
	}

	// ─── Concurrent reads and writes ───────────────────────────────

	[Fact]
	public async Task ConcurrentReadsAndWrites_DoNotThrow()
	{
		// Seed some initial data
		for (var i = 0; i < 5; i++)
			await Store.SaveRunAsync(CreateTestRecord(runId: $"seed-{i}", orchestrationName: "rw-orch"));

		const int operations = 50;
		var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
		var tasks = new List<Task>();

		// Mix of writes, reads, and summaries
		for (var i = 0; i < operations; i++)
		{
			var idx = i;
			if (idx % 3 == 0)
			{
				tasks.Add(Store.SaveRunAsync(
					CreateTestRecord(runId: $"rw-{idx}", orchestrationName: "rw-orch"), cts.Token));
			}
			else if (idx % 3 == 1)
			{
				tasks.Add(Store.ListRunsAsync("rw-orch", cancellationToken: cts.Token));
			}
			else
			{
				tasks.Add(_store.GetRunSummariesAsync("rw-orch", cancellationToken: cts.Token));
			}
		}

		// Should complete without deadlocks or exceptions
		var act = () => Task.WhenAll(tasks);
		await act.Should().NotThrowAsync();
	}

	[Fact]
	public async Task ConcurrentListAllRuns_WhileWriting_DoesNotThrow()
	{
		const int writers = 10;
		const int readers = 10;
		var barrier = new TaskCompletionSource();
		var tasks = new List<Task>();

		// Writers wait for barrier, then write
		for (var i = 0; i < writers; i++)
		{
			var idx = i;
			tasks.Add(Task.Run(async () =>
			{
				await barrier.Task;
				await Store.SaveRunAsync(
					CreateTestRecord(runId: $"barrier-w-{idx}", orchestrationName: "barrier-orch"));
			}));
		}

		// Readers wait for barrier, then read
		for (var i = 0; i < readers; i++)
		{
			tasks.Add(Task.Run(async () =>
			{
				await barrier.Task;
				await _store.ListAllRunsAsync();
			}));
		}

		// Release all tasks simultaneously
		barrier.SetResult();
		var act = () => Task.WhenAll(tasks);
		await act.Should().NotThrowAsync();
	}

	// ─── Concurrent deletes ────────────────────────────────────────

	[Fact]
	public async Task ConcurrentDeletes_SameRun_OnlyOneSucceeds()
	{
		await Store.SaveRunAsync(CreateTestRecord(runId: "del-once", orchestrationName: "del-orch"));

		const int concurrency = 10;
		var results = await Task.WhenAll(
			Enumerable.Range(0, concurrency)
				.Select(_ => _store.DeleteRunAsync("del-orch", "del-once")));

		// Exactly one should succeed, the rest should return false
		results.Count(r => r).Should().Be(1);

		// Run should be gone
		var loaded = await _store.GetRunAsync("del-orch", "del-once");
		loaded.Should().BeNull();
	}

	[Fact]
	public async Task ConcurrentDeletesAndWrites_DifferentRuns_AllComplete()
	{
		// Seed runs for deletion
		for (var i = 0; i < 10; i++)
			await Store.SaveRunAsync(CreateTestRecord(runId: $"dw-{i}", orchestrationName: "dw-orch"));

		var tasks = new List<Task>();

		// Delete even-numbered runs
		for (var i = 0; i < 10; i += 2)
		{
			var idx = i;
			tasks.Add(_store.DeleteRunAsync("dw-orch", $"dw-{idx}"));
		}

		// Write new runs concurrently
		for (var i = 10; i < 20; i++)
		{
			var idx = i;
			tasks.Add(Store.SaveRunAsync(
				CreateTestRecord(runId: $"dw-{idx}", orchestrationName: "dw-orch")));
		}

		await Task.WhenAll(tasks);

		// Should have odd originals (5) + new writes (10) = 15
		var runs = await Store.ListRunsAsync("dw-orch");
		runs.Should().HaveCount(15);
	}

	// ─── Delete with trigger index consistency ─────────────────────

	[Fact]
	public async Task DeleteRun_WithTriggerId_RemovesFromBothIndices()
	{
		await Store.SaveRunAsync(CreateTestRecord(
			runId: "trig-del", orchestrationName: "trig-orch", triggerId: "my-trigger"));
		await Store.SaveRunAsync(CreateTestRecord(
			runId: "trig-keep", orchestrationName: "trig-orch", triggerId: "my-trigger"));

		await _store.DeleteRunAsync("trig-orch", "trig-del");

		// Should be gone from orchestration index
		var orchRuns = await Store.ListRunsAsync("trig-orch");
		orchRuns.Should().HaveCount(1);
		orchRuns[0].RunId.Should().Be("trig-keep");

		// Should be gone from trigger index
		var trigRuns = await _store.ListRunsByTriggerAsync("my-trigger");
		trigRuns.Should().HaveCount(1);
		trigRuns[0].RunId.Should().Be("trig-keep");
	}

	// ─── Index loading from disk ───────────────────────────────────

	[Fact]
	public async Task NewStoreInstance_LoadsExistingRunsFromDisk()
	{
		// Save some runs with the first store instance
		await Store.SaveRunAsync(CreateTestRecord(runId: "persist-1", orchestrationName: "persist-orch"));
		await Store.SaveRunAsync(CreateTestRecord(runId: "persist-2", orchestrationName: "persist-orch"));

		// Create a brand new store instance pointing at the same directory
		var store2 = new FileSystemRunStore(_tempDir);

		var runs = await store2.ListRunsAsync("persist-orch");
		runs.Should().HaveCount(2);
		runs.Select(r => r.RunId).Should().Contain("persist-1").And.Contain("persist-2");
	}

	[Fact]
	public async Task ConcurrentIndexLoad_OnlyLoadsOnce()
	{
		// Seed data first
		await Store.SaveRunAsync(CreateTestRecord(runId: "load-test", orchestrationName: "load-orch"));

		// Create a new store (not yet loaded)
		var freshStore = new FileSystemRunStore(_tempDir);

		// Multiple concurrent reads should all trigger index load, but only once
		var tasks = Enumerable.Range(0, 20)
			.Select(_ => freshStore.ListRunsAsync("load-orch"))
			.ToArray();

		var results = await Task.WhenAll(tasks);

		// All should return the same result
		results.Should().AllSatisfy(r => r.Should().HaveCount(1));
	}

	// ─── Error information in RunIndex ─────────────────────────────

	[Fact]
	public async Task RunIndex_FailedRun_CarriesErrorMessage()
	{
		var now = DateTimeOffset.UtcNow;
		var record = new OrchestrationRunRecord
		{
			RunId = "failed-run",
			OrchestrationName = "error-orch",
			StartedAt = now.AddSeconds(-5),
			CompletedAt = now,
			Status = ExecutionStatus.Failed,
			FinalContent = "",
			StepRecords = new Dictionary<string, StepRunRecord>
			{
				["step-1"] = new StepRunRecord
				{
					StepName = "step-1",
					Status = ExecutionStatus.Failed,
					StartedAt = now.AddSeconds(-3),
					CompletedAt = now,
					Content = "",
					ErrorMessage = "Connection timeout after 30s"
				}
			},
			AllStepRecords = new Dictionary<string, StepRunRecord>
			{
				["step-1"] = new StepRunRecord
				{
					StepName = "step-1",
					Status = ExecutionStatus.Failed,
					StartedAt = now.AddSeconds(-3),
					CompletedAt = now,
					Content = "",
					ErrorMessage = "Connection timeout after 30s"
				}
			}
		};

		await Store.SaveRunAsync(record);

		var summaries = await _store.GetRunSummariesAsync("error-orch");
		summaries.Should().HaveCount(1);
		summaries[0].Status.Should().Be(ExecutionStatus.Failed);
		summaries[0].FailedStepName.Should().Be("step-1");
		summaries[0].ErrorMessage.Should().Be("Connection timeout after 30s");
	}

	[Fact]
	public async Task RunIndex_SucceededRun_HasNoErrorMessage()
	{
		var record = CreateTestRecord(runId: "success-run");
		await Store.SaveRunAsync(record);

		var summaries = await _store.GetRunSummariesAsync("test-orchestration");
		summaries.Should().HaveCount(1);
		summaries[0].FailedStepName.Should().BeNull();
		summaries[0].ErrorMessage.Should().BeNull();
	}

	[Fact]
	public async Task RunIndex_FailedRun_UsesFirstFailedStep()
	{
		var now = DateTimeOffset.UtcNow;
		var record = new OrchestrationRunRecord
		{
			RunId = "multi-fail",
			OrchestrationName = "multi-fail-orch",
			StartedAt = now.AddSeconds(-10),
			CompletedAt = now,
			Status = ExecutionStatus.Failed,
			FinalContent = "",
			StepRecords = new Dictionary<string, StepRunRecord>
			{
				["step-1"] = new StepRunRecord
				{
					StepName = "step-1",
					Status = ExecutionStatus.Succeeded,
					StartedAt = now.AddSeconds(-10),
					CompletedAt = now.AddSeconds(-7),
					Content = "ok"
				},
				["step-2"] = new StepRunRecord
				{
					StepName = "step-2",
					Status = ExecutionStatus.Failed,
					StartedAt = now.AddSeconds(-7),
					CompletedAt = now.AddSeconds(-3),
					Content = "",
					ErrorMessage = "First error"
				},
				["step-3"] = new StepRunRecord
				{
					StepName = "step-3",
					Status = ExecutionStatus.Failed,
					StartedAt = now.AddSeconds(-3),
					CompletedAt = now,
					Content = "",
					ErrorMessage = "Second error"
				}
			},
			AllStepRecords = new Dictionary<string, StepRunRecord>
			{
				["step-1"] = new StepRunRecord
				{
					StepName = "step-1",
					Status = ExecutionStatus.Succeeded,
					StartedAt = now.AddSeconds(-10),
					CompletedAt = now.AddSeconds(-7),
					Content = "ok"
				},
				["step-2"] = new StepRunRecord
				{
					StepName = "step-2",
					Status = ExecutionStatus.Failed,
					StartedAt = now.AddSeconds(-7),
					CompletedAt = now.AddSeconds(-3),
					Content = "",
					ErrorMessage = "First error"
				},
				["step-3"] = new StepRunRecord
				{
					StepName = "step-3",
					Status = ExecutionStatus.Failed,
					StartedAt = now.AddSeconds(-3),
					CompletedAt = now,
					Content = "",
					ErrorMessage = "Second error"
				}
			}
		};

		await Store.SaveRunAsync(record);

		var summaries = await _store.GetRunSummariesAsync("multi-fail-orch");
		summaries.Should().HaveCount(1);
		// Should use the first failed step chronologically
		summaries[0].FailedStepName.Should().Be("step-2");
		summaries[0].ErrorMessage.Should().Be("First error");
	}

	[Fact]
	public async Task RunIndex_FailedRun_PersistsErrorAcrossReload()
	{
		var now = DateTimeOffset.UtcNow;
		var record = new OrchestrationRunRecord
		{
			RunId = "persist-error",
			OrchestrationName = "persist-err-orch",
			StartedAt = now.AddSeconds(-5),
			CompletedAt = now,
			Status = ExecutionStatus.Failed,
			FinalContent = "",
			StepRecords = new Dictionary<string, StepRunRecord>
			{
				["analyze"] = new StepRunRecord
				{
					StepName = "analyze",
					Status = ExecutionStatus.Failed,
					StartedAt = now.AddSeconds(-3),
					CompletedAt = now,
					Content = "",
					ErrorMessage = "Rate limit exceeded"
				}
			},
			AllStepRecords = new Dictionary<string, StepRunRecord>
			{
				["analyze"] = new StepRunRecord
				{
					StepName = "analyze",
					Status = ExecutionStatus.Failed,
					StartedAt = now.AddSeconds(-3),
					CompletedAt = now,
					Content = "",
					ErrorMessage = "Rate limit exceeded"
				}
			}
		};

		await Store.SaveRunAsync(record);

		// Create a new store instance to force re-loading from disk
		var freshStore = new FileSystemRunStore(_tempDir);
		var summaries = await freshStore.GetRunSummariesAsync("persist-err-orch");
		summaries.Should().HaveCount(1);
		summaries[0].FailedStepName.Should().Be("analyze");
		summaries[0].ErrorMessage.Should().Be("Rate limit exceeded");
	}
}
