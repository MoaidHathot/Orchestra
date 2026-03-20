using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Orchestra.Engine;
using Orchestra.Host.Persistence;
using Xunit;

namespace Orchestra.Host.Tests;

/// <summary>
/// Tests for <see cref="FileSystemOrchestrationVersionStore"/>: version saving,
/// listing, snapshots, idempotent saves, content hashing, change descriptions, diff, and deletion.
/// </summary>
public class FileSystemOrchestrationVersionStoreTests : IDisposable
{
	private readonly string _tempDir;
	private readonly FileSystemOrchestrationVersionStore _store;

	public FileSystemOrchestrationVersionStoreTests()
	{
		_tempDir = Path.Combine(
			Path.GetTempPath(),
			$"orchestra-versionstore-tests-{Guid.NewGuid():N}");
		Directory.CreateDirectory(_tempDir);
		_store = new FileSystemOrchestrationVersionStore(_tempDir, NullLogger<FileSystemOrchestrationVersionStore>.Instance);
	}

	public void Dispose()
	{
		if (Directory.Exists(_tempDir))
		{
			try { Directory.Delete(_tempDir, recursive: true); }
			catch { /* best-effort cleanup */ }
		}
	}

	// ── Helpers ──

	private static OrchestrationVersionEntry CreateTestEntry(
		string? contentHash = null,
		string? declaredVersion = null,
		string? orchestrationName = null,
		int stepCount = 3,
		string? changeDescription = null)
	{
		return new OrchestrationVersionEntry
		{
			ContentHash = contentHash ?? "abc123",
			DeclaredVersion = declaredVersion ?? "1.0.0",
			Timestamp = DateTimeOffset.UtcNow,
			OrchestrationName = orchestrationName ?? "Test Orchestration",
			StepCount = stepCount,
			ChangeDescription = changeDescription ?? "Initial version"
		};
	}

	private static string SampleOrchestrationJson(string name = "Test Orchestration", string version = "1.0.0") =>
		$$"""
		{
		  "name": "{{name}}",
		  "description": "A test orchestration",
		  "version": "{{version}}",
		  "steps": [
		    {
		      "name": "step1",
		      "systemPrompt": "You are a test agent.",
		      "userPrompt": "Hello.",
		      "model": "claude-opus-4.5"
		    }
		  ]
		}
		""";

	// ── SaveVersionAsync ──

	[Fact]
	public async Task SaveVersionAsync_CreatesSnapshotFile()
	{
		// Arrange
		var json = SampleOrchestrationJson();
		var hash = FileSystemOrchestrationVersionStore.ComputeContentHash(json);
		var entry = CreateTestEntry(contentHash: hash);

		// Act
		await _store.SaveVersionAsync("orch-1", entry, json);

		// Assert
		var snapshotPath = Path.Combine(_store.RootPath, "orch-1", "snapshots", $"{hash}.json");
		File.Exists(snapshotPath).Should().BeTrue();
		var savedJson = await File.ReadAllTextAsync(snapshotPath);
		savedJson.Should().Be(json);
	}

	[Fact]
	public async Task SaveVersionAsync_CreatesHistoryFile()
	{
		// Arrange
		var json = SampleOrchestrationJson();
		var hash = FileSystemOrchestrationVersionStore.ComputeContentHash(json);
		var entry = CreateTestEntry(contentHash: hash);

		// Act
		await _store.SaveVersionAsync("orch-1", entry, json);

		// Assert
		var historyPath = Path.Combine(_store.RootPath, "orch-1", "history.json");
		File.Exists(historyPath).Should().BeTrue();
	}

	[Fact]
	public async Task SaveVersionAsync_IdempotentForSameHash()
	{
		// Arrange
		var json = SampleOrchestrationJson();
		var hash = FileSystemOrchestrationVersionStore.ComputeContentHash(json);
		var entry1 = CreateTestEntry(contentHash: hash, changeDescription: "First save");
		var entry2 = CreateTestEntry(contentHash: hash, changeDescription: "Second save");

		// Act
		await _store.SaveVersionAsync("orch-1", entry1, json);
		await _store.SaveVersionAsync("orch-1", entry2, json);

		// Assert — should only have one entry in history
		var versions = await _store.ListVersionsAsync("orch-1");
		versions.Should().HaveCount(1);
		versions[0].ChangeDescription.Should().Be("First save");
	}

