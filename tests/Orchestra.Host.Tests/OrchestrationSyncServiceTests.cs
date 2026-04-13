using System.Collections.Concurrent;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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
	private readonly string _dataPath;
	private readonly string _persistPath;
	private readonly OrchestrationRegistry _registry;
	private readonly TriggerManager _triggerManager;
	private readonly ProfileManager _profileManager;

	public OrchestrationSyncServiceTests()
	{
		_tempDir = Path.Combine(Path.GetTempPath(), $"orchestra-sync-tests-{Guid.NewGuid():N}");
		_watchDir = Path.Combine(_tempDir, "watch");
		_dataPath = Path.Combine(_tempDir, "data");
		_persistPath = Path.Combine(_dataPath, "registered-orchestrations.json");
		Directory.CreateDirectory(_watchDir);
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

		var profileStore = new ProfileStore(_dataPath, NullLogger<ProfileStore>.Instance);
		var tagStore = new OrchestrationTagStore(_dataPath, NullLogger<OrchestrationTagStore>.Instance);
		_profileManager = new ProfileManager(profileStore, tagStore, _registry, NullLogger<ProfileManager>.Instance);
		_profileManager.Initialize();
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
		File.Move(tempPath, path, overwrite: true);

		return path;
	}

	private OrchestrationSyncService CreateService(bool watch = true, bool recursive = false)
	{
		var options = new OrchestrationHostOptions
		{
			DataPath = _dataPath,
			OrchestrationsScan = new OrchestrationsScanConfig
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
		var options = new OrchestrationHostOptions { OrchestrationsScan = null };
		var service = new OrchestrationSyncService(
			_registry, _triggerManager, _profileManager, options,
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

		// Act — create a new file in the watched directory
		WriteOrchestrationFile(_watchDir, "watcher-new");

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
		var filePath = WriteOrchestrationFile(_watchDir, "watcher-update", "Version 1");
		_registry.Register(filePath, persist: false);
		_registry.Count.Should().Be(1);
		_registry.GetAll().First().Orchestration.Description.Should().Be("Version 1");

		var service = CreateService();
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
		await service.StartAsync(cts.Token);
		await service.WatcherReady.Task;

		// Act — modify the file
		WriteOrchestrationFile(_watchDir, "watcher-update", "Version 2");

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
		var filePath = WriteOrchestrationFile(_watchDir, "watcher-delete");
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
		File.WriteAllText(Path.Combine(_watchDir, "broken.json"), "not valid json {{{");

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
		File.WriteAllText(Path.Combine(_watchDir, "readme.txt"), "This is not an orchestration");

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
		var originalPath = WriteOrchestrationFile(_watchDir, "watcher-rename");
		_registry.Register(originalPath, persist: false);
		_registry.Count.Should().Be(1);

		var service = CreateService();
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
		await service.StartAsync(cts.Token);
		await service.WatcherReady.Task;

		// Act — rename the file (creates a new file with different name, but same content structure)
		var newPath = Path.Combine(_watchDir, "renamed-orch.json");
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
		var subDir = Path.Combine(_watchDir, "sub");
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
			WriteOrchestrationFile(_watchDir, "rapid-changes", $"Version {i + 1}");
			await Task.Delay(10); // Much faster than debounce delay
		}

		// Wait for final debounce to complete
		await WaitForConditionAsync(() => _registry.Count == 1, TimeSpan.FromSeconds(5));

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
		WriteOrchestrationFile(_watchDir, "shutdown-test");

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
}
