using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Orchestra.Engine;
using Orchestra.Host.Profiles;
using Orchestra.Host.Registry;
using Xunit;

namespace Orchestra.Host.Tests;

/// <summary>
/// Unit tests for profile import and export functionality
/// in <see cref="ProfileManager"/>.
/// </summary>
public class ProfileImportExportTests : IDisposable
{
	private readonly string _tempDir;
	private readonly string _orchestrationsDir;
	private readonly OrchestrationRegistry _registry;
	private readonly ProfileStore _profileStore;
	private readonly OrchestrationTagStore _tagStore;

	public ProfileImportExportTests()
	{
		_tempDir = Path.Combine(Path.GetTempPath(), $"orchestra-importexport-tests-{Guid.NewGuid():N}");
		Directory.CreateDirectory(_tempDir);
		_orchestrationsDir = Path.Combine(_tempDir, "orchestrations");
		Directory.CreateDirectory(_orchestrationsDir);

		var registryPersistPath = Path.Combine(_tempDir, "registered-orchestrations.json");
		_registry = new OrchestrationRegistry(persistPath: registryPersistPath, dataPath: _tempDir);
		_profileStore = new ProfileStore(_tempDir, NullLogger<ProfileStore>.Instance);
		_tagStore = new OrchestrationTagStore(_tempDir, NullLogger<OrchestrationTagStore>.Instance);
	}

	public void Dispose()
	{
		if (Directory.Exists(_tempDir))
		{
			try { Directory.Delete(_tempDir, recursive: true); }
			catch { /* best-effort cleanup */ }
		}
	}

	private ProfileManager CreateManager() =>
		new(_profileStore, _tagStore, _registry, NullLogger<ProfileManager>.Instance);

	private Profile CreateTestProfile(string name = "Test Profile", bool isActive = true)
	{
		var id = ProfileStore.GenerateId(name);
		return new Profile
		{
			Id = id,
			Name = name,
			Description = "A test profile for import/export",
			IsActive = isActive,
			Filter = new ProfileFilter { Tags = ["test"] },
			CreatedAt = DateTimeOffset.UtcNow,
			UpdatedAt = DateTimeOffset.UtcNow,
		};
	}

	// ── Import ──

	[Fact]
	public void ImportProfile_NewProfile_ImportsSuccessfully()
	{
		var manager = CreateManager();
		var profile = CreateTestProfile("Import Me");

		var result = manager.ImportProfile(profile, overwriteExisting: false);

		result.Imported.Should().BeTrue();
		result.Id.Should().Be(profile.Id);
		result.Name.Should().Be("Import Me");
		result.SkipReason.Should().BeNull();
	}

	[Fact]
	public void ImportProfile_ForcesInactive()
	{
		var manager = CreateManager();
		var profile = CreateTestProfile("Active Import", isActive: true);
		profile.ActivatedAt = DateTimeOffset.UtcNow;

		manager.ImportProfile(profile, overwriteExisting: false);

		var stored = _profileStore.Get(profile.Id);
		stored.Should().NotBeNull();
		stored!.IsActive.Should().BeFalse();
		stored.ActivatedAt.Should().BeNull();
		stored.DeactivatedAt.Should().BeNull();
	}

	[Fact]
	public void ImportProfile_DuplicateWithoutOverwrite_Skips()
	{
		var manager = CreateManager();
		var profile = CreateTestProfile("Duplicate Test");

		// First import
		manager.ImportProfile(profile, overwriteExisting: false);

		// Second import without overwrite
		var result = manager.ImportProfile(profile, overwriteExisting: false);

		result.Imported.Should().BeFalse();
		result.SkipReason.Should().Contain("already exists");
	}

