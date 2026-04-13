using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orchestra.Engine;
using Orchestra.Host.Profiles;
using Orchestra.Host.Registry;
using Orchestra.Host.Triggers;

namespace Orchestra.Host.Hosting;

/// <summary>
/// Background service that watches a configured directory for orchestration file changes
/// and automatically registers, updates, or removes orchestrations at runtime.
/// Only active when <see cref="OrchestrationsScanConfig.Watch"/> is enabled.
/// </summary>
public partial class OrchestrationSyncService : BackgroundService
{
	private readonly OrchestrationRegistry _registry;
	private readonly TriggerManager _triggerManager;
	private readonly ProfileManager _profileManager;
	private readonly OrchestrationHostOptions _options;
	private readonly ILogger<OrchestrationSyncService> _logger;

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
		OrchestrationHostOptions options,
		ILogger<OrchestrationSyncService> logger)
	{
		_registry = registry;
		_triggerManager = triggerManager;
		_profileManager = profileManager;
		_options = options;
		_logger = logger;
	}

	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		var scanConfig = _options.OrchestrationsScan;
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

		using var watcher = CreateWatcher(scanConfig);

		watcher.Created += (_, e) => OnFileEvent(e.FullPath, WatcherChangeTypes.Created, stoppingToken);
		watcher.Changed += (_, e) => OnFileEvent(e.FullPath, WatcherChangeTypes.Changed, stoppingToken);
		watcher.Deleted += (_, e) => OnFileEvent(e.FullPath, WatcherChangeTypes.Deleted, stoppingToken);
		watcher.Renamed += (_, e) =>
		{
			OnFileEvent(e.OldFullPath, WatcherChangeTypes.Deleted, stoppingToken);
			OnFileEvent(e.FullPath, WatcherChangeTypes.Created, stoppingToken);
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

	private static FileSystemWatcher CreateWatcher(OrchestrationsScanConfig config)
	{
		return new FileSystemWatcher(config.Directory)
		{
			IncludeSubdirectories = config.Recursive,
			NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime | NotifyFilters.Size,
			Filter = ""
		};
	}

	private void OnFileEvent(string fullPath, WatcherChangeTypes changeType, CancellationToken stoppingToken)
	{
		if (!IsOrchestrationFile(fullPath))
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
				switch (changeType)
				{
					case WatcherChangeTypes.Created:
					case WatcherChangeTypes.Changed:
						await HandleFileCreatedOrChangedAsync(fullPath);
						break;
					case WatcherChangeTypes.Deleted:
						HandleFileDeleted(fullPath);
						break;
				}
			}
			catch (Exception ex)
			{
				LogFileEventHandlingFailed(fullPath, changeType.ToString(), ex);
			}
		}, stoppingToken);
	}

	private async Task HandleFileCreatedOrChangedAsync(string fullPath)
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

	private void HandleFileDeleted(string fullPath)
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

	private void OnWatcherError(Exception error, OrchestrationsScanConfig config)
	{
		LogWatcherError(config.Directory, error);

		// Re-scan the entire directory to recover from buffer overflow or other errors
		try
		{
			_registry.SyncDirectory(config.Directory, config.Recursive);
			LogWatcherRecoveryCompleted(config.Directory);
		}
		catch (Exception ex)
		{
			LogWatcherRecoveryFailed(config.Directory, ex);
		}
	}

	private static bool IsOrchestrationFile(string path)
	{
		var ext = Path.GetExtension(path);
		return ext.Equals(".json", StringComparison.OrdinalIgnoreCase)
			|| ext.Equals(".yaml", StringComparison.OrdinalIgnoreCase)
			|| ext.Equals(".yml", StringComparison.OrdinalIgnoreCase);
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
}
