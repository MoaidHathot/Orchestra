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

	// ── SyncDirectory ──

	private string CreateExternalProfileDir()
	{
		var dir = Path.Combine(_tempDir, "external-profiles");
		Directory.CreateDirectory(dir);
		return dir;
	}

	private string WriteProfileFile(string directory, string name, string[]? tags = null, bool isActive = false, ProfileSchedule? schedule = null)
	{
		var id = ProfileStore.GenerateId(name);
		var tagsJson = tags is not null
			? string.Join(", ", tags.Select(t => $"\"{t}\""))
			: "\"test\"";
		var scheduleJson = schedule is not null
			? $$"""
			,
				"schedule": {
					"windows": [{{string.Join(",", schedule.Windows.Select(w =>
						$$"""
						{
							"days": [{{string.Join(", ", w.Days.Select(d => $"\"{d}\""))}}],
							"startTime": "{{w.StartTime}}",
							"endTime": "{{w.EndTime}}"
						}
						"""))}}]
				}
			"""
			: "";
		var json = $$"""
		{
			"id": "{{id}}",
			"name": "{{name}}",
			"description": "Profile for {{name}}",
			"isActive": {{isActive.ToString().ToLowerInvariant()}},
			"filter": {
				"tags": [{{tagsJson}}],
				"orchestrationIds": [],
				"excludeOrchestrationIds": []
			}{{scheduleJson}},
			"createdAt": "2026-01-01T00:00:00+00:00",
			"updatedAt": "2026-01-01T00:00:00+00:00"
		}
		""";
		var path = Path.Combine(directory, $"{name}.json");
		File.WriteAllText(path, json);
		return path;
	}

	[Fact]
	public void SyncDirectory_ImportsNewProfiles()
	{
		var store = CreateStore();
		var externalDir = CreateExternalProfileDir();

		WriteProfileFile(externalDir, "Alpha", tags: ["tag-a"]);
		WriteProfileFile(externalDir, "Beta", tags: ["tag-b"]);

		var result = store.SyncDirectory(externalDir);

		result.Added.Should().Be(2);
		result.Updated.Should().Be(0);
		result.Removed.Should().Be(0);
		result.Failed.Should().Be(0);
		store.Count.Should().Be(2);
	}

	[Fact]
	public void SyncDirectory_ImportsNewProfilesAsInactive()
	{
		var store = CreateStore();
		var externalDir = CreateExternalProfileDir();

		// Write profile with isActive=true in the JSON
		WriteProfileFile(externalDir, "ActiveProfile", isActive: true);

		store.SyncDirectory(externalDir);

		// Profile should be imported as inactive regardless of the file's isActive value
		var profiles = store.GetAll();
		profiles.Should().HaveCount(1);
		profiles.First().IsActive.Should().BeFalse();
	}

	[Fact]
	public void SyncDirectory_SetsSourcePath()
	{
		var store = CreateStore();
		var externalDir = CreateExternalProfileDir();

		var filePath = WriteProfileFile(externalDir, "Tracked");

		store.SyncDirectory(externalDir);

		var profile = store.GetAll().First();
		profile.SourcePath.Should().Be(Path.GetFullPath(filePath));
	}

	[Fact]
	public void SyncDirectory_SetsContentHash()
	{
		var store = CreateStore();
		var externalDir = CreateExternalProfileDir();

		WriteProfileFile(externalDir, "Hashed");

		store.SyncDirectory(externalDir);

		var profile = store.GetAll().First();
		profile.ContentHash.Should().NotBeNullOrEmpty();
	}

	[Fact]
	public void SyncDirectory_DetectsChangedProfiles()
	{
		var store = CreateStore();
		var externalDir = CreateExternalProfileDir();

		// Initial sync
		WriteProfileFile(externalDir, "Evolving", tags: ["v1"]);
		store.SyncDirectory(externalDir);
		store.Count.Should().Be(1);
		store.GetAll().First().Filter.Tags.Should().Contain("v1");

		// Modify the file
		WriteProfileFile(externalDir, "Evolving", tags: ["v2"]);

		var result = store.SyncDirectory(externalDir);

		result.Updated.Should().Be(1);
		result.Added.Should().Be(0);
		store.Count.Should().Be(1);
		store.GetAll().First().Filter.Tags.Should().Contain("v2");
	}

	[Fact]
	public void SyncDirectory_PreservesActivationStateOnUpdate()
	{
		var store = CreateStore();
		var externalDir = CreateExternalProfileDir();

		// Initial sync
		WriteProfileFile(externalDir, "Preserved");
		store.SyncDirectory(externalDir);

		// Activate the profile manually
		var profile = store.GetAll().First();
		profile.IsActive = true;
		profile.ActivatedAt = DateTimeOffset.UtcNow;
		profile.ActivationTrigger = "manual";
		store.Save(profile);

		// Modify the file and re-sync
		WriteProfileFile(externalDir, "Preserved", tags: ["changed"]);
		store.SyncDirectory(externalDir);

		// Activation state should be preserved
		var updated = store.GetAll().First();
		updated.IsActive.Should().BeTrue();
		updated.ActivationTrigger.Should().Be("manual");
		updated.Filter.Tags.Should().Contain("changed");
	}

	[Fact]
	public void SyncDirectory_SkipsUnchangedProfiles()
	{
		var store = CreateStore();
		var externalDir = CreateExternalProfileDir();

		WriteProfileFile(externalDir, "Stable");

		store.SyncDirectory(externalDir);
		var result = store.SyncDirectory(externalDir);

		result.Unchanged.Should().Be(1);
		result.Added.Should().Be(0);
		result.Updated.Should().Be(0);
	}

	[Fact]
	public void SyncDirectory_RemovesDeletedProfiles()
	{
		var store = CreateStore();
		var externalDir = CreateExternalProfileDir();

		var filePath = WriteProfileFile(externalDir, "Ephemeral");
		store.SyncDirectory(externalDir);
		store.Count.Should().Be(1);

		// Delete the source file
		File.Delete(filePath);

		var result = store.SyncDirectory(externalDir);

		result.Removed.Should().Be(1);
		store.Count.Should().Be(0);
	}

	[Fact]
	public void SyncDirectory_DoesNotRemoveProfilesFromOtherSources()
	{
		var store = CreateStore();
		var externalDir = CreateExternalProfileDir();

		// Manually create a profile that's NOT from the external dir
		var manualProfile = CreateTestProfile("Manual");
		store.Save(manualProfile);

		// Sync from external dir (no files there)
		var result = store.SyncDirectory(externalDir);

		// Manual profile should NOT be removed (it has no SourcePath in external dir)
		result.Removed.Should().Be(0);
		store.Count.Should().Be(1);
		store.Get(manualProfile.Id).Should().NotBeNull();
	}

	[Fact]
	public void SyncDirectory_NonExistentDirectory_ReturnsEmpty()
	{
		var store = CreateStore();

		var result = store.SyncDirectory(Path.Combine(_tempDir, "does-not-exist"));

		result.Added.Should().Be(0);
		result.Updated.Should().Be(0);
		result.Removed.Should().Be(0);
	}

	[Fact]
	public void SyncDirectory_InvalidJsonFile_CountsAsFailure()
	{
		var store = CreateStore();
		var externalDir = CreateExternalProfileDir();

		File.WriteAllText(Path.Combine(externalDir, "broken.json"), "not valid json {{{");

		var result = store.SyncDirectory(externalDir);

		result.Failed.Should().Be(1);
		result.Added.Should().Be(0);
		store.Count.Should().Be(0);
	}

	[Fact]
	public void SyncDirectory_ProfilesPersistedToDisk()
	{
		var store = CreateStore();
		var externalDir = CreateExternalProfileDir();

		WriteProfileFile(externalDir, "Persisted");
		store.SyncDirectory(externalDir);

		// Create a new store instance and load from disk
		var store2 = CreateStore();
		var loaded = store2.LoadAll();

		loaded.Should().HaveCount(1);
		loaded.First().Name.Should().Be("Persisted");
	}

	[Fact]
	public void SyncDirectory_RegeneratesIdFromName()
	{
		var store = CreateStore();
		var externalDir = CreateExternalProfileDir();

		// Write a profile file — the ID will be regenerated from the name
		WriteProfileFile(externalDir, "My Custom Profile");
		store.SyncDirectory(externalDir);

		var expectedId = ProfileStore.GenerateId("My Custom Profile");
		var profile = store.Get(expectedId);
		profile.Should().NotBeNull();
		profile!.Name.Should().Be("My Custom Profile");
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
