using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Orchestra.Engine;
using Orchestra.Host.Hosting;
using Orchestra.Host.Persistence;
using Xunit;

namespace Orchestra.Host.Tests;

/// <summary>
/// Retention behaviour for favorited runs.
/// </summary>
/// <remarks>
/// A favorite is an explicit "keep this" signal, so retention must never delete one. The
/// non-obvious half is the max-count rule, which deletes by <i>position</i> in a newest-first
/// ranking: if favorites stayed in that ranking they would occupy keep-slots and block pruning
/// of ordinary runs entirely. These tests pin both halves.
/// </remarks>
public class RunAnnotationRetentionTests : IDisposable
{
	private readonly string _tempDir;
	private readonly RunAnnotationStore _annotations;
	private readonly FileSystemRunStore _store;

	public RunAnnotationRetentionTests()
	{
		_tempDir = Path.Combine(Path.GetTempPath(), $"orchestra-fav-retention-{Guid.NewGuid():N}");
		Directory.CreateDirectory(_tempDir);
		_annotations = new RunAnnotationStore(_tempDir, NullLogger<RunAnnotationStore>.Instance);
		_store = new FileSystemRunStore(_tempDir, NullLogger<FileSystemRunStore>.Instance, _annotations);
	}

	public void Dispose()
	{
		if (Directory.Exists(_tempDir))
		{
			try { Directory.Delete(_tempDir, recursive: true); }
			catch { /* best-effort cleanup */ }
		}
		GC.SuppressFinalize(this);
	}

	private async Task<string> SaveRunAsync(string runId, DateTimeOffset startedAt, string orchestration = "test-orchestration")
	{
		await _store.SaveRunAsync(new OrchestrationRunRecord
		{
			RunId = runId,
			OrchestrationName = orchestration,
			OrchestrationVersion = "1.0.0",
			TriggeredBy = "manual",
			StartedAt = startedAt,
			CompletedAt = startedAt.AddMinutes(1),
			Status = ExecutionStatus.Succeeded,
			FinalContent = "result",
			HookExecutions = [],
			StepRecords = new Dictionary<string, StepRunRecord>(),
			AllStepRecords = new Dictionary<string, StepRunRecord>(),
		}, cancellationToken: default);
		return runId;
	}

	private async Task<IReadOnlyList<string>> SurvivingRunIdsAsync() =>
		[.. (await _store.GetRunSummariesAsync()).Select(s => s.RunId)];

	// ── Age rule ──

	[Fact]
	public async Task MaxAge_DoesNotDeleteFavoritedRun()
	{
		var old = DateTimeOffset.UtcNow.AddDays(-30);
		await SaveRunAsync("favorited", old);
		await SaveRunAsync("ordinary", old);
		_annotations.Patch("favorited", favorite: true, orchestrationName: "test-orchestration");

		var deleted = await _store.ApplyRetentionAsync(new RetentionPolicy { MaxRunAgeDays = 7 });

		deleted.Should().Be(1);
		(await SurvivingRunIdsAsync()).Should().BeEquivalentTo(["favorited"]);
	}

	// ── Count rule ──

	[Fact]
	public async Task MaxCount_DoesNotDeleteFavoritedRun()
	{
		var now = DateTimeOffset.UtcNow;
		for (var i = 0; i < 5; i++)
			await SaveRunAsync($"run{i}", now.AddMinutes(-i));
		// run4 is the oldest, so it would normally be pruned first.
		_annotations.Patch("run4", favorite: true, orchestrationName: "test-orchestration");

		await _store.ApplyRetentionAsync(new RetentionPolicy { MaxRunsPerOrchestration = 2 });

		var survivors = await SurvivingRunIdsAsync();
		survivors.Should().Contain("run4", "favorites are exempt from the count rule");
		survivors.Should().Contain(["run0", "run1"], "the two newest non-favorites are kept");
		survivors.Should().NotContain(["run2", "run3"]);
	}

