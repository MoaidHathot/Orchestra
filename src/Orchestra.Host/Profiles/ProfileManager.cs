using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orchestra.Host.Registry;

namespace Orchestra.Host.Profiles;

/// <summary>
/// Background service that manages profile lifecycle, computes effective active orchestration sets,
/// and evaluates time-window schedules using event-driven scheduling.
/// </summary>
public partial class ProfileManager : BackgroundService
{
	private readonly ProfileStore _store;
	private readonly OrchestrationTagStore _tagStore;
	private readonly OrchestrationRegistry _registry;
	private readonly ILogger<ProfileManager> _logger;

	/// <summary>
	/// Event raised when the effective active orchestration set changes.
	/// </summary>
	public event Action<EffectiveActiveSetChangedEvent>? OnEffectiveActiveSetChanged;

	/// <summary>
	/// The current set of orchestration IDs that are effectively active
	/// (matched by at least one active profile).
	/// </summary>
	private HashSet<string> _currentActiveSet = new(StringComparer.OrdinalIgnoreCase);
	private readonly object _activeSetLock = new();

	/// <summary>
	/// CancellationTokenSource used to interrupt the schedule delay when profiles change.
	/// </summary>
	private CancellationTokenSource? _scheduleInterruptCts;
	private readonly object _scheduleInterruptLock = new();

	/// <summary>
	/// Name used for the auto-created default profile.
	/// </summary>
	public const string DefaultProfileName = "Default";

	public ProfileManager(
		ProfileStore store,
		OrchestrationTagStore tagStore,
		OrchestrationRegistry registry,
		ILogger<ProfileManager> logger)
	{
		_store = store;
		_tagStore = tagStore;
		_registry = registry;
		_logger = logger;
	}

	// ── Profile CRUD ──

	/// <summary>
	/// Gets all profiles.
	/// </summary>
	public IReadOnlyCollection<Profile> GetAllProfiles() => _store.GetAll();

	/// <summary>
	/// Gets a specific profile by ID.
	/// </summary>
	public Profile? GetProfile(string id) => _store.Get(id);

	/// <summary>
	/// Creates a new profile. Returns the created profile or null if ID already exists.
	/// </summary>
	public Profile? CreateProfile(string name, string? description, ProfileFilter filter, ProfileSchedule? schedule = null)
	{
		var id = ProfileStore.GenerateId(name);
		if (_store.Get(id) is not null)
			return null;

		var now = DateTimeOffset.UtcNow;
		var profile = new Profile
		{
			Id = id,
			Name = name,
			Description = description,
			IsActive = false,
			Filter = filter,
			Schedule = schedule,
			CreatedAt = now,
			UpdatedAt = now,
		};

		_store.Save(profile);
		LogProfileCreated(id, name);

		// If this profile has a schedule, re-evaluate whether it should be active now
		if (schedule is not null)
		{
			var shouldBeActive = schedule.IsActiveAt(DateTimeOffset.UtcNow);
			if (shouldBeActive)
			{
				ActivateProfile(id, "schedule");
			}
			InterruptScheduleDelay();
		}

		return profile;
	}

	/// <summary>
	/// Updates an existing profile's name, description, filter, and/or schedule.
	/// </summary>
	public Profile? UpdateProfile(string id, string? name, string? description, ProfileFilter? filter, ProfileSchedule? schedule)
	{
		var profile = _store.Get(id);
		if (profile is null)
			return null;

		if (name is not null)
			profile.Name = name;
		if (description is not null)
			profile.Description = description;
		if (filter is not null)
			profile.Filter = filter;

		// Schedule is explicitly nullable -- null means "remove schedule"
		profile.Schedule = schedule;
		profile.UpdatedAt = DateTimeOffset.UtcNow;

		_store.Save(profile);
		LogProfileUpdated(id);

		// If profile is active, recompute effective set since filter may have changed
		if (profile.IsActive)
		{
			RecomputeEffectiveActiveSet("profile-updated");
		}

		InterruptScheduleDelay();

		return profile;
	}

	/// <summary>
	/// Deletes a profile. If active, deactivates it first.
	/// If this is the last profile and orchestrations exist, auto-creates a default profile.
	/// </summary>
	public bool DeleteProfile(string id)
	{
		var profile = _store.Get(id);
		if (profile is null)
			return false;

		// Deactivate first (captures history, recomputes active set)
		if (profile.IsActive)
		{
			DeactivateProfile(id, "profile-deleted");
		}

		_store.Remove(id);
		LogProfileDeleted(id);

		// Auto-create default profile if this was the last one and orchestrations exist
		if (_store.Count == 0 && _registry.GetAll().Any())
		{
			EnsureDefaultProfile();
		}

		return true;
	}

