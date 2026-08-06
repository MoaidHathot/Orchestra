using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Orchestra.Host.Persistence;
using Xunit;

namespace Orchestra.Host.Tests;

/// <summary>
/// Tests for <see cref="RunAnnotationStore"/> — the user-curated favorite/title/tags/note
/// metadata attached to runs.
/// </summary>
public class RunAnnotationStoreTests : IDisposable
{
	private readonly string _tempDir;
	private readonly RunAnnotationStore _store;

	public RunAnnotationStoreTests()
	{
		_tempDir = Path.Combine(Path.GetTempPath(), $"orchestra-annotations-tests-{Guid.NewGuid():N}");
		Directory.CreateDirectory(_tempDir);
		_store = CreateStore();
	}

	private RunAnnotationStore CreateStore() =>
		new(_tempDir, NullLogger<RunAnnotationStore>.Instance);

	public void Dispose()
	{
		if (Directory.Exists(_tempDir))
		{
			try { Directory.Delete(_tempDir, recursive: true); }
			catch { /* best-effort cleanup */ }
		}
		GC.SuppressFinalize(this);
	}

	private static RunAnnotation Annotation(
		bool favorite = false,
		string? title = null,
		string[]? tags = null,
		string? note = null,
		string? orchestrationName = "test-orchestration") => new()
		{
			Favorite = favorite,
			Title = title,
			Tags = tags ?? [],
			Note = note,
			OrchestrationName = orchestrationName,
			AnnotatedAt = DateTimeOffset.UtcNow,
		};

	// ── Basic CRUD ──

	[Fact]
	public void Get_UnknownRun_ReturnsNull() =>
		_store.Get("nope").Should().BeNull();

	[Fact]
	public void Set_ThenGet_RoundTripsAllFields()
	{
		_store.Set("run1", Annotation(
			favorite: true,
			title: "Connect evidence pack",
			tags: ["connect", "keep"],
			note: "Counts are unreliable."));

		var result = _store.Get("run1");

		result.Should().NotBeNull();
		result!.Favorite.Should().BeTrue();
		result.Title.Should().Be("Connect evidence pack");
		result.Tags.Should().BeEquivalentTo(["connect", "keep"]);
		result.Note.Should().Be("Counts are unreliable.");
		result.OrchestrationName.Should().Be("test-orchestration");
	}

	[Fact]
	public void Set_EmptyAnnotation_RemovesRecord()
	{
		_store.Set("run1", Annotation(favorite: true));
		_store.Get("run1").Should().NotBeNull();

		var result = _store.Set("run1", Annotation());

		result.Should().BeNull();
		_store.Get("run1").Should().BeNull();
	}

	[Fact]
	public void Remove_ExistingRun_ReturnsTrueAndDeletes()
	{
		_store.Set("run1", Annotation(favorite: true));

		_store.Remove("run1").Should().BeTrue();
		_store.Get("run1").Should().BeNull();
		_store.Remove("run1").Should().BeFalse();
	}

	[Fact]
	public void RemoveMany_RemovesOnlyKnownRuns()
	{
		_store.Set("run1", Annotation(favorite: true));
		_store.Set("run2", Annotation(favorite: true));

		var removed = _store.RemoveMany(["run1", "run2", "never-existed"]);

		removed.Should().Be(2);
		_store.Count.Should().Be(0);
	}

	// ── Patch semantics: the reason `annotate --title` must not wipe tags ──

	[Fact]
	public void Patch_OnlySuppliedFieldsChange()
	{
		_store.Set("run1", Annotation(favorite: true, title: "Original", tags: ["a"], note: "keep me"));

		var result = _store.Patch("run1", title: "Renamed");

		result!.Title.Should().Be("Renamed");
		result.Favorite.Should().BeTrue("favorite was not supplied and must be preserved");
		result.Tags.Should().BeEquivalentTo(["a"], "tags were not supplied and must be preserved");
		result.Note.Should().Be("keep me", "note was not supplied and must be preserved");
	}

	[Fact]
	public void Patch_EmptyStringClearsField()
	{
		_store.Set("run1", Annotation(favorite: true, title: "Original"));

		var result = _store.Patch("run1", title: "");

		result!.Title.Should().BeNull();
		result.Favorite.Should().BeTrue();
	}

	[Fact]
	public void Patch_OnNewRun_CreatesAnnotation()
	{
		var result = _store.Patch("brand-new", favorite: true, orchestrationName: "orch");

		result.Should().NotBeNull();
		result!.Favorite.Should().BeTrue();
		_store.IsFavorite("brand-new").Should().BeTrue();
	}

	[Fact]
	public void Patch_ClearingLastField_RemovesRecord()
	{
		_store.Set("run1", Annotation(favorite: true));

		var result = _store.Patch("run1", favorite: false);

		result.Should().BeNull();
		_store.Get("run1").Should().BeNull();
	}

