using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Orchestra.Engine;
using Orchestra.Host.Hosting;
using Orchestra.Host.Persistence;
using Xunit;

namespace Orchestra.Host.Tests;

/// <summary>
/// Tests for the SQLite-backed run index: reconciliation against the filesystem, persistence
/// across process lifetimes, and recovery when the database is missing or unusable.
/// </summary>
/// <remarks>
/// The index is a derived cache over write-once run folders. The properties that matter are that
/// it always converges on what is actually on disk, and that losing it is never worse than a
/// rebuild.
/// </remarks>
public class SqliteRunIndexTests : IDisposable
{
	private readonly string _dataPath;
	private readonly List<FileSystemRunStore> _stores = [];

	public SqliteRunIndexTests()
	{
		_dataPath = Path.Combine(Path.GetTempPath(), $"orchestra-sqlite-index-{Guid.NewGuid():N}");
		Directory.CreateDirectory(_dataPath);
	}

	public void Dispose()
	{
		foreach (var store in _stores)
			store.Dispose();

		if (Directory.Exists(_dataPath))
		{
			try { Directory.Delete(_dataPath, recursive: true); }
			catch { /* best-effort cleanup */ }
		}
		GC.SuppressFinalize(this);
	}

	private string IndexDbPath => Path.Combine(_dataPath, "executions", ".index.db");

	/// <summary>
	/// Creates a store and tracks it for disposal. The store holds an open SQLite handle, so a
	/// test that replaces the database must dispose the previous store first.
	/// </summary>
	private FileSystemRunStore NewStore()
	{
		var store = new FileSystemRunStore(_dataPath, NullLogger<FileSystemRunStore>.Instance);
		_stores.Add(store);
		return store;
	}

	private static OrchestrationRunRecord Record(
		string runId,
		string orchestration = "test-orch",
		ExecutionStatus status = ExecutionStatus.Succeeded,
		DateTimeOffset? startedAt = null,
		string? triggerId = null,
		string? parentExecutionId = null,
		string? rootExecutionId = null,
		Dictionary<string, StepRunRecord>? steps = null)
	{
		var started = startedAt ?? DateTimeOffset.UtcNow.AddMinutes(-5);
		steps ??= [];
		return new OrchestrationRunRecord
		{
			RunId = runId,
			OrchestrationName = orchestration,
			OrchestrationVersion = "1.0.0",
			TriggeredBy = "manual",
			TriggerId = triggerId,
			StartedAt = started,
			CompletedAt = started.AddMinutes(1),
			Status = status,
			FinalContent = "result",
			HookExecutions = [],
			ParentExecutionId = parentExecutionId,
			RootExecutionId = rootExecutionId,
			StepRecords = steps,
			AllStepRecords = steps,
		};
	}

	// ── Persistence across instances ──

	[Fact]
	public async Task Index_SurvivesAcrossStoreInstances()
	{
		var store = NewStore();
		await store.SaveRunAsync(Record("run1"), cancellationToken: default);

		File.Exists(IndexDbPath).Should().BeTrue("the index is written beside the executions it describes");

		var reopened = NewStore();
		var summaries = await reopened.GetRunSummariesAsync();

		summaries.Should().ContainSingle(s => s.RunId == "run1");
	}

	[Fact]
	public async Task Reconcile_PicksUpRunFoldersWrittenByAnotherProcess()
	{
		// First store writes the run and the index.
		var first = NewStore();
		await first.SaveRunAsync(Record("run1"), cancellationToken: default);

		// Simulate a run appearing without this index knowing: drop the database entirely.
		first.Dispose();
		File.Delete(IndexDbPath);

		var second = NewStore();
		var summaries = await second.GetRunSummariesAsync();

		summaries.Should().ContainSingle(s => s.RunId == "run1",
			"a missing index must be rebuilt from the run folders");
	}

	[Fact]
	public async Task Reconcile_DropsRowsWhoseFolderIsGone()
	{
		var store = NewStore();
		await store.SaveRunAsync(Record("keeper"), cancellationToken: default);
		await store.SaveRunAsync(Record("vanisher"), cancellationToken: default);

		var vanisher = (await store.GetRunSummariesAsync()).Single(s => s.RunId == "vanisher");

		// Delete the folder behind the store's back, as an external cleanup would.
		Directory.Delete(vanisher.FolderPath, recursive: true);

		var reopened = NewStore();
		var summaries = await reopened.GetRunSummariesAsync();

		summaries.Select(s => s.RunId).Should().BeEquivalentTo(["keeper"]);
	}

	[Fact]
	public async Task Reconcile_IsIncremental_ExistingRowsAreNotRebuilt()
	{
		var store = NewStore();
		await store.SaveRunAsync(Record("run1"), cancellationToken: default);

		var indexed = (await store.GetRunSummariesAsync()).Single();

		// Corrupt the on-disk run.json. A reconciling store must NOT re-read it, because the
		// folder is already indexed -- run folders are write-once, so the row cannot be stale.
		await File.WriteAllTextAsync(Path.Combine(indexed.FolderPath, "run.json"), "{ corrupt");

		var reopened = NewStore();
		var summaries = await reopened.GetRunSummariesAsync();

		summaries.Should().ContainSingle(s => s.RunId == "run1",
			"an already-indexed folder is not re-projected");
	}

