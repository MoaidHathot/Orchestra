using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Orchestra.Engine;
using Orchestra.Host.Profiles;
using Orchestra.Host.Registry;
using Xunit;

namespace Orchestra.Host.Tests;

/// <summary>
/// Unit tests for ProfileManager — CRUD, activation, effective set computation,
/// default profile auto-creation, and schedule-based transitions.
/// </summary>
public class ProfileManagerTests : IDisposable
{
	private readonly string _tempDir;
	private readonly string _orchestrationsDir;
	private readonly OrchestrationRegistry _registry;
	private readonly ProfileStore _profileStore;
	private readonly OrchestrationTagStore _tagStore;

	public ProfileManagerTests()
	{
		_tempDir = Path.Combine(Path.GetTempPath(), $"orchestra-profilemgr-tests-{Guid.NewGuid():N}");
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

	private string RegisterOrchestration(string name, string[]? tags = null)
	{
		var tagsJson = tags is { Length: > 0 }
			? $", \"tags\": [{string.Join(", ", tags.Select(t => $"\"{t}\""))}]"
			: "";

		var json = $$"""
		{
			"name": "{{name}}",
			"description": "Test orchestration: {{name}}",
			"version": "1.0.0",
			"model": "claude-opus-4.5"{{tagsJson}},
			"steps": [
				{
					"name": "step1",
					"type": "prompt",
					"systemPrompt": "Test.",
					"userPrompt": "Test.",
					"model": "claude-opus-4.5"
				}
			]
		}
		""";

		var path = Path.Combine(_orchestrationsDir, $"{name}.json");
		File.WriteAllText(path, json);
		var entry = _registry.Register(path, null);
		return entry.Id;
	}

	// ── CRUD ──

	[Fact]
	public void CreateProfile_ReturnsNewProfile()
	{
		var mgr = CreateManager();

		var profile = mgr.CreateProfile("On-Call", "On-call profile", new ProfileFilter { Tags = ["on-call"] });

		profile.Should().NotBeNull();
		profile!.Name.Should().Be("On-Call");
		profile.IsActive.Should().BeFalse();
		profile.Filter.Tags.Should().Contain("on-call");
	}

	[Fact]
	public void CreateProfile_DuplicateName_ReturnsNull()
	{
		var mgr = CreateManager();
		mgr.CreateProfile("On-Call", null, new ProfileFilter { Tags = ["on-call"] });

		var duplicate = mgr.CreateProfile("On-Call", null, new ProfileFilter { Tags = ["other"] });

		duplicate.Should().BeNull();
	}

	[Fact]
	public void GetAllProfiles_ReturnsAll()
	{
		var mgr = CreateManager();
		mgr.CreateProfile("A", null, new ProfileFilter { Tags = ["a"] });
		mgr.CreateProfile("B", null, new ProfileFilter { Tags = ["b"] });

		mgr.GetAllProfiles().Should().HaveCount(2);
	}

	[Fact]
	public void GetProfile_ExistingId_ReturnsProfile()
	{
		var mgr = CreateManager();
		var created = mgr.CreateProfile("My Profile", null, new ProfileFilter { Tags = ["test"] });

		var fetched = mgr.GetProfile(created!.Id);

		fetched.Should().NotBeNull();
		fetched!.Name.Should().Be("My Profile");
	}

	[Fact]
	public void GetProfile_NonExistent_ReturnsNull()
	{
		var mgr = CreateManager();
		mgr.GetProfile("non-existent").Should().BeNull();
	}

	[Fact]
	public void UpdateProfile_ChangesFields()
	{
		var mgr = CreateManager();
		var profile = mgr.CreateProfile("Original", "Desc", new ProfileFilter { Tags = ["test"] });

		var updated = mgr.UpdateProfile(profile!.Id, "Updated Name", "New Desc",
			new ProfileFilter { Tags = ["new-tag"] }, null);

		updated.Should().NotBeNull();
		updated!.Name.Should().Be("Updated Name");
		updated.Description.Should().Be("New Desc");
		updated.Filter.Tags.Should().Contain("new-tag");
	}

	[Fact]
	public void UpdateProfile_NonExistent_ReturnsNull()
	{
		var mgr = CreateManager();
		mgr.UpdateProfile("bad-id", "X", null, null, null).Should().BeNull();
	}

	[Fact]
	public void DeleteProfile_RemovesProfile()
	{
		var mgr = CreateManager();
		var profile = mgr.CreateProfile("To Delete", null, new ProfileFilter { Tags = ["test"] });

		mgr.DeleteProfile(profile!.Id).Should().BeTrue();
		mgr.GetProfile(profile.Id).Should().BeNull();
	}

	[Fact]
	public void DeleteProfile_NonExistent_ReturnsFalse()
	{
		var mgr = CreateManager();
		mgr.DeleteProfile("non-existent").Should().BeFalse();
	}

	// ── Activation / Deactivation ──

	[Fact]
	public void ActivateProfile_SetsIsActive()
	{
		var mgr = CreateManager();
		var profile = mgr.CreateProfile("My Profile", null, new ProfileFilter { Tags = ["test"] });

		mgr.ActivateProfile(profile!.Id).Should().BeTrue();

		mgr.GetProfile(profile.Id)!.IsActive.Should().BeTrue();
	}

	[Fact]
	public void DeactivateProfile_ClearsIsActive()
	{
		var mgr = CreateManager();
		var profile = mgr.CreateProfile("My Profile", null, new ProfileFilter { Tags = ["test"] });
		mgr.ActivateProfile(profile!.Id);

		mgr.DeactivateProfile(profile.Id).Should().BeTrue();

		mgr.GetProfile(profile.Id)!.IsActive.Should().BeFalse();
	}

	[Fact]
	public void ActivateProfile_AlreadyActive_ReturnsTrue()
	{
		var mgr = CreateManager();
		var profile = mgr.CreateProfile("My Profile", null, new ProfileFilter { Tags = ["test"] });
		mgr.ActivateProfile(profile!.Id);

		mgr.ActivateProfile(profile.Id).Should().BeTrue();
	}

	[Fact]
	public void DeactivateProfile_AlreadyInactive_ReturnsTrue()
	{
		var mgr = CreateManager();
		var profile = mgr.CreateProfile("My Profile", null, new ProfileFilter { Tags = ["test"] });

		mgr.DeactivateProfile(profile!.Id).Should().BeTrue();
	}

	// ── Effective Active Set Computation ──

	[Fact]
	public void ComputeEffectiveActiveSet_NoActiveProfiles_ReturnsEmpty()
	{
		var mgr = CreateManager();
		RegisterOrchestration("Test Orch");
		mgr.CreateProfile("P1", null, new ProfileFilter { Tags = ["*"] });

		var activeSet = mgr.ComputeEffectiveActiveSet();
		activeSet.Should().BeEmpty();
	}

	[Fact]
	public void ComputeEffectiveActiveSet_WildcardProfile_MatchesAll()
	{
		var mgr = CreateManager();
		var id1 = RegisterOrchestration("Orch A");
		var id2 = RegisterOrchestration("Orch B");
		var profile = mgr.CreateProfile("All", null, new ProfileFilter { Tags = ["*"] });
		mgr.ActivateProfile(profile!.Id);

		var activeSet = mgr.ComputeEffectiveActiveSet();
		activeSet.Should().Contain(id1);
		activeSet.Should().Contain(id2);
	}

	[Fact]
	public void ComputeEffectiveActiveSet_TagFilter_MatchesTagged()
	{
		var mgr = CreateManager();
		var id1 = RegisterOrchestration("Orch A", ["production"]);
		var id2 = RegisterOrchestration("Orch B", ["staging"]);
		var profile = mgr.CreateProfile("Prod", null, new ProfileFilter { Tags = ["production"] });
		mgr.ActivateProfile(profile!.Id);

		var activeSet = mgr.ComputeEffectiveActiveSet();
		activeSet.Should().Contain(id1);
		activeSet.Should().NotContain(id2);
	}

	[Fact]
	public void ComputeEffectiveActiveSet_UnionSemantics_MultipleProfiles()
	{
		var mgr = CreateManager();
		var id1 = RegisterOrchestration("Orch A", ["production"]);
		var id2 = RegisterOrchestration("Orch B", ["monitoring"]);
		var id3 = RegisterOrchestration("Orch C", ["staging"]);

		var p1 = mgr.CreateProfile("Prod", null, new ProfileFilter { Tags = ["production"] });
		var p2 = mgr.CreateProfile("Monitor", null, new ProfileFilter { Tags = ["monitoring"] });
		mgr.ActivateProfile(p1!.Id);
		mgr.ActivateProfile(p2!.Id);

		var activeSet = mgr.ComputeEffectiveActiveSet();
		activeSet.Should().Contain(id1); // production
		activeSet.Should().Contain(id2); // monitoring
		activeSet.Should().NotContain(id3); // staging - not matched
	}

	[Fact]
	public void ComputeEffectiveActiveSet_HostTagsIncluded()
	{
		var mgr = CreateManager();
		var id = RegisterOrchestration("Orch NoTags");
		_tagStore.SetTags(id, ["special"]);

		var profile = mgr.CreateProfile("Special", null, new ProfileFilter { Tags = ["special"] });
		mgr.ActivateProfile(profile!.Id);

		var activeSet = mgr.ComputeEffectiveActiveSet();
		activeSet.Should().Contain(id);
	}

	[Fact]
	public void ComputeEffectiveActiveSet_ExcludeOverridesWildcard()
	{
		var mgr = CreateManager();
		var id1 = RegisterOrchestration("Orch A");
		var id2 = RegisterOrchestration("Orch B");
		var profile = mgr.CreateProfile("Almost All", null,
			new ProfileFilter { Tags = ["*"], ExcludeOrchestrationIds = [id2] });
		mgr.ActivateProfile(profile!.Id);

		var activeSet = mgr.ComputeEffectiveActiveSet();
		activeSet.Should().Contain(id1);
		activeSet.Should().NotContain(id2);
	}

	// ── Effective Active Set Events ──

	[Fact]
	public void ActivateProfile_EmitsChangeEvent()
	{
		var mgr = CreateManager();
		var orchId = RegisterOrchestration("Event Orch", ["test"]);
		var profile = mgr.CreateProfile("P1", null, new ProfileFilter { Tags = ["test"] });

		EffectiveActiveSetChangedEvent? receivedEvent = null;
		mgr.OnEffectiveActiveSetChanged += evt => receivedEvent = evt;

		mgr.ActivateProfile(profile!.Id);

		receivedEvent.Should().NotBeNull();
		receivedEvent!.ActivatedOrchestrationIds.Should().Contain(orchId);
	}

	[Fact]
	public void DeactivateProfile_EmitsChangeEventWithDeactivated()
	{
		var mgr = CreateManager();
		var orchId = RegisterOrchestration("Event Orch", ["test"]);
		var profile = mgr.CreateProfile("P1", null, new ProfileFilter { Tags = ["test"] });
		mgr.ActivateProfile(profile!.Id);

		EffectiveActiveSetChangedEvent? receivedEvent = null;
		mgr.OnEffectiveActiveSetChanged += evt => receivedEvent = evt;

		mgr.DeactivateProfile(profile.Id);

		receivedEvent.Should().NotBeNull();
		receivedEvent!.DeactivatedOrchestrationIds.Should().Contain(orchId);
	}

	// ── Default Profile ──

	[Fact]
	public void Initialize_WithOrchestrations_CreatesDefaultProfile()
	{
		RegisterOrchestration("Orch A");
		var mgr = CreateManager();

		mgr.Initialize();

		var profiles = mgr.GetAllProfiles();
		profiles.Should().HaveCount(1);
		profiles.First().Name.Should().Be(ProfileManager.DefaultProfileName);
		profiles.First().IsActive.Should().BeTrue();
		profiles.First().Filter.IsWildcard.Should().BeTrue();
	}

	[Fact]
	public void Initialize_NoOrchestrations_NoDefaultProfile()
	{
		var mgr = CreateManager();

		mgr.Initialize();

		mgr.GetAllProfiles().Should().BeEmpty();
	}

	[Fact]
	public void DeleteLastProfile_RecreatesDefault_WhenOrchestrationsExist()
	{
		RegisterOrchestration("Orch A");
		var mgr = CreateManager();
		mgr.Initialize(); // Creates default profile

		var defaultProfile = mgr.GetAllProfiles().First();
		mgr.DeleteProfile(defaultProfile.Id);

		// Default profile should be auto-recreated
		mgr.GetAllProfiles().Should().HaveCount(1);
		mgr.GetAllProfiles().First().Filter.IsWildcard.Should().BeTrue();
	}

	// ── GetOrchestrationsByProfile ──

	[Fact]
	public void GetOrchestrationsByProfile_ReturnsMatchingEntries()
	{
		var mgr = CreateManager();
		RegisterOrchestration("Orch Prod", ["production"]);
		RegisterOrchestration("Orch Dev", ["development"]);
		var profile = mgr.CreateProfile("Prod", null, new ProfileFilter { Tags = ["production"] });

		var matches = mgr.GetOrchestrationsByProfile(profile!.Id);

		matches.Should().HaveCount(1);
		matches.First().Orchestration.Name.Should().Be("Orch Prod");
	}

	[Fact]
	public void GetOrchestrationsByProfile_NonExistent_ReturnsEmpty()
	{
		var mgr = CreateManager();
		mgr.GetOrchestrationsByProfile("bad-id").Should().BeEmpty();
	}

	// ── GetProfilesForOrchestration ──

	[Fact]
	public void GetProfilesForOrchestration_ReturnsMatchingProfiles()
	{
		var mgr = CreateManager();
		var orchId = RegisterOrchestration("Orch Multi", ["production", "monitoring"]);
		mgr.CreateProfile("Prod", null, new ProfileFilter { Tags = ["production"] });
		mgr.CreateProfile("Monitor", null, new ProfileFilter { Tags = ["monitoring"] });
		mgr.CreateProfile("Staging", null, new ProfileFilter { Tags = ["staging"] });

		var profiles = mgr.GetProfilesForOrchestration(orchId);

		profiles.Should().HaveCount(2);
		profiles.Select(p => p.Name).Should().BeEquivalentTo(["Prod", "Monitor"]);
	}

	// ── History ──

	[Fact]
	public void ActivateDeactivate_RecordsHistory()
	{
		var mgr = CreateManager();
		RegisterOrchestration("Orch A", ["test"]);
		var profile = mgr.CreateProfile("P1", null, new ProfileFilter { Tags = ["test"] });

		mgr.ActivateProfile(profile!.Id);
		mgr.DeactivateProfile(profile.Id);

		var history = mgr.GetProfileHistory(profile.Id);
		history.Should().HaveCountGreaterThanOrEqualTo(2);
		history.Should().Contain(h => h.Action == "activated");
		history.Should().Contain(h => h.Action == "deactivated");
	}

	// ── IsOrchestrationActive ──

	[Fact]
	public void IsOrchestrationActive_ReflectsActiveSet()
	{
		var mgr = CreateManager();
		var orchId = RegisterOrchestration("Active Orch", ["test"]);
		var profile = mgr.CreateProfile("P1", null, new ProfileFilter { Tags = ["test"] });

		mgr.IsOrchestrationActive(orchId).Should().BeFalse();

		mgr.ActivateProfile(profile!.Id);
		mgr.IsOrchestrationActive(orchId).Should().BeTrue();

		mgr.DeactivateProfile(profile.Id);
		mgr.IsOrchestrationActive(orchId).Should().BeFalse();
	}

	// ── RefreshEffectiveActiveSet ──

	[Fact]
	public void RefreshEffectiveActiveSet_RecomputesAfterExternalChange()
	{
		var mgr = CreateManager();
		var orchId = RegisterOrchestration("Orch A");
		var profile = mgr.CreateProfile("Tag Based", null, new ProfileFilter { Tags = ["special"] });
		mgr.ActivateProfile(profile!.Id);

		// Initially not matched (no "special" tag)
		mgr.IsOrchestrationActive(orchId).Should().BeFalse();

		// Add the tag externally
		_tagStore.SetTags(orchId, ["special"]);
		mgr.RefreshEffectiveActiveSet("tags-changed");

		// Now it should match
		mgr.IsOrchestrationActive(orchId).Should().BeTrue();
	}
}