	// ── Normalization ──

	[Fact]
	public void Tags_AreLowercasedTrimmedAndDeduplicated()
	{
		_store.Set("run1", Annotation(tags: ["  Connect ", "CONNECT", "keep", "", "  "]));

		_store.Get("run1")!.Tags.Should().BeEquivalentTo(["connect", "keep"]);
	}

	[Fact]
	public void BlankTitleAndNote_NormalizeToNull()
	{
		_store.Set("run1", Annotation(favorite: true, title: "   ", note: ""));

		var result = _store.Get("run1")!;
		result.Title.Should().BeNull();
		result.Note.Should().BeNull();
	}

	[Fact]
	public void Get_IsCaseInsensitiveOnRunId()
	{
		_store.Set("AbCdEf", Annotation(favorite: true));

		_store.Get("abcdef").Should().NotBeNull();
		_store.IsFavorite("ABCDEF").Should().BeTrue();
	}

	// ── Queries ──

	[Fact]
	public void GetFavoriteRunIds_ReturnsOnlyFavorites()
	{
		_store.Set("fav", Annotation(favorite: true));
		_store.Set("tagged", Annotation(tags: ["x"]));

		_store.GetFavoriteRunIds().Should().BeEquivalentTo(["fav"]);
	}

	[Fact]
	public void GetAllTagsWithCounts_AggregatesAcrossRuns()
	{
		_store.Set("run1", Annotation(tags: ["connect", "keep"]));
		_store.Set("run2", Annotation(tags: ["connect"]));

		var counts = _store.GetAllTagsWithCounts();

		counts["connect"].Should().Be(2);
		counts["keep"].Should().Be(1);
	}

	[Fact]
	public void FindOrphans_ReturnsAnnotationsWithoutALiveRun()
	{
		_store.Set("alive", Annotation(favorite: true));
		_store.Set("dead", Annotation(favorite: true));

		var orphans = _store.FindOrphans(new HashSet<string>(["alive"], StringComparer.OrdinalIgnoreCase));

		orphans.Should().BeEquivalentTo(["dead"]);
	}

	// ── Persistence ──

	[Fact]
	public void Annotations_SurviveAcrossStoreInstances()
	{
		_store.Set("run1", Annotation(
			favorite: true, title: "Kept", tags: ["connect"], note: "why"));

		var reloaded = CreateStore();

		var result = reloaded.Get("run1");
		result.Should().NotBeNull();
		result!.Favorite.Should().BeTrue();
		result.Title.Should().Be("Kept");
		result.Tags.Should().BeEquivalentTo(["connect"]);
		result.Note.Should().Be("why");
	}

	[Fact]
	public void Removal_IsPersistedAcrossStoreInstances()
	{
		_store.Set("run1", Annotation(favorite: true));
		_store.Remove("run1");

		CreateStore().Get("run1").Should().BeNull();
	}

	[Fact]
	public void StorageLayout_IsOneFilePerRunUnderOrchestrationFolder()
	{
		_store.Set("run1", Annotation(favorite: true, orchestrationName: "my-orch"));

		var expected = Path.Combine(_tempDir, "annotations", "my-orch", "run1.json");
		File.Exists(expected).Should().BeTrue();
	}

	[Fact]
	public void OrchestrationNameWithPathSeparators_IsSanitized()
	{
		_store.Set("run1", Annotation(favorite: true, orchestrationName: "a/b\\c"));

		// Must not escape the annotations root.
		var files = Directory.GetFiles(Path.Combine(_tempDir, "annotations"), "*.json", SearchOption.AllDirectories);
		files.Should().HaveCount(1);
		CreateStore().Get("run1").Should().NotBeNull();
	}

	[Fact]
	public void CorruptFile_DoesNotPreventLoadingOthers()
	{
		_store.Set("good1", Annotation(favorite: true, orchestrationName: "orch"));
		_store.Set("good2", Annotation(favorite: true, orchestrationName: "orch"));

		// Simulate a truncated write from a crash.
		File.WriteAllText(Path.Combine(_tempDir, "annotations", "orch", "broken.json"), "{ not json");

		var reloaded = CreateStore();

		reloaded.Get("good1").Should().NotBeNull();
		reloaded.Get("good2").Should().NotBeNull();
		reloaded.Get("broken").Should().BeNull();
	}

	[Fact]
	public void ConcurrentMutations_DoNotCorruptTheStore()
	{
		Parallel.For(0, 100, i =>
		{
			var runId = $"run{i % 10}";
			_store.Patch(runId, favorite: true, tags: [$"tag{i % 3}"], orchestrationName: "orch");
		});

		_store.Count.Should().Be(10);
		CreateStore().Count.Should().Be(10, "every write must have landed on disk intact");
	}
}