	[Fact]
	public async Task SaveVersionAsync_MultipleDistinctVersions()
	{
		// Arrange
		var json1 = SampleOrchestrationJson(version: "1.0.0");
		var hash1 = FileSystemOrchestrationVersionStore.ComputeContentHash(json1);
		var entry1 = CreateTestEntry(contentHash: hash1, declaredVersion: "1.0.0");

		var json2 = SampleOrchestrationJson(version: "2.0.0");
		var hash2 = FileSystemOrchestrationVersionStore.ComputeContentHash(json2);
		var entry2 = CreateTestEntry(contentHash: hash2, declaredVersion: "2.0.0");

		// Act
		await _store.SaveVersionAsync("orch-1", entry1, json1);
		await _store.SaveVersionAsync("orch-1", entry2, json2);

		// Assert
		var versions = await _store.ListVersionsAsync("orch-1");
		versions.Should().HaveCount(2);
	}

	// ── ListVersionsAsync ──

	[Fact]
	public async Task ListVersionsAsync_ReturnsEmptyForUnknownOrchestration()
	{
		// Act
		var versions = await _store.ListVersionsAsync("nonexistent");

		// Assert
		versions.Should().BeEmpty();
	}

	[Fact]
	public async Task ListVersionsAsync_ReturnsNewestFirst()
	{
		// Arrange
		var json1 = SampleOrchestrationJson(version: "1.0.0");
		var hash1 = FileSystemOrchestrationVersionStore.ComputeContentHash(json1);
		var entry1 = new OrchestrationVersionEntry
		{
			ContentHash = hash1,
			DeclaredVersion = "1.0.0",
			Timestamp = DateTimeOffset.UtcNow.AddHours(-2),
			OrchestrationName = "Test",
			StepCount = 1
		};

		var json2 = SampleOrchestrationJson(version: "2.0.0");
		var hash2 = FileSystemOrchestrationVersionStore.ComputeContentHash(json2);
		var entry2 = new OrchestrationVersionEntry
		{
			ContentHash = hash2,
			DeclaredVersion = "2.0.0",
			Timestamp = DateTimeOffset.UtcNow,
			OrchestrationName = "Test",
			StepCount = 1
		};

		await _store.SaveVersionAsync("orch-1", entry1, json1);
		await _store.SaveVersionAsync("orch-1", entry2, json2);

		// Act
		var versions = await _store.ListVersionsAsync("orch-1");

		// Assert — newest (2.0.0) should be first
		versions.Should().HaveCount(2);
		versions[0].DeclaredVersion.Should().Be("2.0.0");
		versions[1].DeclaredVersion.Should().Be("1.0.0");
	}

	// ── GetSnapshotAsync ──

	[Fact]
	public async Task GetSnapshotAsync_ReturnsNullForUnknownHash()
	{
		// Act
		var snapshot = await _store.GetSnapshotAsync("orch-1", "nonexistenthash");

		// Assert
		snapshot.Should().BeNull();
	}

	[Fact]
	public async Task GetSnapshotAsync_ReturnsSavedJson()
	{
		// Arrange
		var json = SampleOrchestrationJson();
		var hash = FileSystemOrchestrationVersionStore.ComputeContentHash(json);
		var entry = CreateTestEntry(contentHash: hash);
		await _store.SaveVersionAsync("orch-1", entry, json);

		// Act
		var snapshot = await _store.GetSnapshotAsync("orch-1", hash);

		// Assert
		snapshot.Should().Be(json);
	}

	// ── GetLatestVersionAsync ──

	[Fact]
	public async Task GetLatestVersionAsync_ReturnsNullForUnknownOrchestration()
	{
		// Act
		var latest = await _store.GetLatestVersionAsync("nonexistent");

		// Assert
		latest.Should().BeNull();
	}

