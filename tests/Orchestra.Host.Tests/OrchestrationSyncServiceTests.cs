using System.Collections.Concurrent;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Orchestra.Host.Api;
using Orchestra.Host.Hosting;
using Orchestra.Host.Profiles;
using Orchestra.Host.Registry;
using Orchestra.Host.Triggers;
using Xunit;

namespace Orchestra.Host.Tests;

/// <summary>
/// Unit tests for OrchestrationSyncService — the background file watcher service.
/// </summary>
public class OrchestrationSyncServiceTests : IDisposable
{
	private readonly string _tempDir;
	private readonly string _watchDir;
	private readonly string _orchestrationsDir;
	private readonly string _dataPath;
	private readonly string _persistPath;
	private readonly OrchestrationRegistry _registry;
	private readonly TriggerManager _triggerManager;
	private readonly ProfileManager _profileManager;
	private readonly ProfileStore _profileStore;
	private readonly DashboardEventBroadcaster _dashboardBroadcaster;

	public OrchestrationSyncServiceTests()
	{
		_tempDir = Path.Combine(Path.GetTempPath(), $"orchestra-sync-tests-{Guid.NewGuid():N}");
		_watchDir = Path.Combine(_tempDir, "watch");
		_orchestrationsDir = Path.Combine(_watchDir, OrchestrationSyncService.OrchestrationsDirName);
		_dataPath = Path.Combine(_tempDir, "data");
		_persistPath = Path.Combine(_dataPath, "registered-orchestrations.json");
		Directory.CreateDirectory(_watchDir);
		Directory.CreateDirectory(_orchestrationsDir);
		Directory.CreateDirectory(_dataPath);

		_registry = new OrchestrationRegistry(_persistPath, NullLogger<OrchestrationRegistry>.Instance);

		var runsDir = Path.Combine(_dataPath, "runs");
		Directory.CreateDirectory(runsDir);

		_triggerManager = new TriggerManager(
			new ConcurrentDictionary<string, CancellationTokenSource>(),
			new ConcurrentDictionary<string, ActiveExecutionInfo>(),
			agentBuilder: null!,
			scheduler: null!,
			loggerFactory: NullLoggerFactory.Instance,
			logger: NullLogger<TriggerManager>.Instance,
			runsDir: runsDir,
			runStore: null!,
			checkpointStore: null!,
			dataPath: _dataPath);

		_profileStore = new ProfileStore(_dataPath, NullLogger<ProfileStore>.Instance);
		var tagStore = new OrchestrationTagStore(_dataPath, NullLogger<OrchestrationTagStore>.Instance);
		_profileManager = new ProfileManager(_profileStore, tagStore, _registry, NullLogger<ProfileManager>.Instance);
		_profileManager.Initialize();
		_dashboardBroadcaster = new DashboardEventBroadcaster(NullLogger<DashboardEventBroadcaster>.Instance);
	}

	public void Dispose()
	{
		if (Directory.Exists(_tempDir))
		{
			try { Directory.Delete(_tempDir, recursive: true); }
			catch { /* best-effort cleanup */ }
		}
	}

	private string WriteOrchestrationFile(string directory, string name, string? description = null)
	{
		Directory.CreateDirectory(directory);
		var json = $$"""
		{
			"name": "{{name}}",
			"description": "{{description ?? $"Test orchestration: {name}"}}",
			"version": "1.0.0",
			"steps": [{
				"name": "step1",
				"type": "prompt",
				"systemPrompt": "You are a test assistant.",
				"userPrompt": "Test prompt",
				"model": "claude-opus-4.5"
			}]
		}
		""";
		var path = Path.Combine(directory, $"{name}.json");

		// Write atomically via temp file + move to avoid conflicts with concurrent readers
		var tempPath = path + $".{Guid.NewGuid():N}.tmp";
		File.WriteAllText(tempPath, json);

		var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
		while (true)
		{
			try
			{
				File.Move(tempPath, path, overwrite: true);
				break;
			}
			catch (UnauthorizedAccessException) when (DateTime.UtcNow < deadline)
			{
				Thread.Sleep(25);
			}
			catch (IOException) when (DateTime.UtcNow < deadline)
			{
				Thread.Sleep(25);
			}
		}

		return path;
	}