	// ── Recovery ──

	[Fact]
	public async Task CorruptDatabase_IsRebuiltFromRunFolders()
	{
		var store = NewStore();
		await store.SaveRunAsync(Record("run1"), cancellationToken: default);
		store.Dispose();

		// Garbage where the database was.
		await File.WriteAllTextAsync(IndexDbPath, "this is definitely not a sqlite file");

		Func<Task> reopen = async () =>
		{
			var reopened = NewStore();
			var summaries = await reopened.GetRunSummariesAsync();
			summaries.Should().ContainSingle(s => s.RunId == "run1");
		};

		await reopen.Should().NotThrowAsync("an unusable index must be discarded and rebuilt, never fatal");
	}

	[Fact]
	public async Task DeletingTheIndexFile_LosesNothing()
	{
		var store = NewStore();
		await store.SaveRunAsync(Record("run1"), cancellationToken: default);
		await store.SaveRunAsync(Record("run2"), cancellationToken: default);

		store.Dispose();
		File.Delete(IndexDbPath);

		var reopened = NewStore();
		var summaries = await reopened.GetRunSummariesAsync();

		summaries.Select(s => s.RunId).Should().BeEquivalentTo(["run1", "run2"],
			"the index is derived; the run folders are the source of truth");
	}

	// ── The duplicate-row bug the in-memory index had ──

	[Fact]
	public async Task SavingTheSameRecordTwice_DoesNotDuplicateTheRow()
	{
		// The in-memory index appended unconditionally, so a re-save produced a duplicate history
		// entry until the process restarted. Keying on folder path makes the write idempotent.
		var store = NewStore();
		var record = Record("run1");

		await store.SaveRunAsync(record, cancellationToken: default);
		await store.SaveRunAsync(record, cancellationToken: default);

		(await store.GetRunSummariesAsync()).Should().ContainSingle(s => s.RunId == "run1");
	}

	// ── Query semantics preserved ──

	[Fact]
	public async Task Summaries_AreNewestFirst()
	{
		var store = NewStore();
		var t0 = DateTimeOffset.UtcNow.AddHours(-3);
		await store.SaveRunAsync(Record("oldest", startedAt: t0), cancellationToken: default);
		await store.SaveRunAsync(Record("newest", startedAt: t0.AddHours(2)), cancellationToken: default);
		await store.SaveRunAsync(Record("middle", startedAt: t0.AddHours(1)), cancellationToken: default);

		var summaries = await store.GetRunSummariesAsync();

		summaries.Select(s => s.RunId).Should().ContainInOrder("newest", "middle", "oldest");
	}

	[Fact]
	public async Task Summaries_RespectLimit()
	{
		var store = NewStore();
		var t0 = DateTimeOffset.UtcNow.AddHours(-5);
		for (var i = 0; i < 5; i++)
			await store.SaveRunAsync(Record($"run{i}", startedAt: t0.AddMinutes(i)), cancellationToken: default);

		(await store.GetRunSummariesAsync(limit: 2)).Should().HaveCount(2);
	}

	[Fact]
	public async Task FindRunById_IsCaseInsensitive()
	{
		var store = NewStore();
		await store.SaveRunAsync(Record("AbCdEf"), cancellationToken: default);

		(await store.FindRunByIdAsync("abcdef")).Should().NotBeNull();
	}

	[Fact]
	public async Task ListByTrigger_ReturnsOnlyThatTriggersRuns()
	{
		var store = NewStore();
		await store.SaveRunAsync(Record("a", triggerId: "trig-1"), cancellationToken: default);
		await store.SaveRunAsync(Record("b", triggerId: "trig-2"), cancellationToken: default);
		await store.SaveRunAsync(Record("c"), cancellationToken: default);

		var runs = await store.ListRunsByTriggerAsync("trig-1");

		runs.Select(r => r.RunId).Should().BeEquivalentTo(["a"]);
	}

	[Fact]
	public async Task FindChildRuns_ScopesToParentAndRoot_NewestFirst()
	{
		var store = NewStore();
		var t0 = DateTimeOffset.UtcNow.AddHours(-2);
		await store.SaveRunAsync(Record("kid-1", parentExecutionId: "root", rootExecutionId: "root", startedAt: t0), cancellationToken: default);
		await store.SaveRunAsync(Record("kid-2", parentExecutionId: "root", rootExecutionId: "root", startedAt: t0.AddMinutes(5)), cancellationToken: default);
		await store.SaveRunAsync(Record("grandkid", parentExecutionId: "kid-1", rootExecutionId: "root", startedAt: t0.AddMinutes(10)), cancellationToken: default);
		await store.SaveRunAsync(Record("unrelated"), cancellationToken: default);

		var direct = await store.FindChildRunsAsync("root", null, null);
		var subtree = await store.FindChildRunsAsync(null, "root", null);

		direct.Select(r => r.RunId).Should().ContainInOrder("kid-2", "kid-1");
		direct.Should().HaveCount(2, "only direct children");
		subtree.Select(r => r.RunId).Should().BeEquivalentTo(["kid-1", "kid-2", "grandkid"]);
	}