	[Fact]
	public async Task MaxCount_FavoritesDoNotConsumeKeepSlots()
	{
		// The regression this guards: with favorites left in the ranking, three favorites would
		// fill a keep-limit of 3 and no ordinary run would ever be pruned.
		var now = DateTimeOffset.UtcNow;
		for (var i = 0; i < 3; i++)
		{
			await SaveRunAsync($"fav{i}", now.AddMinutes(-i));
			_annotations.Patch($"fav{i}", favorite: true, orchestrationName: "test-orchestration");
		}
		for (var i = 0; i < 5; i++)
			await SaveRunAsync($"ordinary{i}", now.AddMinutes(-10 - i));

		await _store.ApplyRetentionAsync(new RetentionPolicy { MaxRunsPerOrchestration = 3 });

		var survivors = await SurvivingRunIdsAsync();
		survivors.Should().Contain(["fav0", "fav1", "fav2"]);
		survivors.Should().Contain(["ordinary0", "ordinary1", "ordinary2"],
			"the three newest ordinary runs still get their keep-slots");
		survivors.Should().NotContain(["ordinary3", "ordinary4"],
			"pruning must still happen despite the favorites");
	}

	[Fact]
	public async Task AllRunsFavorited_NothingIsDeleted()
	{
		var now = DateTimeOffset.UtcNow.AddDays(-30);
		for (var i = 0; i < 4; i++)
		{
			await SaveRunAsync($"run{i}", now.AddMinutes(-i));
			_annotations.Patch($"run{i}", favorite: true, orchestrationName: "test-orchestration");
		}

		var deleted = await _store.ApplyRetentionAsync(
			new RetentionPolicy { MaxRunsPerOrchestration = 1, MaxRunAgeDays = 1 });

		deleted.Should().Be(0);
		(await SurvivingRunIdsAsync()).Should().HaveCount(4);
	}

	// ── Annotation lifecycle ──

	[Fact]
	public async Task RetentionDelete_AlsoRemovesTheAnnotation()
	{
		var old = DateTimeOffset.UtcNow.AddDays(-30);
		await SaveRunAsync("doomed", old);
		// Tagged but not favorited: retention may delete it, and the annotation must go too.
		_annotations.Patch("doomed", tags: ["scratch"], orchestrationName: "test-orchestration");
		_annotations.Get("doomed").Should().NotBeNull();

		await _store.ApplyRetentionAsync(new RetentionPolicy { MaxRunAgeDays = 7 });

		_annotations.Get("doomed").Should().BeNull("an annotation must not outlive its run");
	}

	[Fact]
	public async Task ExplicitDelete_AlsoRemovesTheAnnotation()
	{
		await SaveRunAsync("run1", DateTimeOffset.UtcNow);
		_annotations.Patch("run1", favorite: true, orchestrationName: "test-orchestration");

		var deleted = await _store.DeleteRunAsync("test-orchestration", "run1");

		deleted.Should().BeTrue();
		_annotations.Get("run1").Should().BeNull();
	}

	[Fact]
	public async Task IsFavorite_ReflectsTheAnnotationStore()
	{
		await SaveRunAsync("run1", DateTimeOffset.UtcNow);

		_store.IsFavorite("run1").Should().BeFalse();
		_annotations.Patch("run1", favorite: true, orchestrationName: "test-orchestration");
		_store.IsFavorite("run1").Should().BeTrue();
	}

	[Fact]
	public async Task StoreWithoutAnnotationStore_BehavesAsBefore()
	{
		// Embedded hosts and older tests construct the store without annotations.
		var plainDir = Path.Combine(_tempDir, "plain");
		Directory.CreateDirectory(plainDir);
		var plain = new FileSystemRunStore(plainDir);

		await plain.SaveRunAsync(new OrchestrationRunRecord
		{
			RunId = "run1",
			OrchestrationName = "orch",
			StartedAt = DateTimeOffset.UtcNow.AddDays(-30),
			CompletedAt = DateTimeOffset.UtcNow.AddDays(-30),
			Status = ExecutionStatus.Succeeded,
			FinalContent = "x",
			HookExecutions = [],
			StepRecords = new Dictionary<string, StepRunRecord>(),
			AllStepRecords = new Dictionary<string, StepRunRecord>(),
		}, cancellationToken: default);

		plain.IsFavorite("run1").Should().BeFalse();
		var deleted = await plain.ApplyRetentionAsync(new RetentionPolicy { MaxRunAgeDays = 7 });
		deleted.Should().Be(1);
	}
}
