using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Orchestra.Host.Profiles;
using Xunit;

namespace Orchestra.Host.Tests;

/// <summary>
/// Unit tests for OrchestrationTagStore.
/// </summary>
public class OrchestrationTagStoreTests : IDisposable
{
	private readonly string _tempDir;

	public OrchestrationTagStoreTests()
	{
		_tempDir = Path.Combine(Path.GetTempPath(), $"orchestra-tagstore-tests-{Guid.NewGuid():N}");
		Directory.CreateDirectory(_tempDir);
	}

	public void Dispose()
	{
		if (Directory.Exists(_tempDir))
		{
			try { Directory.Delete(_tempDir, recursive: true); }
			catch { /* best-effort cleanup */ }
		}
	}

	private OrchestrationTagStore CreateStore() =>
		new(_tempDir, NullLogger<OrchestrationTagStore>.Instance);

	[Fact]
	public void GetTags_NoTags_ReturnsEmpty()
	{
		var store = CreateStore();
		store.GetTags("orch-1").Should().BeEmpty();
	}

	[Fact]
	public void SetTags_ThenGetTags_ReturnsSetTags()
	{
		var store = CreateStore();

		store.SetTags("orch-1", ["production", "monitoring"]);

		var tags = store.GetTags("orch-1");
		tags.Should().BeEquivalentTo(["production", "monitoring"]);
	}

	[Fact]
	public void SetTags_NormalizesCaseAndTrims()
	{
		var store = CreateStore();

		store.SetTags("orch-1", [" Production ", "MONITORING"]);

		var tags = store.GetTags("orch-1");
		tags.Should().BeEquivalentTo(["production", "monitoring"]);
	}

	[Fact]
	public void SetTags_EmptyArray_RemovesTags()
	{
		var store = CreateStore();

		store.SetTags("orch-1", ["tag-a"]);
		store.SetTags("orch-1", []);

		store.GetTags("orch-1").Should().BeEmpty();
	}

	[Fact]
	public void AddTags_MergesWithExisting()
	{
		var store = CreateStore();

		store.SetTags("orch-1", ["tag-a"]);
		store.AddTags("orch-1", ["tag-b", "tag-c"]);

		var tags = store.GetTags("orch-1");
		tags.Should().BeEquivalentTo(["tag-a", "tag-b", "tag-c"]);
	}

	[Fact]
	public void AddTags_DeduplicatesWithExisting()
	{
		var store = CreateStore();

		store.SetTags("orch-1", ["tag-a"]);
		store.AddTags("orch-1", ["tag-a", "tag-b"]);

		var tags = store.GetTags("orch-1");
		tags.Should().BeEquivalentTo(["tag-a", "tag-b"]);
	}

	[Fact]
	public void AddTags_ToNewOrchestration_Creates()
	{
		var store = CreateStore();

		store.AddTags("orch-new", ["tag-x"]);

		store.GetTags("orch-new").Should().BeEquivalentTo(["tag-x"]);
	}

	[Fact]
	public void RemoveTag_ExistingTag_ReturnsTrue()
	{
		var store = CreateStore();

		store.SetTags("orch-1", ["tag-a", "tag-b"]);
		var result = store.RemoveTag("orch-1", "tag-a");

		result.Should().BeTrue();
		store.GetTags("orch-1").Should().BeEquivalentTo(["tag-b"]);
	}

	[Fact]
	public void RemoveTag_NonExistentTag_ReturnsFalse()
	{
		var store = CreateStore();

		store.SetTags("orch-1", ["tag-a"]);
		var result = store.RemoveTag("orch-1", "tag-missing");

		result.Should().BeFalse();
	}

	[Fact]
	public void RemoveTag_LastTag_RemovesOrchestrationEntry()
	{
		var store = CreateStore();

		store.SetTags("orch-1", ["only-tag"]);
		store.RemoveTag("orch-1", "only-tag");

		store.GetTags("orch-1").Should().BeEmpty();
	}

	[Fact]
	public void RemoveOrchestration_RemovesAllTags()
	{
		var store = CreateStore();

		store.SetTags("orch-1", ["tag-a", "tag-b"]);
		store.RemoveOrchestration("orch-1");

		store.GetTags("orch-1").Should().BeEmpty();
	}

	// ── GetEffectiveTags ──

	[Fact]
	public void GetEffectiveTags_MergesAuthorAndHostTags()
	{
		var store = CreateStore();

		store.SetTags("orch-1", ["host-tag"]);
		var effective = store.GetEffectiveTags("orch-1", ["author-tag"]);

		effective.Should().BeEquivalentTo(["author-tag", "host-tag"]);
	}

	[Fact]
	public void GetEffectiveTags_AuthorOnlyTags()
	{
		var store = CreateStore();

		var effective = store.GetEffectiveTags("orch-1", ["author-tag"]);

		effective.Should().BeEquivalentTo(["author-tag"]);
	}

	[Fact]
	public void GetEffectiveTags_HostOnlyTags()
	{
		var store = CreateStore();

		store.SetTags("orch-1", ["host-tag"]);
		var effective = store.GetEffectiveTags("orch-1", []);

		effective.Should().BeEquivalentTo(["host-tag"]);
	}

	[Fact]
	public void GetEffectiveTags_NoTags_ReturnsEmpty()
	{
		var store = CreateStore();

		var effective = store.GetEffectiveTags("orch-1", []);

		effective.Should().BeEmpty();
	}

	[Fact]
	public void GetEffectiveTags_DeduplicatesOverlapping()
	{
		var store = CreateStore();

		store.SetTags("orch-1", ["shared-tag"]);
		var effective = store.GetEffectiveTags("orch-1", ["shared-tag"]);

		effective.Should().HaveCount(1);
		effective.Should().Contain("shared-tag");
	}

	// ── GetAllTagsWithCounts ──

	[Fact]
	public void GetAllTagsWithCounts_AggregatesAcrossOrchestrations()
	{
		var store = CreateStore();

		store.SetTags("orch-1", ["monitoring"]);
		store.SetTags("orch-2", ["monitoring"]);

		var orchestrations = new (string, string[])[]
		{
			("orch-1", ["production"]),
			("orch-2", ["production"]),
			("orch-3", ["staging"]),
		};

		var counts = store.GetAllTagsWithCounts(orchestrations);

		counts["monitoring"].Should().Be(2);
		counts["production"].Should().Be(2);
		counts["staging"].Should().Be(1);
	}

	// ── Persistence ──

	[Fact]
	public void Tags_PersistToAndLoadFromDisk()
	{
		// Set up tags
		var store1 = CreateStore();
		store1.SetTags("orch-1", ["tag-a", "tag-b"]);
		store1.SetTags("orch-2", ["tag-c"]);

		// Create a new store instance — should load from disk
		var store2 = CreateStore();

		store2.GetTags("orch-1").Should().BeEquivalentTo(["tag-a", "tag-b"]);
		store2.GetTags("orch-2").Should().BeEquivalentTo(["tag-c"]);
	}
}