	[Fact]
	public async Task GetLatestVersionAsync_ReturnsNewestEntry()
	{
		// Arrange
		var json1 = SampleOrchestrationJson(version: "1.0.0");
		var hash1 = FileSystemOrchestrationVersionStore.ComputeContentHash(json1);
		var entry1 = new OrchestrationVersionEntry
		{
			ContentHash = hash1,
			DeclaredVersion = "1.0.0",
			Timestamp = DateTimeOffset.UtcNow.AddHours(-1),
			OrchestrationName = "Test",
			StepCount = 1
		};

		var json2 = SampleOrchestrationJson(version: "2.0.0");
		var hash2 = FileSystemOrchestrationVersionStore.ComputeContentHash(json2);
		var entry2 = new OrchestrationVersionEntry
		{
			ContentHash = hash2,
			DeclaredVersion = "2.0.0",
			Timestamp = DateTimeOffset.UtcNow,
			OrchestrationName = "Test",
			StepCount = 1
		};

		await _store.SaveVersionAsync("orch-1", entry1, json1);
		await _store.SaveVersionAsync("orch-1", entry2, json2);

		// Act
		var latest = await _store.GetLatestVersionAsync("orch-1");

		// Assert
		latest.Should().NotBeNull();
		latest!.DeclaredVersion.Should().Be("2.0.0");
		latest.ContentHash.Should().Be(hash2);
	}

	// ── DeleteAllVersionsAsync ──

	[Fact]
	public async Task DeleteAllVersionsAsync_RemovesEntireDirectory()
	{
		// Arrange
		var json = SampleOrchestrationJson();
		var hash = FileSystemOrchestrationVersionStore.ComputeContentHash(json);
		var entry = CreateTestEntry(contentHash: hash);
		await _store.SaveVersionAsync("orch-1", entry, json);

		// Verify it exists first
		var orchDir = Path.Combine(_store.RootPath, "orch-1");
		Directory.Exists(orchDir).Should().BeTrue();

		// Act
		await _store.DeleteAllVersionsAsync("orch-1");

		// Assert
		Directory.Exists(orchDir).Should().BeFalse();
	}

	[Fact]
	public async Task DeleteAllVersionsAsync_NoErrorForUnknownOrchestration()
	{
		// Act — should not throw
		await _store.DeleteAllVersionsAsync("nonexistent");
	}

	[Fact]
	public async Task DeleteAllVersionsAsync_ListReturnsEmptyAfterDelete()
	{
		// Arrange
		var json = SampleOrchestrationJson();
		var hash = FileSystemOrchestrationVersionStore.ComputeContentHash(json);
		var entry = CreateTestEntry(contentHash: hash);
		await _store.SaveVersionAsync("orch-1", entry, json);

		// Act
		await _store.DeleteAllVersionsAsync("orch-1");

		// Assert
		var versions = await _store.ListVersionsAsync("orch-1");
		versions.Should().BeEmpty();
	}

	// ── ComputeContentHash (static) ──

	[Fact]
	public void ComputeContentHash_ReturnsSameHashForSameContent()
	{
		// Arrange
		var json = SampleOrchestrationJson();

		// Act
		var hash1 = FileSystemOrchestrationVersionStore.ComputeContentHash(json);
		var hash2 = FileSystemOrchestrationVersionStore.ComputeContentHash(json);

		// Assert
		hash1.Should().Be(hash2);
	}

	[Fact]
	public void ComputeContentHash_ReturnsDifferentHashForDifferentContent()
	{
		// Arrange
		var json1 = SampleOrchestrationJson(version: "1.0.0");
		var json2 = SampleOrchestrationJson(version: "2.0.0");

		// Act
		var hash1 = FileSystemOrchestrationVersionStore.ComputeContentHash(json1);
		var hash2 = FileSystemOrchestrationVersionStore.ComputeContentHash(json2);

		// Assert
		hash1.Should().NotBe(hash2);
	}

