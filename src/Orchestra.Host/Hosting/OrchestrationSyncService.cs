using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orchestra.Engine;
using Orchestra.Host.Profiles;
using Orchestra.Host.Registry;
using Orchestra.Host.Triggers;

namespace Orchestra.Host.Hosting;

/// <summary>
/// Background service that watches a configured scan directory for orchestration and profile
/// file changes and automatically registers, updates, or removes them at runtime.
/// The scan directory is expected to contain <c>orchestrations/</c> and/or <c>profiles/</c> subdirectories.
/// Only active when <see cref="ScanConfig.Watch"/> is enabled.
/// </summary>
public partial class OrchestrationSyncService : BackgroundService
{
	private readonly OrchestrationRegistry _registry;
	private readonly TriggerManager _triggerManager;
	private readonly ProfileManager _profileManager;
	private readonly ProfileStore _profileStore;
	private readonly OrchestrationHostOptions _options;
	private readonly ILogger<OrchestrationSyncService> _logger;

	/// <summary>
	/// Subdirectory name for orchestration files within the scan root.
	/// </summary>
	internal const string OrchestrationsDirName = "orchestrations";

	/// <summary>
	/// Subdirectory name for profile files within the scan root.
	/// </summary>
	internal const string ProfilesDirName = "profiles";

	/// <summary>
	/// Debounce delay before processing a file change event.
	/// </summary>
	internal TimeSpan DebounceDelay { get; set; } = TimeSpan.FromMilliseconds(500);

	/// <summary>
	/// Maximum number of retries when a file is locked (e.g., by an editor saving).
	/// </summary>
	internal int MaxRetries { get; set; } = 3;

	/// <summary>
	/// Delay between retries when a file is locked.
	/// </summary>
	internal TimeSpan RetryDelay { get; set; } = TimeSpan.FromMilliseconds(200);

	private readonly ConcurrentDictionary<string, CancellationTokenSource> _debounceTimers = new();

	/// <summary>
	/// Completes when the file system watcher has been initialized and is raising events.
	/// Used by tests to avoid race conditions between watcher setup and file operations.
	/// </summary>
	internal TaskCompletionSource WatcherReady { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

	public OrchestrationSyncService(
		OrchestrationRegistry registry,
		TriggerManager triggerManager,
		ProfileManager profileManager,
		ProfileStore profileStore,
		OrchestrationHostOptions options,
		ILogger<OrchestrationSyncService> logger)
	{
		_registry = registry;
		_triggerManager = triggerManager;
		_profileManager = profileManager;
		_profileStore = profileStore;
		_options = options;
		_logger = logger;
	}

	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		var scanConfig = _options.Scan;
		if (scanConfig is null || !scanConfig.Watch)
		{
			WatcherReady.TrySetResult();
			return;
		}

		if (!Directory.Exists(scanConfig.Directory))
		{
			LogWatchDirectoryNotFound(scanConfig.Directory);
			WatcherReady.TrySetResult();
			return;
		}

		LogWatchStarted(scanConfig.Directory, scanConfig.Recursive);

		// Watch the root directory with IncludeSubdirectories = true so we can
		// see events in both orchestrations/ and profiles/ subdirectories.
		using var watcher = CreateWatcher(scanConfig);

		watcher.Created += (_, e) => OnFileEvent(e.FullPath, WatcherChangeTypes.Created, scanConfig, stoppingToken);
		watcher.Changed += (_, e) => OnFileEvent(e.FullPath, WatcherChangeTypes.Changed, scanConfig, stoppingToken);
		watcher.Deleted += (_, e) => OnFileEvent(e.FullPath, WatcherChangeTypes.Deleted, scanConfig, stoppingToken);
		watcher.Renamed += (_, e) =>
		{
			OnFileEvent(e.OldFullPath, WatcherChangeTypes.Deleted, scanConfig, stoppingToken);
			OnFileEvent(e.FullPath, WatcherChangeTypes.Created, scanConfig, stoppingToken);
		};
		watcher.Error += (_, e) => OnWatcherError(e.GetException(), scanConfig);

		watcher.EnableRaisingEvents = true;
		WatcherReady.TrySetResult();

		// Block until cancellation is requested
		try
		{
			await Task.Delay(Timeout.Infinite, stoppingToken);
		}
		catch (OperationCanceledException)
		{
			// Expected on shutdown
		}

		// Cancel all pending debounce timers
		foreach (var cts in _debounceTimers.Values)
		{
			cts.Cancel();
			cts.Dispose();
		}
		_debounceTimers.Clear();

		LogWatchStopped(scanConfig.Directory);
	}