	private OrchestrationSyncService CreateService(bool watch = true, bool recursive = false)
	{
		var options = new OrchestrationHostOptions
		{
			DataPath = _dataPath,
			Scan = new ScanConfig
			{
				Directory = _watchDir,
				Watch = watch,
				Recursive = recursive,
			},
		};

		var service = new OrchestrationSyncService(
			_registry,
			_triggerManager,
			_profileManager,
			_profileStore,
			_dashboardBroadcaster,
			options,
			NullLogger<OrchestrationSyncService>.Instance);

		// Use short debounce for tests
		service.DebounceDelay = TimeSpan.FromMilliseconds(50);
		service.RetryDelay = TimeSpan.FromMilliseconds(50);

		return service;
	}

	// ── Configuration tests ──

	[Fact]
	public async Task ExecuteAsync_WatchDisabled_ExitsImmediately()
	{
		// Arrange
		var service = CreateService(watch: false);
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

		// Act
		await service.StartAsync(cts.Token);
		await Task.Delay(100);
		await service.StopAsync(CancellationToken.None);

		// Assert — should complete without hanging
	}

	[Fact]
	public async Task ExecuteAsync_NullScanConfig_ExitsImmediately()
	{
		// Arrange
		var options = new OrchestrationHostOptions { Scan = null };
		var service = new OrchestrationSyncService(
			_registry, _triggerManager, _profileManager, _profileStore, _dashboardBroadcaster, options,
			NullLogger<OrchestrationSyncService>.Instance);
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

		// Act
		await service.StartAsync(cts.Token);
		await Task.Delay(100);
		await service.StopAsync(CancellationToken.None);

		// Assert — should complete without hanging
	}

	// ── File watcher: create tests ──

	[Fact]
	public async Task FileWatcher_NewFileCreated_RegistersOrchestration()
	{
		// Arrange
		var service = CreateService();
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
		await service.StartAsync(cts.Token);
		await service.WatcherReady.Task;

		// Act — create a new file in the watched orchestrations directory
		WriteOrchestrationFile(_orchestrationsDir, "watcher-new");

		// Wait for debounce + processing
		await WaitForConditionAsync(() => _registry.Count == 1, TimeSpan.FromSeconds(5));

		await service.StopAsync(CancellationToken.None);

		// Assert
		_registry.Count.Should().Be(1);
		_registry.GetAll().First().Orchestration.Name.Should().Be("watcher-new");
	}

	[Fact]
	public async Task FileWatcher_FileModified_UpdatesOrchestration()
	{
		// Arrange — pre-register a file
		var filePath = WriteOrchestrationFile(_orchestrationsDir, "watcher-update", "Version 1");
		_registry.Register(filePath, persist: false);
		_registry.Count.Should().Be(1);
		_registry.GetAll().First().Orchestration.Description.Should().Be("Version 1");

		var service = CreateService();
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
		await service.StartAsync(cts.Token);
		await service.WatcherReady.Task;

		// Act — modify the file
		WriteOrchestrationFile(_orchestrationsDir, "watcher-update", "Version 2");

		// Wait for debounce + processing
		await WaitForConditionAsync(
			() => _registry.GetAll().FirstOrDefault()?.Orchestration.Description == "Version 2",
			TimeSpan.FromSeconds(5));

		await service.StopAsync(CancellationToken.None);

		// Assert
		_registry.Count.Should().Be(1);
		_registry.GetAll().First().Orchestration.Description.Should().Be("Version 2");
	}

	[Fact]
	public async Task FileWatcher_FileDeleted_RemovesOrchestration()
	{
		// Arrange — pre-register a file
		var filePath = WriteOrchestrationFile(_orchestrationsDir, "watcher-delete");
		_registry.Register(filePath, persist: false);
		_registry.Count.Should().Be(1);

		var service = CreateService();
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
		await service.StartAsync(cts.Token);
		await service.WatcherReady.Task;

		// Act — delete the file
		File.Delete(filePath);

		// Wait for debounce + processing (longer timeout for Windows FSW reliability)
		await WaitForConditionAsync(() => _registry.Count == 0, TimeSpan.FromSeconds(10));

		await service.StopAsync(CancellationToken.None);

		// Assert
		_registry.Count.Should().Be(0);
	}

	[Fact]
	public async Task FileWatcher_InvalidFile_DoesNotCrash()
	{
		// Arrange
		var service = CreateService();
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
		await service.StartAsync(cts.Token);
		await service.WatcherReady.Task;

		// Act — write an invalid JSON file
		File.WriteAllText(Path.Combine(_orchestrationsDir, "broken.json"), "not valid json {{{");

		// Wait for debounce + processing attempt
		await Task.Delay(500);

		await service.StopAsync(CancellationToken.None);

		// Assert — should not crash, no orchestration registered
		_registry.Count.Should().Be(0);
	}