	// ── Activation / Deactivation ──

	/// <summary>
	/// Activates a profile. Recomputes the effective active set and notifies listeners.
	/// The trigger parameter records what caused the activation ("manual" or "schedule").
	/// Manual activations persist until the user explicitly deactivates -- the schedule
	/// evaluator will not override them.
	/// </summary>
	public bool ActivateProfile(string id, string trigger = "manual")
	{
		var profile = _store.Get(id);
		if (profile is null)
			return false;

		if (profile.IsActive)
		{
			// If already active via schedule but user is now manually activating,
			// upgrade to manual override so the schedule won't deactivate it.
			if (trigger == "manual" && profile.ActivationTrigger != "manual")
			{
				profile.ActivationTrigger = "manual";
				profile.UpdatedAt = DateTimeOffset.UtcNow;
				_store.Save(profile);
				LogProfileActivated(id, "manual-override");
			}
			return true;
		}

		profile.IsActive = true;
		profile.ActivationTrigger = trigger;
		profile.ActivatedAt = DateTimeOffset.UtcNow;
		profile.UpdatedAt = DateTimeOffset.UtcNow;
		_store.Save(profile);

		LogProfileActivated(id, trigger);

		RecomputeEffectiveActiveSet(trigger, profileIdChanged: id, activated: true);
		return true;
	}

	/// <summary>
	/// Deactivates a profile. Recomputes the effective active set and notifies listeners.
	/// Clears the activation trigger so the schedule evaluator can resume control.
	/// </summary>
	public bool DeactivateProfile(string id, string trigger = "manual")
	{
		var profile = _store.Get(id);
		if (profile is null)
			return false;

		if (!profile.IsActive)
			return true; // Already inactive

		profile.IsActive = false;
		profile.ActivationTrigger = null;
		profile.DeactivatedAt = DateTimeOffset.UtcNow;
		profile.UpdatedAt = DateTimeOffset.UtcNow;
		_store.Save(profile);

		LogProfileDeactivated(id, trigger);

		RecomputeEffectiveActiveSet(trigger, profileIdChanged: id, activated: false);
		return true;
	}

	// ── Effective Active Set ──

	/// <summary>
	/// Gets the current effective active orchestration IDs.
	/// </summary>
	public HashSet<string> GetEffectiveActiveOrchestrationIds()
	{
		lock (_activeSetLock)
		{
			return new HashSet<string>(_currentActiveSet, StringComparer.OrdinalIgnoreCase);
		}
	}

	/// <summary>
	/// Determines whether a specific orchestration is effectively active.
	/// </summary>
	public bool IsOrchestrationActive(string orchestrationId)
	{
		lock (_activeSetLock)
		{
			return _currentActiveSet.Contains(orchestrationId);
		}
	}

	/// <summary>
	/// Computes the effective active orchestration set from all active profiles.
	/// </summary>
	public HashSet<string> ComputeEffectiveActiveSet()
	{
		var activeSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		var activeProfiles = _store.GetAll().Where(p => p.IsActive).ToArray();

		foreach (var entry in _registry.GetAll())
		{
			var effectiveTags = _tagStore.GetEffectiveTags(entry.Id, entry.Orchestration.Tags);

			foreach (var profile in activeProfiles)
			{
				if (profile.Filter.Matches(entry.Id, effectiveTags))
				{
					activeSet.Add(entry.Id);
					break; // No need to check more profiles for this orchestration
				}
			}
		}

		return activeSet;
	}

	/// <summary>
	/// Recomputes the effective active set and emits change events if the set changed.
	/// </summary>
	private void RecomputeEffectiveActiveSet(string trigger, string? profileIdChanged = null, bool? activated = null)
	{
		HashSet<string> previousSet;
		HashSet<string> newSet;

		lock (_activeSetLock)
		{
			previousSet = _currentActiveSet;
			newSet = ComputeEffectiveActiveSet();
			_currentActiveSet = newSet;
		}

		// Compute diff
		var nowActive = newSet.Except(previousSet).ToArray();
		var nowInactive = previousSet.Except(newSet).ToArray();

		if (nowActive.Length == 0 && nowInactive.Length == 0)
			return;

		LogEffectiveActiveSetChanged(nowActive.Length, nowInactive.Length, trigger);

		// Record history for the profile that changed
		if (profileIdChanged is not null)
		{
			_store.AppendHistory(profileIdChanged, new ProfileHistoryEntry
			{
				Action = activated == true ? "activated" : "deactivated",
				Timestamp = DateTimeOffset.UtcNow,
				Trigger = trigger,
				OrchestrationsActivated = nowActive,
				OrchestrationsDeactivated = nowInactive,
			});
		}

		// Notify listeners
		var evt = new EffectiveActiveSetChangedEvent
		{
			ActivatedOrchestrationIds = nowActive,
			DeactivatedOrchestrationIds = nowInactive,
			Trigger = trigger,
		};

		try
		{
			OnEffectiveActiveSetChanged?.Invoke(evt);
		}
		catch (Exception ex)
		{
			LogEventHandlerFailed(ex);
		}
	}