	private static FileSystemWatcher CreateWatcher(ScanConfig config)
	{
		return new FileSystemWatcher(config.Directory)
		{
			// Always watch subdirectories so we can see orchestrations/ and profiles/
			IncludeSubdirectories = true,
			NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime | NotifyFilters.Size,
			Filter = ""
		};
	}

	// ── File type classification ──

	private enum FileCategory { Orchestration, Profile, Ignored }

	/// <summary>
	/// Determines whether a file event is for an orchestration, profile, or should be ignored
	/// based on which subdirectory the file resides in and its extension.
	/// </summary>
	private static FileCategory ClassifyFile(string fullPath, ScanConfig scanConfig)
	{
		if (!IsSupportedExtension(fullPath))
			return FileCategory.Ignored;

		var rootDir = Path.GetFullPath(scanConfig.Directory);
		var normalizedPath = Path.GetFullPath(fullPath);

		// Check if file is under orchestrations/ subdirectory
		var orchestrationsDir = Path.Combine(rootDir, OrchestrationsDirName);
		if (normalizedPath.StartsWith(orchestrationsDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
			|| normalizedPath.StartsWith(orchestrationsDir + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
		{
			return FileCategory.Orchestration;
		}

		// Check if file is under profiles/ subdirectory
		var profilesDir = Path.Combine(rootDir, ProfilesDirName);
		if (normalizedPath.StartsWith(profilesDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
			|| normalizedPath.StartsWith(profilesDir + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
		{
			// Profiles are JSON only
			if (Path.GetExtension(fullPath).Equals(".json", StringComparison.OrdinalIgnoreCase))
				return FileCategory.Profile;
		}

		return FileCategory.Ignored;
	}

	private static bool IsSupportedExtension(string path)
	{
		var ext = Path.GetExtension(path);
		return ext.Equals(".json", StringComparison.OrdinalIgnoreCase)
			|| ext.Equals(".yaml", StringComparison.OrdinalIgnoreCase)
			|| ext.Equals(".yml", StringComparison.OrdinalIgnoreCase);
	}

	// ── Event routing ──

	private void OnFileEvent(string fullPath, WatcherChangeTypes changeType, ScanConfig scanConfig, CancellationToken stoppingToken)
	{
		var category = ClassifyFile(fullPath, scanConfig);
		if (category == FileCategory.Ignored)
			return;

		// Cancel any previous debounce timer for this file
		if (_debounceTimers.TryRemove(fullPath, out var existingCts))
		{
			existingCts.Cancel();
			existingCts.Dispose();
		}

		var debounceCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
		_debounceTimers[fullPath] = debounceCts;

		_ = Task.Run(async () =>
		{
			try
			{
				await Task.Delay(DebounceDelay, debounceCts.Token);
			}
			catch (OperationCanceledException)
			{
				return; // Debounce was superseded by a newer event or shutdown
			}
			finally
			{
				_debounceTimers.TryRemove(fullPath, out _);
			}

			try
			{
				switch (category)
				{
					case FileCategory.Orchestration:
						await HandleOrchestrationEventAsync(fullPath, changeType);
						break;
					case FileCategory.Profile:
						await HandleProfileEventAsync(fullPath, changeType);
						break;
				}
			}
			catch (Exception ex)
			{
				LogFileEventHandlingFailed(fullPath, changeType.ToString(), ex);
			}
		}, stoppingToken);
	}

	// ── Orchestration handlers ──

	private async Task HandleOrchestrationEventAsync(string fullPath, WatcherChangeTypes changeType)
	{
		switch (changeType)
		{
			case WatcherChangeTypes.Created:
			case WatcherChangeTypes.Changed:
				await HandleOrchestrationFileCreatedOrChangedAsync(fullPath);
				break;
			case WatcherChangeTypes.Deleted:
				HandleOrchestrationFileDeleted(fullPath);
				break;
		}
	}

	private async Task HandleOrchestrationFileCreatedOrChangedAsync(string fullPath)
	{
		// File may still be locked by the editor — retry with backoff
		string? rawContent = null;
		for (var attempt = 0; attempt < MaxRetries; attempt++)
		{
			try
			{
				if (!File.Exists(fullPath))
					return; // File was deleted between event and processing

				rawContent = File.ReadAllText(fullPath);
				break;
			}
			catch (IOException) when (attempt < MaxRetries - 1)
			{
				await Task.Delay(RetryDelay);
			}
		}

		if (rawContent is null)
		{
			LogFileReadFailed(fullPath);
			return;
		}

		try
		{
			var entry = _registry.Register(fullPath);
			LogOrchestrationSynced(entry.Orchestration.Name, fullPath);

			// Re-register the trigger if the orchestration defines one
			ReRegisterTrigger(entry);
		}
		catch (Exception ex)
		{
			LogOrchestrationSyncFailed(fullPath, ex);
		}
	}

	private void HandleOrchestrationFileDeleted(string fullPath)
	{
		// Find the registry entry by matching the source path or path
		var entry = FindEntryBySourcePath(fullPath);
		if (entry is null)
			return;

		var id = entry.Id;
		var name = entry.Orchestration.Name;

		_triggerManager.RemoveTrigger(id);
		_registry.Remove(id);

		LogOrchestrationRemoved(name, fullPath);
	}

	private OrchestrationEntry? FindEntryBySourcePath(string fullPath)
	{
		var normalized = Path.GetFullPath(fullPath);
		foreach (var entry in _registry.GetAll())
		{
			var entrySource = Path.GetFullPath(entry.SourcePath ?? entry.Path);
			if (string.Equals(entrySource, normalized, StringComparison.OrdinalIgnoreCase))
				return entry;
		}
		return null;
	}

	private void ReRegisterTrigger(OrchestrationEntry entry)
	{
		var trigger = entry.Orchestration.Trigger;
		var id = entry.Id;

		// Remove existing trigger first (if any) to ensure clean re-registration
		_triggerManager.RemoveTrigger(id);

		// Check for persisted enabled-state override
		var enabledOverride = _triggerManager.GetJsonTriggerEnabledOverride(id);
		var effectiveEnabled = enabledOverride ?? trigger.Enabled;

		if (!effectiveEnabled && !trigger.Enabled)
			return; // Disabled in JSON and no override — skip

		var effectiveTrigger = effectiveEnabled != trigger.Enabled
			? TriggerManager.CloneTriggerConfigWithEnabled(trigger, effectiveEnabled)
			: trigger;

		_triggerManager.RegisterTrigger(
			entry.Path,
			effectiveTrigger,
			null,
			TriggerSource.Json,
			entry.Id,
			entry.Orchestration);

		// Apply profile active set — disable if not in active set
		var activeIds = _profileManager.GetEffectiveActiveOrchestrationIds();
		if (!activeIds.Contains(id) && effectiveTrigger.Enabled)
		{
			_triggerManager.SetTriggerEnabled(id, false);
		}
	}

	// ── Profile handlers ──

	private async Task HandleProfileEventAsync(string fullPath, WatcherChangeTypes changeType)
	{
		switch (changeType)
		{
			case WatcherChangeTypes.Created:
			case WatcherChangeTypes.Changed:
				await HandleProfileFileCreatedOrChangedAsync(fullPath);
				break;
			case WatcherChangeTypes.Deleted:
				HandleProfileFileDeleted(fullPath);
				break;
		}
	}

	private async Task HandleProfileFileCreatedOrChangedAsync(string fullPath)
	{
		// File may still be locked by the editor — retry with backoff
		string? rawContent = null;
		for (var attempt = 0; attempt < MaxRetries; attempt++)
		{
			try
			{
				if (!File.Exists(fullPath))
					return;

				rawContent = File.ReadAllText(fullPath);
				break;
			}
			catch (IOException) when (attempt < MaxRetries - 1)
			{
				await Task.Delay(RetryDelay);
			}
		}

		if (rawContent is null)
		{
			LogFileReadFailed(fullPath);
			return;
		}

		try
		{
			var contentHash = ProfileStore.ComputeContentHash(rawContent);
			var profile = JsonSerializer.Deserialize<Profile>(rawContent, ProfileStore.JsonOptions);
			if (profile is null)
			{
				LogProfileSyncFailed(fullPath);
				return;
			}

			var id = ProfileStore.GenerateId(profile.Name);
			var existing = _profileStore.Get(id);

			// Skip if content hasn't changed
			if (existing?.ContentHash == contentHash)
				return;

			if (existing is not null)
			{
				// Update: preserve activation state
				profile = profile with
				{
					Id = id,
					IsActive = existing.IsActive,
					ActivatedAt = existing.ActivatedAt,
					DeactivatedAt = existing.DeactivatedAt,
					ActivationTrigger = existing.ActivationTrigger,
					SourcePath = Path.GetFullPath(fullPath),
					ContentHash = contentHash,
					UpdatedAt = DateTimeOffset.UtcNow,
				};
			}
			else
			{
				// New: import as inactive
				profile = profile with
				{
					Id = id,
					IsActive = false,
					ActivatedAt = null,
					DeactivatedAt = null,
					ActivationTrigger = null,
					SourcePath = Path.GetFullPath(fullPath),
					ContentHash = contentHash,
					CreatedAt = DateTimeOffset.UtcNow,
					UpdatedAt = DateTimeOffset.UtcNow,
				};
			}

			_profileStore.Save(profile);
			LogProfileSynced(profile.Name, fullPath);

			// Recompute effective active set in case filter changed
			_profileManager.RefreshEffectiveActiveSet();
		}
		catch (Exception ex)
		{
			LogProfileSyncError(fullPath, ex);
		}
	}

	private void HandleProfileFileDeleted(string fullPath)
	{
		var normalized = Path.GetFullPath(fullPath);
		foreach (var profile in _profileStore.GetAll())
		{
			if (profile.SourcePath is null)
				continue;

			if (string.Equals(Path.GetFullPath(profile.SourcePath), normalized, StringComparison.OrdinalIgnoreCase))
			{
				LogProfileRemoved(profile.Name, fullPath);

				// Deactivate if active before removing
				if (profile.IsActive)
					_profileManager.DeactivateProfile(profile.Id, "sync-delete");

				_profileStore.Remove(profile.Id);
				_profileManager.RefreshEffectiveActiveSet();
				return;
			}
		}
	}

	// ── Error recovery ──

	private void OnWatcherError(Exception error, ScanConfig config)
	{
		LogWatcherError(config.Directory, error);

		// Re-scan the entire directory to recover from buffer overflow or other errors
		try
		{
			var orchestrationsDir = Path.Combine(config.Directory, OrchestrationsDirName);
			if (Directory.Exists(orchestrationsDir))
				_registry.SyncDirectory(orchestrationsDir, config.Recursive);

			var profilesDir = Path.Combine(config.Directory, ProfilesDirName);
			if (Directory.Exists(profilesDir))
				_profileStore.SyncDirectory(profilesDir);

			LogWatcherRecoveryCompleted(config.Directory);
		}
		catch (Exception ex)
		{
			LogWatcherRecoveryFailed(config.Directory, ex);
		}
	}

	// ── Structured Logging ──

	[LoggerMessage(Level = LogLevel.Information, Message = "File watcher started for directory '{Directory}' (recursive: {Recursive})")]
	private partial void LogWatchStarted(string directory, bool recursive);

	[LoggerMessage(Level = LogLevel.Information, Message = "File watcher stopped for directory '{Directory}'")]
	private partial void LogWatchStopped(string directory);

	[LoggerMessage(Level = LogLevel.Warning, Message = "Watch directory not found: '{Directory}'")]
	private partial void LogWatchDirectoryNotFound(string directory);

	[LoggerMessage(Level = LogLevel.Information, Message = "Orchestration '{Name}' synced from '{Path}'")]
	private partial void LogOrchestrationSynced(string name, string path);

	[LoggerMessage(Level = LogLevel.Information, Message = "Orchestration '{Name}' removed (file deleted: '{Path}')")]
	private partial void LogOrchestrationRemoved(string name, string path);

	[LoggerMessage(Level = LogLevel.Warning, Message = "Failed to sync orchestration from '{Path}'")]
	private partial void LogOrchestrationSyncFailed(string path, Exception ex);

	[LoggerMessage(Level = LogLevel.Warning, Message = "Failed to read file '{Path}' after retries")]
	private partial void LogFileReadFailed(string path);

	[LoggerMessage(Level = LogLevel.Warning, Message = "Failed to handle file event for '{Path}' (event: {EventType})")]
	private partial void LogFileEventHandlingFailed(string path, string eventType, Exception ex);

	[LoggerMessage(Level = LogLevel.Error, Message = "File watcher error for directory '{Directory}'")]
	private partial void LogWatcherError(string directory, Exception ex);

	[LoggerMessage(Level = LogLevel.Information, Message = "File watcher recovery completed for directory '{Directory}'")]
	private partial void LogWatcherRecoveryCompleted(string directory);

	[LoggerMessage(Level = LogLevel.Error, Message = "File watcher recovery failed for directory '{Directory}'")]
	private partial void LogWatcherRecoveryFailed(string directory, Exception ex);

	[LoggerMessage(Level = LogLevel.Information, Message = "Profile '{Name}' synced from '{Path}'")]
	private partial void LogProfileSynced(string name, string path);

	[LoggerMessage(Level = LogLevel.Information, Message = "Profile '{Name}' removed (file deleted: '{Path}')")]
	private partial void LogProfileRemoved(string name, string path);

	[LoggerMessage(Level = LogLevel.Warning, Message = "Failed to sync profile from '{Path}'")]
	private partial void LogProfileSyncFailed(string path);

	[LoggerMessage(Level = LogLevel.Warning, Message = "Failed to sync profile from '{Path}'")]
	private partial void LogProfileSyncError(string path, Exception ex);
}