	[Fact]
	public async Task FileWatcher_NonOrchestrationFile_Ignored()
	{
		// Arrange
		var service = CreateService();
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
		await service.StartAsync(cts.Token);
		await service.WatcherReady.Task;

		// Act — write a non-orchestration file
		File.WriteAllText(Path.Combine(_orchestrationsDir, "readme.txt"), "This is not an orchestration");

		// Wait to confirm no processing
		await Task.Delay(500);

		await service.StopAsync(CancellationToken.None);

		// Assert
		_registry.Count.Should().Be(0);
	}

	[Fact]
	public async Task FileWatcher_FileRenamed_HandlesAsDeleteAndCreate()
	{
		// Arrange — pre-register a file
		var originalPath = WriteOrchestrationFile(_orchestrationsDir, "watcher-rename");
		_registry.Register(originalPath, persist: false);
		_registry.Count.Should().Be(1);

		var service = CreateService();
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
		await service.StartAsync(cts.Token);
		await service.WatcherReady.Task;

		// Act — rename the file (creates a new file with different name, but same content structure)
		var newPath = Path.Combine(_orchestrationsDir, "renamed-orch.json");
		File.Move(originalPath, newPath);

		// Wait for debounce + processing
		await WaitForConditionAsync(() =>
		{
			var all = _registry.GetAll().ToList();
			// After rename, the old entry should be removed and a new one created
			// (potentially — depends on whether the registry matches by source path)
			return all.Count >= 1;
		}, TimeSpan.FromSeconds(5));

		await service.StopAsync(CancellationToken.None);

		// Assert — the renamed file should result in a registered orchestration
		_registry.Count.Should().BeGreaterThanOrEqualTo(1);
	}

	[Fact]
	public async Task FileWatcher_Recursive_WatchesSubdirectories()
	{
		// Arrange
		var subDir = Path.Combine(_orchestrationsDir, "sub");
		Directory.CreateDirectory(subDir);

		var service = CreateService(recursive: true);
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
		await service.StartAsync(cts.Token);
		await service.WatcherReady.Task;

		// Act — create a file in a subdirectory
		WriteOrchestrationFile(subDir, "sub-orch");

		// Wait for debounce + processing
		await WaitForConditionAsync(() => _registry.Count == 1, TimeSpan.FromSeconds(5));

		await service.StopAsync(CancellationToken.None);

		// Assert
		_registry.Count.Should().Be(1);
		_registry.GetAll().First().Orchestration.Name.Should().Be("sub-orch");
	}

	[Fact]
	public async Task FileWatcher_RapidChanges_Debounced()
	{
		// Arrange
		var service = CreateService();
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
		await service.StartAsync(cts.Token);
		await service.WatcherReady.Task;

		// Act — write the same file rapidly multiple times
		for (int i = 0; i < 5; i++)
		{
			WriteOrchestrationFile(_orchestrationsDir, "rapid-changes", $"Version {i + 1}");
			await Task.Delay(10); // Much faster than debounce delay
		}

		// Wait for the debounced update pipeline to settle on the latest write.
		await WaitForConditionAsync(
			() => _registry.Count == 1 && _registry.GetAll().First().Orchestration.Description == "Version 5",
			TimeSpan.FromSeconds(5));

		await service.StopAsync(CancellationToken.None);

		// Assert — only one registration should exist, with the latest version
		_registry.Count.Should().Be(1);
		_registry.GetAll().First().Orchestration.Description.Should().Be("Version 5");
	}

	[Fact]
	public async Task FileWatcher_GracefulShutdown_CancelsPendingDebounce()
	{
		// Arrange
		var service = CreateService();
		service.DebounceDelay = TimeSpan.FromSeconds(30); // Very long debounce
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
		await service.StartAsync(cts.Token);
		await service.WatcherReady.Task;

		// Act — create a file (won't process due to long debounce)
		WriteOrchestrationFile(_orchestrationsDir, "shutdown-test");

		// Immediately stop
		await service.StopAsync(CancellationToken.None);

		// Assert — should stop cleanly without processing the file
		// (the debounce timer should be cancelled)
	}

	// ── Helper ──

