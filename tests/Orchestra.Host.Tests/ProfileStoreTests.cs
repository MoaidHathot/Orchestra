using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Orchestra.Host.Profiles;
using Xunit;

namespace Orchestra.Host.Tests;

/// <summary>
/// Unit tests for ProfileStore file-system persistence.
/// </summary>
public class ProfileStoreTests : IDisposable
{
	private readonly string _tempDir;

	public ProfileStoreTests()
	{
		_tempDir = Path.Combine(Path.GetTempPath(), $"orchestra-profilestore-tests-{Guid.NewGuid():N}");
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

	private ProfileStore CreateStore() =>
		new(_tempDir, NullLogger<ProfileStore>.Instance);

	private Profile CreateTestProfile(string name = "Test Profile", bool isActive = false)
	{
		var id = ProfileStore.GenerateId(name);
		return new Profile
		{
			Id = id,
			Name = name,
			Description = "A test profile",
			IsActive = isActive,
			Filter = new ProfileFilter { Tags = ["test"] },
			CreatedAt = DateTimeOffset.UtcNow,
			UpdatedAt = DateTimeOffset.UtcNow,
		};
	}

	// ── GenerateId ──

	[Fact]
	public void GenerateId_ProducesDeterministicIds()
	{
		var id1 = ProfileStore.GenerateId("My Profile");
		var id2 = ProfileStore.GenerateId("My Profile");

		id1.Should().Be(id2);
	}

	[Fact]
	public void GenerateId_SanitizesName()
	{
		var id = ProfileStore.GenerateId("My Profile!");

		id.Should().NotContain(" ");
		id.Should().NotContain("!");
		id.Should().StartWith("my-profile-");
	}

	[Fact]
	public void GenerateId_DifferentNamesProduceDifferentIds()
	{
		var id1 = ProfileStore.GenerateId("Profile A");
		var id2 = ProfileStore.GenerateId("Profile B");

		id1.Should().NotBe(id2);
	}

	// ── Save & Get ──

	[Fact]
	public void Save_ThenGet_ReturnsProfile()
	{
		var store = CreateStore();
		var profile = CreateTestProfile();

		store.Save(profile);

		var retrieved = store.Get(profile.Id);
		retrieved.Should().NotBeNull();
		retrieved!.Name.Should().Be(profile.Name);
		retrieved.Id.Should().Be(profile.Id);
		retrieved.IsActive.Should().Be(profile.IsActive);
	}

	[Fact]
	public void Get_NonExistent_ReturnsNull()
	{
		var store = CreateStore();
		store.Get("non-existent").Should().BeNull();
	}

	[Fact]
	public void GetAll_ReturnsAllSavedProfiles()
	{
		var store = CreateStore();
		var p1 = CreateTestProfile("Profile Alpha");
		var p2 = CreateTestProfile("Profile Beta");

		store.Save(p1);
		store.Save(p2);

		store.GetAll().Should().HaveCount(2);
	}

	[Fact]
	public void Count_ReflectsStoredProfiles()
	{
		var store = CreateStore();
		store.Count.Should().Be(0);

		store.Save(CreateTestProfile("A"));
		store.Count.Should().Be(1);

		store.Save(CreateTestProfile("B"));
		store.Count.Should().Be(2);
	}

	// ── Remove ──

	[Fact]
	public void Remove_ExistingProfile_ReturnsTrue()
	{
		var store = CreateStore();
		var profile = CreateTestProfile();
		store.Save(profile);

		var result = store.Remove(profile.Id);

		result.Should().BeTrue();
		store.Get(profile.Id).Should().BeNull();
		store.Count.Should().Be(0);
	}

	[Fact]
	public void Remove_NonExistent_ReturnsFalse()
	{
		var store = CreateStore();
		store.Remove("non-existent").Should().BeFalse();
	}

	[Fact]
	public void Remove_DeletesFileFromDisk()
	{
		var store = CreateStore();
		var profile = CreateTestProfile();
		store.Save(profile);

		// Verify file exists
		var filePath = Path.Combine(_tempDir, "profiles", $"{profile.Id}.json");
		File.Exists(filePath).Should().BeTrue();

		store.Remove(profile.Id);

		File.Exists(filePath).Should().BeFalse();
	}

	// ── Persistence / LoadAll ──

	[Fact]
	public void LoadAll_RestoresProfilesFromDisk()
	{
		// Save profiles with one store instance
		var store1 = CreateStore();
		var p1 = CreateTestProfile("Profile One");
		var p2 = CreateTestProfile("Profile Two");
		store1.Save(p1);
		store1.Save(p2);

		// Create a new store instance and load
		var store2 = CreateStore();
		var loaded = store2.LoadAll();

		loaded.Should().HaveCount(2);
		store2.Get(p1.Id).Should().NotBeNull();
		store2.Get(p2.Id).Should().NotBeNull();
	}

	[Fact]
	public void Save_UpdatesExistingProfile()
	{
		var store = CreateStore();
		var profile = CreateTestProfile();
		store.Save(profile);

		profile.IsActive = true;
		profile.Name = "Updated Name";
		store.Save(profile);

		var retrieved = store.Get(profile.Id);
		retrieved!.IsActive.Should().BeTrue();
		retrieved.Name.Should().Be("Updated Name");
		store.Count.Should().Be(1);
	}

	// ── History ──

	[Fact]
	public void AppendHistory_ThenGetHistory_ReturnsEntries()
	{
		var store = CreateStore();
		var profileId = "test-profile-123";

		store.AppendHistory(profileId, new ProfileHistoryEntry
		{
			Action = "activated",
			Timestamp = DateTimeOffset.UtcNow,
			Trigger = "manual",
			OrchestrationsActivated = ["orch-1", "orch-2"],
			OrchestrationsDeactivated = [],
		});

		store.AppendHistory(profileId, new ProfileHistoryEntry
		{
			Action = "deactivated",
			Timestamp = DateTimeOffset.UtcNow,
			Trigger = "schedule",
			OrchestrationsActivated = [],
			OrchestrationsDeactivated = ["orch-1", "orch-2"],
		});

		var history = store.GetHistory(profileId);
		history.Should().HaveCount(2);
		history[0].Action.Should().Be("activated");
		history[1].Action.Should().Be("deactivated");
	}

	[Fact]
	public void GetHistory_NoHistory_ReturnsEmpty()
	{
		var store = CreateStore();
		store.GetHistory("non-existent").Should().BeEmpty();
	}

	[Fact]
	public void AppendHistory_EnforcesMaxEntries()
	{
		var store = CreateStore();
		var profileId = "test-profile-overflow";

		// Append 510 entries (max is 500)
		for (var i = 0; i < 510; i++)
		{
			store.AppendHistory(profileId, new ProfileHistoryEntry
			{
				Action = "activated",
				Timestamp = DateTimeOffset.UtcNow.AddMinutes(i),
				Trigger = $"test-{i}",
			});
		}

		var history = store.GetHistory(profileId);
		history.Should().HaveCount(500);

		// Oldest entries should have been trimmed, keeping last 500
		history[0].Trigger.Should().Be("test-10");
	}
}