	[Fact]
	public void ImportProfile_DuplicateWithOverwrite_Overwrites()
	{
		var manager = CreateManager();
		var profile = CreateTestProfile("Overwrite Test");
		profile.Description = "Original";
		manager.ImportProfile(profile, overwriteExisting: false);

		// Update description and re-import with overwrite
		var updated = CreateTestProfile("Overwrite Test");
		updated.Description = "Updated";
		var result = manager.ImportProfile(updated, overwriteExisting: true);

		result.Imported.Should().BeTrue();
		var stored = _profileStore.Get(profile.Id);
		stored!.Description.Should().Be("Updated");
	}

	[Fact]
	public void ImportProfile_SetsUpdatedAtToNow()
	{
		var manager = CreateManager();
		var profile = CreateTestProfile("Timestamp Test");
		var oldTimestamp = DateTimeOffset.UtcNow.AddDays(-30);
		profile.UpdatedAt = oldTimestamp;

		manager.ImportProfile(profile, overwriteExisting: false);

		var stored = _profileStore.Get(profile.Id);
		stored!.UpdatedAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5));
	}

	// ── Export ──

	[Fact]
	public void ExportProfiles_AllProfiles_ExportsAll()
	{
		var manager = CreateManager();
		var p1 = CreateTestProfile("Profile One");
		var p2 = CreateTestProfile("Profile Two");
		_profileStore.Save(p1);
		_profileStore.Save(p2);

		var exportDir = Path.Combine(_tempDir, "export-all");
		var results = manager.ExportProfiles(exportDir, profileIds: null, overwriteExisting: false);

		results.Should().HaveCount(2);
		results.Should().OnlyContain(r => r.Exported);
		File.Exists(Path.Combine(exportDir, $"{p1.Id}.json")).Should().BeTrue();
		File.Exists(Path.Combine(exportDir, $"{p2.Id}.json")).Should().BeTrue();
	}

	[Fact]
	public void ExportProfiles_SpecificIds_ExportsOnlyMatching()
	{
		var manager = CreateManager();
		var p1 = CreateTestProfile("Export One");
		var p2 = CreateTestProfile("Export Two");
		_profileStore.Save(p1);
		_profileStore.Save(p2);

		var exportDir = Path.Combine(_tempDir, "export-specific");
		var results = manager.ExportProfiles(exportDir, profileIds: [p1.Id], overwriteExisting: false);

		results.Should().HaveCount(1);
		results[0].Id.Should().Be(p1.Id);
		results[0].Exported.Should().BeTrue();
		File.Exists(Path.Combine(exportDir, $"{p1.Id}.json")).Should().BeTrue();
		File.Exists(Path.Combine(exportDir, $"{p2.Id}.json")).Should().BeFalse();
	}

	[Fact]
	public void ExportProfiles_ExistingFileWithoutOverwrite_Skips()
	{
		var manager = CreateManager();
		var profile = CreateTestProfile("No Overwrite Export");
		_profileStore.Save(profile);

		var exportDir = Path.Combine(_tempDir, "export-no-overwrite");
		Directory.CreateDirectory(exportDir);
		File.WriteAllText(Path.Combine(exportDir, $"{profile.Id}.json"), "existing");

		var results = manager.ExportProfiles(exportDir, profileIds: null, overwriteExisting: false);

		results.Should().HaveCount(1);
		results[0].Exported.Should().BeFalse();
		results[0].SkipReason.Should().Contain("already exists");
		File.ReadAllText(Path.Combine(exportDir, $"{profile.Id}.json")).Should().Be("existing");
	}

	[Fact]
	public void ExportProfiles_ExistingFileWithOverwrite_Overwrites()
	{
		var manager = CreateManager();
		var profile = CreateTestProfile("Overwrite Export");
		_profileStore.Save(profile);

		var exportDir = Path.Combine(_tempDir, "export-overwrite");
		Directory.CreateDirectory(exportDir);
		File.WriteAllText(Path.Combine(exportDir, $"{profile.Id}.json"), "old content");

		var results = manager.ExportProfiles(exportDir, profileIds: null, overwriteExisting: true);

		results.Should().HaveCount(1);
		results[0].Exported.Should().BeTrue();
		var content = File.ReadAllText(Path.Combine(exportDir, $"{profile.Id}.json"));
		content.Should().NotBe("old content");
		content.Should().Contain("Overwrite Export");
	}

	[Fact]
	public void ExportProfiles_CreatesDirectoryIfNotExists()
	{
		var manager = CreateManager();
		var profile = CreateTestProfile("Dir Creation");
		_profileStore.Save(profile);

		var exportDir = Path.Combine(_tempDir, "new-export-dir", "sub");
		Directory.Exists(exportDir).Should().BeFalse();

		manager.ExportProfiles(exportDir, profileIds: null, overwriteExisting: false);

		Directory.Exists(exportDir).Should().BeTrue();
		File.Exists(Path.Combine(exportDir, $"{profile.Id}.json")).Should().BeTrue();
	}

	[Fact]
	public void ExportProfiles_EmptyStore_ReturnsEmpty()
	{
		var manager = CreateManager();
		var exportDir = Path.Combine(_tempDir, "export-empty");

		var results = manager.ExportProfiles(exportDir, profileIds: null, overwriteExisting: false);

		results.Should().BeEmpty();
	}

	// ── Round-trip: Export then Import ──

	[Fact]
	public void RoundTrip_ExportThenImport_PreservesProfileData()
	{
		var manager = CreateManager();
		var profile = CreateTestProfile("Round Trip");
		profile.Filter = new ProfileFilter
		{
			Tags = ["production", "critical"],
			OrchestrationIds = [],
			ExcludeOrchestrationIds = [],
		};
		profile.Schedule = new ProfileSchedule
		{
			Timezone = "America/New_York",
			Windows =
			[
				new ScheduleWindow
				{
					Days = ["Monday", "Friday"],
					StartTime = "09:00",
					EndTime = "17:00",
				}
			],
		};
		_profileStore.Save(profile);

		// Export
		var exportDir = Path.Combine(_tempDir, "roundtrip");
		manager.ExportProfiles(exportDir, profileIds: null, overwriteExisting: false);

		// Create a fresh store / manager
		var freshTempDir = Path.Combine(_tempDir, "roundtrip-fresh");
		Directory.CreateDirectory(freshTempDir);
		var freshStore = new ProfileStore(freshTempDir, NullLogger<ProfileStore>.Instance);
		var freshTagStore = new OrchestrationTagStore(freshTempDir, NullLogger<OrchestrationTagStore>.Instance);
		var freshRegistryPath = Path.Combine(freshTempDir, "registered-orchestrations.json");
		var freshRegistry = new OrchestrationRegistry(persistPath: freshRegistryPath, dataPath: freshTempDir);
		var freshManager = new ProfileManager(freshStore, freshTagStore, freshRegistry, NullLogger<ProfileManager>.Instance);

		// Read the exported file and import
		var exportedFile = Path.Combine(exportDir, $"{profile.Id}.json");
		var json = File.ReadAllText(exportedFile);
		var jsonOptions = new System.Text.Json.JsonSerializerOptions
		{
			WriteIndented = true,
			PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
			DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
		};
		var imported = System.Text.Json.JsonSerializer.Deserialize<Profile>(json, jsonOptions)!;
		freshManager.ImportProfile(imported, overwriteExisting: false);

		// Verify
		var stored = freshStore.Get(profile.Id);
		stored.Should().NotBeNull();
		stored!.Name.Should().Be("Round Trip");
		stored.IsActive.Should().BeFalse(); // Import forces inactive
		stored.Filter.Tags.Should().BeEquivalentTo(["production", "critical"]);
		stored.Schedule.Should().NotBeNull();
		stored.Schedule!.Timezone.Should().Be("America/New_York");
		stored.Schedule.Windows.Should().HaveCount(1);
		stored.Schedule.Windows[0].Days.Should().BeEquivalentTo(["Monday", "Friday"]);
		stored.Schedule.Windows[0].StartTime.Should().Be("09:00");
		stored.Schedule.Windows[0].EndTime.Should().Be("17:00");
	}
}