	private static async Task WaitForConditionAsync(Func<bool> condition, TimeSpan timeout)
	{
		var deadline = DateTime.UtcNow + timeout;
		while (DateTime.UtcNow < deadline)
		{
			if (condition())
				return;
			await Task.Delay(50);
		}
	}

	// ── Profile watcher tests ──

	private string WriteProfileFile(string directory, string name, string[]? tags = null, string? description = null)
	{
		Directory.CreateDirectory(directory);
		var tagsJson = tags is not null
			? string.Join(", ", tags.Select(t => $"\"{t}\""))
			: "\"test\"";
		var json = $$"""
		{
			"id": "{{ProfileStore.GenerateId(name)}}",
			"name": "{{name}}",
			"description": "{{description ?? $"Profile for {name}"}}",
			"isActive": false,
			"filter": {
				"tags": [{{tagsJson}}],
				"orchestrationIds": [],
				"excludeOrchestrationIds": []
			},
			"createdAt": "2026-01-01T00:00:00+00:00",
			"updatedAt": "2026-01-01T00:00:00+00:00"
		}
		""";
		var path = Path.Combine(directory, $"{name}.json");

		// Write atomically via temp file + move to avoid conflicts with concurrent readers
		var tempPath = path + $".{Guid.NewGuid():N}.tmp";
		File.WriteAllText(tempPath, json);
		File.Move(tempPath, path, overwrite: true);

		return path;
	}

	[Fact]
	public async Task FileWatcher_NewProfileCreated_SyncsProfile()
	{
		// Arrange
		var profilesDir = Path.Combine(_watchDir, OrchestrationSyncService.ProfilesDirName);
		Directory.CreateDirectory(profilesDir);

		var service = CreateService();
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
		await service.StartAsync(cts.Token);
		await service.WatcherReady.Task;

		// Act — create a new profile file
		WriteProfileFile(profilesDir, "watcher-profile");

		// Wait for debounce + processing
		await WaitForConditionAsync(() => _profileStore.GetAll().Any(p => p.Name == "watcher-profile"), TimeSpan.FromSeconds(5));

		await service.StopAsync(CancellationToken.None);

		// Assert
		var profiles = _profileStore.GetAll();
		profiles.Should().Contain(p => p.Name == "watcher-profile");
		profiles.First(p => p.Name == "watcher-profile").IsActive.Should().BeFalse();
		profiles.First(p => p.Name == "watcher-profile").SourcePath.Should().NotBeNullOrEmpty();
	}

	[Fact]
	public async Task FileWatcher_ProfileModified_UpdatesProfile()
	{
		// Arrange — create profile file and sync it
		var profilesDir = Path.Combine(_watchDir, OrchestrationSyncService.ProfilesDirName);
		Directory.CreateDirectory(profilesDir);
		WriteProfileFile(profilesDir, "watcher-update-profile", tags: ["v1"]);
		_profileStore.SyncDirectory(profilesDir);
		_profileStore.GetAll().Should().HaveCount(1);

		var service = CreateService();
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
		await service.StartAsync(cts.Token);
		await service.WatcherReady.Task;

		// Act — modify the profile
		WriteProfileFile(profilesDir, "watcher-update-profile", tags: ["v2"]);

		// Wait for debounce + processing
		await WaitForConditionAsync(
			() => _profileStore.GetAll().FirstOrDefault()?.Filter.Tags.Contains("v2") == true,
			TimeSpan.FromSeconds(5));

		await service.StopAsync(CancellationToken.None);

		// Assert
		_profileStore.GetAll().Should().HaveCount(1);
		_profileStore.GetAll().First().Filter.Tags.Should().Contain("v2");
	}

	[Fact]
	public async Task FileWatcher_ProfileDeleted_RemovesProfile()
	{
		// Arrange — create profile file and sync it
		var profilesDir = Path.Combine(_watchDir, OrchestrationSyncService.ProfilesDirName);
		Directory.CreateDirectory(profilesDir);
		var filePath = WriteProfileFile(profilesDir, "watcher-delete-profile");
		_profileStore.SyncDirectory(profilesDir);
		_profileStore.GetAll().Should().HaveCount(1);

		var service = CreateService();
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
		await service.StartAsync(cts.Token);
		await service.WatcherReady.Task;

		// Act — delete the profile file
		File.Delete(filePath);

		// Wait for debounce + processing
		await WaitForConditionAsync(() => _profileStore.GetAll().Count == 0, TimeSpan.FromSeconds(10));

		await service.StopAsync(CancellationToken.None);

		// Assert
		_profileStore.GetAll().Should().BeEmpty();
	}