	/// <summary>
	/// Forces a recompute of the effective active set.
	/// Call this when orchestrations are registered/unregistered or tags change.
	/// </summary>
	public void RefreshEffectiveActiveSet(string trigger = "refresh")
	{
		RecomputeEffectiveActiveSet(trigger);
	}

	// ── Default Profile ──

	/// <summary>
	/// Ensures a default profile exists with the wildcard tag "*".
	/// Called on initialization and when the last profile is deleted.
	/// </summary>
	public Profile EnsureDefaultProfile()
	{
		var existing = _store.GetAll().FirstOrDefault();
		if (existing is not null)
			return existing;

		var profile = CreateProfile(
			DefaultProfileName,
			"Matches all orchestrations",
			new ProfileFilter { Tags = ["*"] });

		if (profile is not null)
		{
			ActivateProfile(profile.Id, "auto-created");
			LogDefaultProfileCreated(profile.Id);
		}

		return profile ?? _store.GetAll().First();
	}

	// ── History ──

	/// <summary>
	/// Gets the activation/deactivation history for a profile.
	/// </summary>
	public List<ProfileHistoryEntry> GetProfileHistory(string profileId)
	{
		return _store.GetHistory(profileId);
	}

	// ── Import / Export ──

	/// <summary>
	/// Result of importing a single profile.
	/// </summary>
	public record ImportProfileResult(string Id, string Name, bool Imported, string? SkipReason = null);

	/// <summary>
	/// Result of exporting a single profile.
	/// </summary>
	public record ExportProfileResult(string Id, string Name, string? Path, bool Exported, string? SkipReason = null);

	/// <summary>
	/// Imports a profile from an already-deserialized Profile object.
	/// The profile is always imported as inactive. If overwriteExisting is false
	/// and a profile with the same ID already exists, it is skipped.
	/// </summary>
	public ImportProfileResult ImportProfile(Profile profile, bool overwriteExisting)
	{
		// Force inactive on import
		profile.IsActive = false;
		profile.ActivatedAt = null;
		profile.DeactivatedAt = null;
		profile.UpdatedAt = DateTimeOffset.UtcNow;

		if (_store.Exists(profile.Id) && !overwriteExisting)
		{
			LogProfileImportSkipped(profile.Id, profile.Name);
			return new ImportProfileResult(profile.Id, profile.Name, false, "Profile already exists");
		}

		_store.Save(profile);
		LogProfileImported(profile.Id, profile.Name);
		return new ImportProfileResult(profile.Id, profile.Name, true);
	}

	/// <summary>
	/// Exports profiles to a directory as individual JSON files.
	/// </summary>
	public List<ExportProfileResult> ExportProfiles(string directory, string[]? profileIds, bool overwriteExisting)
	{
		Directory.CreateDirectory(directory);

		var profiles = profileIds is { Length: > 0 }
			? _store.GetAll().Where(p => profileIds.Contains(p.Id, StringComparer.OrdinalIgnoreCase)).ToArray()
			: _store.GetAll().ToArray();

		var results = new List<ExportProfileResult>();

		foreach (var profile in profiles)
		{
			var fileName = $"{profile.Id}.json";
			var filePath = Path.Combine(directory, fileName);

			if (File.Exists(filePath) && !overwriteExisting)
			{
				results.Add(new ExportProfileResult(profile.Id, profile.Name, filePath, false, "File already exists"));
				LogProfileExportSkipped(profile.Id, filePath);
				continue;
			}

			try
			{
				var json = System.Text.Json.JsonSerializer.Serialize(profile, ProfileStore.JsonOptions);
				File.WriteAllText(filePath, json);
				results.Add(new ExportProfileResult(profile.Id, profile.Name, filePath, true));
				LogProfileExported(profile.Id, filePath);
			}
			catch (Exception ex)
			{
				results.Add(new ExportProfileResult(profile.Id, profile.Name, filePath, false, ex.Message));
				LogProfileExportFailed(ex, profile.Id);
			}
		}

		return results;
	}