	[Fact]
	public void ComputeContentHash_ReturnsLowercaseHex()
	{
		// Arrange
		var json = SampleOrchestrationJson();

		// Act
		var hash = FileSystemOrchestrationVersionStore.ComputeContentHash(json);

		// Assert
		hash.Should().MatchRegex("^[0-9a-f]{64}$");
	}

	// ── GenerateChangeDescription (static) ──

	[Fact]
	public void GenerateChangeDescription_InitialVersion_WhenPreviousIsNull()
	{
		// Act
		var desc = FileSystemOrchestrationVersionStore.GenerateChangeDescription(null, CreateTestEntry());

		// Assert
		desc.Should().Be("Initial version");
	}

	[Fact]
	public void GenerateChangeDescription_DetectsVersionChange()
	{
		// Arrange
		var previous = CreateTestEntry(declaredVersion: "1.0.0");
		var current = CreateTestEntry(declaredVersion: "2.0.0");

		// Act
		var desc = FileSystemOrchestrationVersionStore.GenerateChangeDescription(previous, current);

		// Assert
		desc.Should().Contain("Version changed: 1.0.0 -> 2.0.0");
	}

	[Fact]
	public void GenerateChangeDescription_DetectsStepCountChange()
	{
		// Arrange
		var previous = CreateTestEntry(stepCount: 3);
		var current = CreateTestEntry(stepCount: 5);

		// Act
		var desc = FileSystemOrchestrationVersionStore.GenerateChangeDescription(previous, current);

		// Assert
		desc.Should().Contain("Steps: +2");
	}

	[Fact]
	public void GenerateChangeDescription_DetectsStepCountDecrease()
	{
		// Arrange
		var previous = CreateTestEntry(stepCount: 5);
		var current = CreateTestEntry(stepCount: 3);

		// Act
		var desc = FileSystemOrchestrationVersionStore.GenerateChangeDescription(previous, current);

		// Assert
		desc.Should().Contain("Steps: -2");
	}

	[Fact]
	public void GenerateChangeDescription_DetectsRename()
	{
		// Arrange
		var previous = CreateTestEntry(orchestrationName: "Old Name");
		var current = CreateTestEntry(orchestrationName: "New Name");

		// Act
		var desc = FileSystemOrchestrationVersionStore.GenerateChangeDescription(previous, current);

		// Assert
		desc.Should().Contain("Renamed: Old Name -> New Name");
	}

	[Fact]
	public void GenerateChangeDescription_ContentUpdated_WhenNoDetectableChanges()
	{
		// Arrange — same metadata, different hash implies content change
		var previous = CreateTestEntry();
		var current = CreateTestEntry();

		// Act
		var desc = FileSystemOrchestrationVersionStore.GenerateChangeDescription(previous, current);

		// Assert
		desc.Should().Be("Content updated");
	}

	[Fact]
	public void GenerateChangeDescription_CombinesMultipleChanges()
	{
		// Arrange
		var previous = CreateTestEntry(declaredVersion: "1.0.0", stepCount: 3, orchestrationName: "Old");
		var current = CreateTestEntry(declaredVersion: "2.0.0", stepCount: 5, orchestrationName: "New");

		// Act
		var desc = FileSystemOrchestrationVersionStore.GenerateChangeDescription(previous, current);

		// Assert
		desc.Should().Contain("Version changed: 1.0.0 -> 2.0.0");
		desc.Should().Contain("Steps: +2");
		desc.Should().Contain("Renamed: Old -> New");
	}

	// ── ComputeDiff (static) ──

	[Fact]
	public void ComputeDiff_IdenticalContent_AllUnchanged()
	{
		// Arrange
		var json = "{\n  \"name\": \"test\"\n}";

		// Act
		var diff = FileSystemOrchestrationVersionStore.ComputeDiff(json, json);

		// Assert
		diff.Should().AllSatisfy(d => d.Type.Should().Be(DiffLineType.Unchanged));
	}

	[Fact]
	public void ComputeDiff_AddedLines()
	{
		// Arrange
		var oldJson = "{\n  \"name\": \"test\"\n}";
		var newJson = "{\n  \"name\": \"test\",\n  \"version\": \"2.0.0\"\n}";

		// Act
		var diff = FileSystemOrchestrationVersionStore.ComputeDiff(oldJson, newJson);

		// Assert
		diff.Should().Contain(d => d.Type == DiffLineType.Added);
	}