	[Fact]
	public async Task FindChildRuns_NoScope_ReturnsEmptyRatherThanEverything()
	{
		var store = NewStore();
		await store.SaveRunAsync(Record("run1"), cancellationToken: default);

		(await store.FindChildRunsAsync(null, null, null)).Should().BeEmpty();
	}

	[Fact]
	public async Task OrchestrationStats_CountAndLatestPerOrchestration()
	{
		var store = NewStore();
		var t0 = DateTimeOffset.UtcNow.AddHours(-4);
		await store.SaveRunAsync(Record("a1", "alpha", startedAt: t0), cancellationToken: default);
		await store.SaveRunAsync(Record("a2", "alpha", startedAt: t0.AddHours(1)), cancellationToken: default);
		await store.SaveRunAsync(Record("b1", "beta", startedAt: t0.AddHours(2)), cancellationToken: default);

		var stats = await store.GetOrchestrationRunStatsAsync();

		stats["alpha"].Count.Should().Be(2);
		stats["alpha"].LastStartedAt.Should().BeCloseTo(t0.AddHours(1), TimeSpan.FromSeconds(1));
		stats["beta"].Count.Should().Be(1);
	}

	[Fact]
	public async Task ProjectedFields_SurviveTheRoundTripThroughSqlite()
	{
		var t0 = new DateTimeOffset(2026, 5, 13, 20, 44, 18, TimeSpan.FromHours(3));
		var steps = new Dictionary<string, StepRunRecord>
		{
			["boom"] = new()
			{
				StepName = "boom",
				Status = ExecutionStatus.Failed,
				StartedAt = t0,
				CompletedAt = t0.AddMinutes(1),
				Content = "",
				ErrorMessage = "connector timed out",
			},
		};

		var store = NewStore();
		await store.SaveRunAsync(
			Record("run1", status: ExecutionStatus.Failed, startedAt: t0, triggerId: "trig", steps: steps),
			cancellationToken: default);

		var reopened = NewStore();
		var index = (await reopened.GetRunSummariesAsync()).Single();

		index.RunId.Should().Be("run1");
		index.OrchestrationName.Should().Be("test-orch");
		index.Status.Should().Be(ExecutionStatus.Failed);
		index.TriggerId.Should().Be("trig");
		index.FailedStepName.Should().Be("boom");
		index.ErrorMessage.Should().Be("connector timed out");
		// Offsets are preserved exactly, not normalized to UTC.
		index.StartedAt.Should().Be(t0);
		index.StartedAt.Offset.Should().Be(TimeSpan.FromHours(3));
	}

	// ── Deletion paths keep the index in step ──

	[Fact]
	public async Task DeleteRun_RemovesTheIndexRow()
	{
		var store = NewStore();
		await store.SaveRunAsync(Record("run1"), cancellationToken: default);

		(await store.DeleteRunAsync("test-orch", "run1")).Should().BeTrue();

		(await store.GetRunSummariesAsync()).Should().BeEmpty();
		(await NewStore().GetRunSummariesAsync()).Should().BeEmpty("the deletion is persisted");
	}

	[Fact]
	public async Task Retention_RemovesIndexRowsForDeletedRuns()
	{
		var store = NewStore();
		var old = DateTimeOffset.UtcNow.AddDays(-30);
		await store.SaveRunAsync(Record("stale", startedAt: old), cancellationToken: default);
		await store.SaveRunAsync(Record("fresh"), cancellationToken: default);

		var deleted = await store.ApplyRetentionAsync(new RetentionPolicy { MaxRunAgeDays = 7 });

		deleted.Should().Be(1);
		(await store.GetRunSummariesAsync()).Select(s => s.RunId).Should().BeEquivalentTo(["fresh"]);
		(await NewStore().GetRunSummariesAsync()).Select(s => s.RunId).Should().BeEquivalentTo(["fresh"]);
	}

	[Fact]
	public async Task EmptyStore_QueriesReturnEmptyRatherThanThrowing()
	{
		var store = NewStore();

		(await store.GetRunSummariesAsync()).Should().BeEmpty();
		(await store.GetRunSummariesAsync("nope")).Should().BeEmpty();
		(await store.FindRunByIdAsync("nope")).Should().BeNull();
		(await store.GetRunAsync("nope", "nope")).Should().BeNull();
		(await store.GetOrchestrationRunStatsAsync()).Should().BeEmpty();
		(await store.DeleteRunAsync("nope", "nope")).Should().BeFalse();
	}
}