	/// <summary>
	/// Gets the profiles that an orchestration belongs to (matches the filter).
	/// </summary>
	public IReadOnlyCollection<Profile> GetProfilesForOrchestration(string orchestrationId)
	{
		var entry = _registry.Get(orchestrationId);
		if (entry is null)
			return [];

		var effectiveTags = _tagStore.GetEffectiveTags(orchestrationId, entry.Orchestration.Tags);

		return _store.GetAll()
			.Where(p => p.Filter.Matches(orchestrationId, effectiveTags))
			.ToArray();
	}

	/// <summary>
	/// Gets all orchestration entries that match a specific profile.
	/// </summary>
	public IReadOnlyCollection<OrchestrationEntry> GetOrchestrationsByProfile(string profileId)
	{
		var profile = _store.Get(profileId);
		if (profile is null)
			return [];

		return _registry.GetAll()
			.Where(e =>
			{
				var effectiveTags = _tagStore.GetEffectiveTags(e.Id, e.Orchestration.Tags);
				return profile.Filter.Matches(e.Id, effectiveTags);
			})
			.ToArray();
	}

	// ── Schedule Evaluation (BackgroundService) ──

	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		LogScheduleEvaluationStarted();

		while (!stoppingToken.IsCancellationRequested)
		{
			try
			{
				EvaluateSchedules();
			}
			catch (Exception ex) when (ex is not OperationCanceledException)
			{
				LogScheduleEvaluationError(ex);
			}

			// Compute next transition time
			var nextTransition = ComputeNextScheduleTransition();

			if (nextTransition is null)
			{
				// No scheduled profiles; wait until interrupted
				try
				{
					using var cts = CreateScheduleInterruptCts(stoppingToken);
					await Task.Delay(Timeout.InfiniteTimeSpan, cts.Token);
				}
				catch (OperationCanceledException) { }
			}
			else
			{
				var delay = nextTransition.Value - DateTimeOffset.UtcNow;
				if (delay < TimeSpan.Zero)
					delay = TimeSpan.Zero;

				LogNextScheduleTransition(nextTransition.Value, delay);

				try
				{
					using var cts = CreateScheduleInterruptCts(stoppingToken);
					await Task.Delay(delay, cts.Token);
				}
				catch (OperationCanceledException) { }
			}
		}