	[Fact]
	public void ComputeDiff_RemovedLines()
	{
		// Arrange
		var oldJson = "{\n  \"name\": \"test\",\n  \"version\": \"1.0.0\"\n}";
		var newJson = "{\n  \"name\": \"test\"\n}";

		// Act
		var diff = FileSystemOrchestrationVersionStore.ComputeDiff(oldJson, newJson);

		// Assert
		diff.Should().Contain(d => d.Type == DiffLineType.Removed);
	}

	[Fact]
	public void ComputeDiff_ModifiedLine_ShowsAsRemoveAndAdd()
	{
		// Arrange
		var oldJson = "{\n  \"name\": \"old\"\n}";
		var newJson = "{\n  \"name\": \"new\"\n}";

		// Act
		var diff = FileSystemOrchestrationVersionStore.ComputeDiff(oldJson, newJson);

		// Assert
		diff.Should().Contain(d => d.Type == DiffLineType.Removed && d.Content.Contains("old"));
		diff.Should().Contain(d => d.Type == DiffLineType.Added && d.Content.Contains("new"));
	}

	[Fact]
	public void ComputeDiff_EmptyOldJson_AllAdded()
	{
		// Arrange
		var oldJson = "";
		var newJson = "{\n  \"name\": \"test\"\n}";

		// Act
		var diff = FileSystemOrchestrationVersionStore.ComputeDiff(oldJson, newJson);

		// Assert — the empty old string produces one empty line, so we should see added lines for non-empty content
		diff.Should().Contain(d => d.Type == DiffLineType.Added);
	}

	// ── Multi-Orchestration Isolation ──

	[Fact]
	public async Task SaveVersionAsync_IsolatesOrchestrations()
	{
		// Arrange
		var json1 = SampleOrchestrationJson(name: "Orch1");
		var hash1 = FileSystemOrchestrationVersionStore.ComputeContentHash(json1);
		var entry1 = CreateTestEntry(contentHash: hash1, orchestrationName: "Orch1");

		var json2 = SampleOrchestrationJson(name: "Orch2");
		var hash2 = FileSystemOrchestrationVersionStore.ComputeContentHash(json2);
		var entry2 = CreateTestEntry(contentHash: hash2, orchestrationName: "Orch2");

		// Act
		await _store.SaveVersionAsync("orch-1", entry1, json1);
		await _store.SaveVersionAsync("orch-2", entry2, json2);

		// Assert
		var versions1 = await _store.ListVersionsAsync("orch-1");
		var versions2 = await _store.ListVersionsAsync("orch-2");
		versions1.Should().HaveCount(1);
		versions1[0].OrchestrationName.Should().Be("Orch1");
		versions2.Should().HaveCount(1);
		versions2[0].OrchestrationName.Should().Be("Orch2");
	}

	[Fact]
	public async Task DeleteAllVersionsAsync_DoesNotAffectOtherOrchestrations()
	{
		// Arrange
		var json1 = SampleOrchestrationJson(name: "Orch1");
		var hash1 = FileSystemOrchestrationVersionStore.ComputeContentHash(json1);
		var entry1 = CreateTestEntry(contentHash: hash1, orchestrationName: "Orch1");

		var json2 = SampleOrchestrationJson(name: "Orch2");
		var hash2 = FileSystemOrchestrationVersionStore.ComputeContentHash(json2);
		var entry2 = CreateTestEntry(contentHash: hash2, orchestrationName: "Orch2");

		await _store.SaveVersionAsync("orch-1", entry1, json1);
		await _store.SaveVersionAsync("orch-2", entry2, json2);

		// Act
		await _store.DeleteAllVersionsAsync("orch-1");

		// Assert
		var versions1 = await _store.ListVersionsAsync("orch-1");
		var versions2 = await _store.ListVersionsAsync("orch-2");
		versions1.Should().BeEmpty();
		versions2.Should().HaveCount(1);
	}
}