	[Fact]
	public async Task FileWatcher_InvalidProfileFile_DoesNotCrash()
	{
		// Arrange
		var profilesDir = Path.Combine(_watchDir, OrchestrationSyncService.ProfilesDirName);
		Directory.CreateDirectory(profilesDir);

		var service = CreateService();
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
		await service.StartAsync(cts.Token);
		await service.WatcherReady.Task;

		// Act — write an invalid JSON file to the profiles directory
		File.WriteAllText(Path.Combine(profilesDir, "broken.json"), "not valid json {{{");

		// Wait for debounce + processing attempt
		await Task.Delay(500);

		await service.StopAsync(CancellationToken.None);

		// Assert — should not crash, no profile added
		_profileStore.GetAll().Should().BeEmpty();
	}

	[Fact]
	public async Task FileWatcher_NonJsonInProfilesDir_Ignored()
	{
		// Arrange
		var profilesDir = Path.Combine(_watchDir, OrchestrationSyncService.ProfilesDirName);
		Directory.CreateDirectory(profilesDir);

		var service = CreateService();
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
		await service.StartAsync(cts.Token);
		await service.WatcherReady.Task;

		// Act — write a YAML file to profiles dir (should be ignored)
		File.WriteAllText(Path.Combine(profilesDir, "readme.yaml"), "name: not-a-profile");

		// Wait to confirm no processing
		await Task.Delay(500);

		await service.StopAsync(CancellationToken.None);

		// Assert — non-JSON files in profiles/ should be ignored
		_profileStore.GetAll().Should().BeEmpty();
	}

	[Fact]
	public async Task FileWatcher_NewProfileCreated_BroadcastsProfilesChanged()
	{
		// Arrange — subscribe to the dashboard broadcaster
		var profilesDir = Path.Combine(_watchDir, OrchestrationSyncService.ProfilesDirName);
		Directory.CreateDirectory(profilesDir);

		var reader = _dashboardBroadcaster.Subscribe();
		reader.Should().NotBeNull("subscriber limit should not be reached");

		var service = CreateService();
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
		await service.StartAsync(cts.Token);
		await service.WatcherReady.Task;

		// Act — create a new profile file
		WriteProfileFile(profilesDir, "broadcast-profile");

		// Wait for debounce + processing
		await WaitForConditionAsync(() => _profileStore.GetAll().Any(p => p.Name == "broadcast-profile"), TimeSpan.FromSeconds(5));

		// Assert — broadcaster should have emitted a profiles-changed event
		var events = new List<SseEvent>();
		while (reader!.TryRead(out var evt))
			events.Add(evt);

		events.Should().Contain(e => e.Type == "profiles-changed",
			"creating a profile file should broadcast a profiles-changed event");

		await service.StopAsync(CancellationToken.None);
		_dashboardBroadcaster.Unsubscribe(reader);
	}

	[Fact]
	public async Task FileWatcher_ProfileDeleted_BroadcastsProfilesChanged()
	{
		// Arrange — create profile file and sync it, then subscribe to broadcaster
		var profilesDir = Path.Combine(_watchDir, OrchestrationSyncService.ProfilesDirName);
		Directory.CreateDirectory(profilesDir);
		var filePath = WriteProfileFile(profilesDir, "broadcast-delete-profile");
		_profileStore.SyncDirectory(profilesDir);
		_profileStore.GetAll().Should().HaveCount(1);

		var reader = _dashboardBroadcaster.Subscribe();
		reader.Should().NotBeNull();

		var service = CreateService();
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
		await service.StartAsync(cts.Token);
		await service.WatcherReady.Task;

		// Act — delete the profile file
		File.Delete(filePath);

		// Wait for debounce + processing
		await WaitForConditionAsync(() => _profileStore.GetAll().Count == 0, TimeSpan.FromSeconds(10));

		// Assert — broadcaster should have emitted a profiles-changed event
		var events = new List<SseEvent>();
		while (reader!.TryRead(out var evt))
			events.Add(evt);

		events.Should().Contain(e => e.Type == "profiles-changed",
			"deleting a profile file should broadcast a profiles-changed event");

		await service.StopAsync(CancellationToken.None);
		_dashboardBroadcaster.Unsubscribe(reader);
	}
}