		LogScheduleEvaluationStopped();
	}

	/// <summary>
	/// Evaluates all profile schedules and activates/deactivates profiles as needed.
	/// Profiles that were manually activated are skipped -- the schedule will not
	/// override a manual activation.
	/// </summary>
	private void EvaluateSchedules()
	{
		var now = DateTimeOffset.UtcNow;

		foreach (var profile in _store.GetAll())
		{
			if (profile.Schedule is null)
				continue;

			var shouldBeActive = profile.Schedule.IsActiveAt(now);

			if (shouldBeActive && !profile.IsActive)
			{
				LogScheduleActivating(profile.Id, profile.Name);
				ActivateProfile(profile.Id, "schedule");
			}
			else if (!shouldBeActive && profile.IsActive && profile.ActivationTrigger != "manual")
			{
				// Only deactivate if the profile was NOT manually activated.
				// Manual activations persist until the user explicitly deactivates.
				LogScheduleDeactivating(profile.Id, profile.Name);
				DeactivateProfile(profile.Id, "schedule");
			}
		}
	}

	/// <summary>
	/// Computes the earliest next schedule transition across all profiles.
	/// </summary>
	private DateTimeOffset? ComputeNextScheduleTransition()
	{
		DateTimeOffset? earliest = null;
		var now = DateTimeOffset.UtcNow;

		foreach (var profile in _store.GetAll())
		{
			if (profile.Schedule is null)
				continue;

			var next = profile.Schedule.GetNextTransitionTime(now);
			if (next is not null && (earliest is null || next < earliest))
				earliest = next;
		}

		return earliest;
	}

	/// <summary>
	/// Creates a CancellationTokenSource that is cancelled when either the stopping token
	/// fires or the schedule is interrupted due to profile changes.
	/// </summary>
	private CancellationTokenSource CreateScheduleInterruptCts(CancellationToken stoppingToken)
	{
		var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);

		lock (_scheduleInterruptLock)
		{
			_scheduleInterruptCts?.Dispose();
			_scheduleInterruptCts = cts;
		}

		return cts;
	}

	/// <summary>
	/// Interrupts the current schedule delay, causing immediate re-evaluation.
	/// </summary>
	private void InterruptScheduleDelay()
	{
		lock (_scheduleInterruptLock)
		{
			try
			{
				_scheduleInterruptCts?.Cancel();
			}
			catch (ObjectDisposedException) { }
		}
	}

	// ── Initialization ──

	/// <summary>
	/// Initializes the profile manager: loads profiles, ensures default, computes initial active set.
	/// </summary>
	public void Initialize()
	{
		_store.LoadAll();

		if (_store.Count == 0 && _registry.GetAll().Any())
		{
			EnsureDefaultProfile();
		}

		// Compute initial active set without emitting events
		lock (_activeSetLock)
		{
			_currentActiveSet = ComputeEffectiveActiveSet();
		}

		LogInitialized(_currentActiveSet.Count, _store.Count);
	}

	// ── Structured Logging ──

	[LoggerMessage(Level = LogLevel.Information, Message = "Profile '{ProfileId}' created with name '{Name}'")]
	private partial void LogProfileCreated(string profileId, string name);

	[LoggerMessage(Level = LogLevel.Information, Message = "Profile '{ProfileId}' updated")]
	private partial void LogProfileUpdated(string profileId);

	[LoggerMessage(Level = LogLevel.Information, Message = "Profile '{ProfileId}' deleted")]
	private partial void LogProfileDeleted(string profileId);

	[LoggerMessage(Level = LogLevel.Information, Message = "Profile '{ProfileId}' activated (trigger: {Trigger})")]
	private partial void LogProfileActivated(string profileId, string trigger);

	[LoggerMessage(Level = LogLevel.Information, Message = "Profile '{ProfileId}' deactivated (trigger: {Trigger})")]
	private partial void LogProfileDeactivated(string profileId, string trigger);

	[LoggerMessage(Level = LogLevel.Information, Message = "Effective active set changed: {ActivatedCount} activated, {DeactivatedCount} deactivated (trigger: {Trigger})")]
	private partial void LogEffectiveActiveSetChanged(int activatedCount, int deactivatedCount, string trigger);

	[LoggerMessage(Level = LogLevel.Warning, Message = "Profile event handler failed")]
	private partial void LogEventHandlerFailed(Exception ex);

	[LoggerMessage(Level = LogLevel.Information, Message = "Default profile auto-created with ID '{ProfileId}'")]
	private partial void LogDefaultProfileCreated(string profileId);

	[LoggerMessage(Level = LogLevel.Information, Message = "ProfileManager initialized: {ActiveCount} active orchestrations across {ProfileCount} profile(s)")]
	private partial void LogInitialized(int activeCount, int profileCount);

	[LoggerMessage(Level = LogLevel.Debug, Message = "Profile schedule evaluation started")]
	private partial void LogScheduleEvaluationStarted();

	[LoggerMessage(Level = LogLevel.Debug, Message = "Profile schedule evaluation stopped")]
	private partial void LogScheduleEvaluationStopped();

	[LoggerMessage(Level = LogLevel.Warning, Message = "Profile schedule evaluation error")]
	private partial void LogScheduleEvaluationError(Exception ex);

	[LoggerMessage(Level = LogLevel.Debug, Message = "Next schedule transition at {TransitionTime} (in {Delay})")]
	private partial void LogNextScheduleTransition(DateTimeOffset transitionTime, TimeSpan delay);

	[LoggerMessage(Level = LogLevel.Information, Message = "Schedule activating profile '{ProfileId}' ({ProfileName})")]
	private partial void LogScheduleActivating(string profileId, string profileName);

	[LoggerMessage(Level = LogLevel.Information, Message = "Schedule deactivating profile '{ProfileId}' ({ProfileName})")]
	private partial void LogScheduleDeactivating(string profileId, string profileName);

	[LoggerMessage(Level = LogLevel.Information, Message = "Profile '{ProfileId}' ({ProfileName}) imported successfully")]
	private partial void LogProfileImported(string profileId, string profileName);

	[LoggerMessage(Level = LogLevel.Information, Message = "Profile '{ProfileId}' ({ProfileName}) import skipped: already exists")]
	private partial void LogProfileImportSkipped(string profileId, string profileName);

	[LoggerMessage(Level = LogLevel.Information, Message = "Profile '{ProfileId}' exported to {FilePath}")]
	private partial void LogProfileExported(string profileId, string filePath);

	[LoggerMessage(Level = LogLevel.Information, Message = "Profile '{ProfileId}' export skipped: file already exists at {FilePath}")]
	private partial void LogProfileExportSkipped(string profileId, string filePath);

	[LoggerMessage(Level = LogLevel.Warning, Message = "Failed to export profile '{ProfileId}'")]
	private partial void LogProfileExportFailed(Exception ex, string profileId);
}
