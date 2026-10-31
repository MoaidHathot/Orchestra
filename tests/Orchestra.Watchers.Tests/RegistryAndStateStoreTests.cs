using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Orchestra.Watchers.Models;
using Orchestra.Watchers.Runtime;
using Orchestra.Watchers.State;
using Xunit;

namespace Orchestra.Watchers.Tests;

public class WatcherSubscriptionRegistryTests
{
	[Fact]
	public void Register_FirstSubscription_ReportsFirst_AndUnregisterLast_ReportsLast()
	{
		var registry = new WatcherSubscriptionRegistry();

		var first = registry.Register(TestEntries.Subscription("a"));
		var second = registry.Register(TestEntries.Subscription("b"));

		first.IsFirstForWatcher.Should().BeTrue();
		first.Replaced.Should().BeFalse();
		second.IsFirstForWatcher.Should().BeFalse();
		registry.Count("w").Should().Be(2);
		registry.WatcherNames.Should().Equal("w");

		registry.Unregister("w", "a").Should().Be(new WatcherUnregistrationOutcome(Removed: true, IsLastForWatcher: false));
		registry.Unregister("w", "a").Should().Be(new WatcherUnregistrationOutcome(Removed: false, IsLastForWatcher: false));
		registry.Unregister("w", "b").Should().Be(new WatcherUnregistrationOutcome(Removed: true, IsLastForWatcher: true));
		registry.WatcherNames.Should().BeEmpty();
		registry.Unregister("w", "b").IsLastForWatcher.Should().BeTrue();
	}

	[Fact]
	public void Register_SameOrchestrationTwice_ReplacesInsteadOfDuplicating()
	{
		var registry = new WatcherSubscriptionRegistry();
		registry.Register(TestEntries.Subscription("a", maxConcurrent: 1));

		var outcome = registry.Register(TestEntries.Subscription("a", maxConcurrent: 7));

		outcome.Replaced.Should().BeTrue();
		outcome.IsFirstForWatcher.Should().BeFalse();
		registry.Snapshot("w").Should().ContainSingle().Which.MaxConcurrent.Should().Be(7);
	}

	[Fact]
	public async Task Snapshot_IsAStableCopy_UnderConcurrentMutation()
	{
		var registry = new WatcherSubscriptionRegistry();
		using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(1));

		var writer = Task.Run(() =>
		{
			var i = 0;
			while (!stop.IsCancellationRequested)
			{
				registry.Register(TestEntries.Subscription($"o{i % 50}"));
				registry.Unregister("w", $"o{(i + 25) % 50}");
				i++;
			}
		});

		var reader = Task.Run(() =>
		{
			while (!stop.IsCancellationRequested)
			{
				var snapshot = registry.Snapshot("w");
				foreach (var sub in snapshot)
					sub.Should().NotBeNull();
				_ = registry.WatcherNames;
			}
		});

		var act = () => Task.WhenAll(writer, reader);
		await act.Should().NotThrowAsync();
	}
}

public class FileWatcherStateStoreTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), $"orchestra-watcher-state-{Guid.NewGuid():N}");

	public void Dispose()
	{
		try { Directory.Delete(_root, recursive: true); } catch { }
	}

	private FileWatcherStateStore NewStore() => new(_root, NullLogger<FileWatcherStateStore>.Instance);

	[Fact]
	public async Task SharedState_RoundTrips()
	{
		var store = NewStore();
		var state = new SharedWatcherState
		{
			Watcher = "PR Comments/prod",
			Cursor = JsonNode.Parse("""{ "lastEventId": 4823 }"""),
			SharedSeenKeys = [new SeenKeyEntry("k1", DateTimeOffset.Parse("2026-01-02T03:04:05Z")), new SeenKeyEntry("k2", DateTimeOffset.Parse("2026-01-01T00:00:00Z"))],
			Stats = new SharedWatcherStats { Ticks = 7, FetchFailures = 2, ConsecutiveFailures = 1, LastTickAt = DateTimeOffset.UtcNow },
		};

		await store.WriteSharedAsync(state, CancellationToken.None);
		var loaded = await store.ReadSharedAsync("PR Comments/prod", CancellationToken.None);

		loaded.Watcher.Should().Be("PR Comments/prod");
		loaded.Cursor!["lastEventId"]!.GetValue<int>().Should().Be(4823);
		loaded.SharedSeenKeys.Should().Equal(state.SharedSeenKeys);
		loaded.Stats.Ticks.Should().Be(7);
		loaded.Stats.FetchFailures.Should().Be(2);
		loaded.Stats.ConsecutiveFailures.Should().Be(1);
		File.Exists(Path.Combine(_root, "pr-comments-prod.state.json")).Should().BeTrue("names are sanitized for file names");
		Directory.GetFiles(_root, "*.tmp").Should().BeEmpty();
	}

	[Fact]
	public async Task SubscriberState_RoundTrips_AndIsNewOnlyWhenMissing()
	{
		var store = NewStore();

		var fresh = await store.ReadSubscriberAsync("w", "orch-1", CancellationToken.None);
		fresh.IsNew.Should().BeTrue();
		fresh.SeenKeys.Should().BeEmpty();

		fresh.Mode = WatcherDedupMode.Presence;
		fresh.SeenKeys.Add(new SeenKeyEntry("a", DateTimeOffset.UtcNow));
		fresh.AbsenceTickCounts["a"] = 2;
		fresh.Stats.LifetimeDispatched = 3;
		await store.WriteSubscriberAsync(fresh, CancellationToken.None);

		var loaded = await store.ReadSubscriberAsync("w", "orch-1", CancellationToken.None);
		loaded.IsNew.Should().BeFalse();
		loaded.Mode.Should().Be(WatcherDedupMode.Presence);
		loaded.SeenKeys.Should().ContainSingle().Which.Key.Should().Be("a");
		loaded.AbsenceTickCounts.Should().ContainKey("a").WhoseValue.Should().Be(2);
		loaded.Stats.LifetimeDispatched.Should().Be(3);
		File.Exists(Path.Combine(_root, "w--orch-1.state.json")).Should().BeTrue();
	}

	[Fact]
	public async Task Read_MissingFiles_ReturnFreshDefaults_NotThrow()
	{
		var store = NewStore();

		var shared = await store.ReadSharedAsync("never-written", CancellationToken.None);
		var sub = await store.ReadSubscriberAsync("never-written", "o", CancellationToken.None);

		shared.Cursor.Should().BeNull();
		shared.Stats.Ticks.Should().Be(0);
		sub.IsNew.Should().BeTrue();
		Directory.Exists(_root).Should().BeFalse("reads must not create the directory");
	}

	[Fact]
	public async Task Write_WhenTargetIsOpen_RespectsPlatformAtomicReplaceSemantics()
	{
		var store = NewStore();
		await store.WriteSharedAsync(new SharedWatcherState { Watcher = "w", Cursor = JsonValue.Create("v1") }, CancellationToken.None);
		var path = store.GetSharedPath("w");
		var before = await File.ReadAllTextAsync(path);

		using (var openFile = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
		{
			var act = () => store.WriteSharedAsync(new SharedWatcherState { Watcher = "w", Cursor = JsonValue.Create("v2") }, CancellationToken.None);
			if (OperatingSystem.IsWindows())
			{
				// Without FileShare.Delete, Windows denies replacing the directory entry.
				(await act.Should().ThrowAsync<Exception>()).Which.Should().Match(e => e is IOException || e is UnauthorizedAccessException);
			}
			else
			{
				// Unix permits rename over an open file. Existing readers keep the old inode,
				// while a new reader observes the complete replacement document.
				await act();
				(await store.ReadSharedAsync("w", CancellationToken.None)).Cursor!.GetValue<string>().Should().Be("v2");
			}
			using var reader = new StreamReader(openFile, leaveOpen: true);
			(await reader.ReadToEndAsync()).Should().Be(before);
		}

		if (OperatingSystem.IsWindows())
			(await File.ReadAllTextAsync(path)).Should().Be(before);
		Directory.GetFiles(_root, "*.tmp").Should().BeEmpty();
	}

	[Fact]
	public async Task Write_WhenReplacementFails_PreservesDestinationAndRemovesTempFile()
	{
		var store = NewStore();
		// Replacing a nonempty directory with a file fails on both Windows and Unix,
		// independently of permissions or whether the test runs with elevated privileges.
		var destination = store.GetSharedPath("w");
		Directory.CreateDirectory(destination);
		var sentinel = Path.Combine(destination, "keep.txt");
		await File.WriteAllTextAsync(sentinel, "original content");

		var act = () => store.WriteSharedAsync(new SharedWatcherState { Watcher = "w", Cursor = JsonValue.Create("v2") }, CancellationToken.None);
		(await act.Should().ThrowAsync<Exception>()).Which.Should().Match(e => e is IOException || e is UnauthorizedAccessException);

		(await File.ReadAllTextAsync(sentinel)).Should().Be("original content");
		Directory.GetFiles(_root, "*.tmp").Should().BeEmpty("failed atomic replacements clean up the temporary file");
	}

	[Fact]
	public async Task Read_VersionMismatch_ThrowsClearValidationException()
	{
		var store = NewStore();
		Directory.CreateDirectory(_root);
		await File.WriteAllTextAsync(store.GetSharedPath("w"), """{ "version": 99, "watcher": "w" }""");

		var act = () => store.ReadSharedAsync("w", CancellationToken.None);

		var ex = await act.Should().ThrowAsync<WatcherValidationException>();
		ex.Which.Message.Should().Contain("version 99").And.Contain("Delete the file");
	}

	[Fact]
	public async Task Read_CorruptJson_ThrowsValidationException()
	{
		var store = NewStore();
		Directory.CreateDirectory(_root);
		await File.WriteAllTextAsync(store.GetSubscriberPath("w", "o"), "{ not json");

		var act = () => store.ReadSubscriberAsync("w", "o", CancellationToken.None);

		await act.Should().ThrowAsync<WatcherValidationException>();
	}

	[Theory]
	[InlineData(@"C:\Users\me\AppData\Roaming\Orchestra", @"C:\Users\me\AppData\Roaming\Orchestra\.orchestra\watchers")]
	[InlineData(@"W:\repo\.orchestra", @"W:\repo\.orchestra\watchers")]
	[InlineData(@"W:\repo\.orchestra\", @"W:\repo\.orchestra\watchers")]
	public void ResolveStateDirectory_UsesConfigDirectoryRoot_WithoutDoublingDotOrchestra(string configDir, string expected)
	{
		if (!OperatingSystem.IsWindows())
			return;

		FileWatcherStateStore.ResolveStateDirectory(configDir).Should().Be(expected);
	}

	[Fact]
	public void ResolveStateDirectory_IsUnderTheProcessTrackerRoot()
	{
		// The PID file the host writes is <configDir>/.orchestra.pids.json; watcher state must share that root.
		var configDir = Path.Combine(_root, "config");
		var pidFile = Path.Combine(configDir, ".orchestra.pids.json");

		var stateDir = FileWatcherStateStore.ResolveStateDirectory(configDir);

		Path.GetDirectoryName(pidFile).Should().Be(Path.GetFullPath(configDir));
		stateDir.Should().StartWith(Path.GetFullPath(configDir));
	}

	[Fact]
	public async Task ConcurrentWrites_ToSameFile_Serialize()
	{
		var store = NewStore();
		var tasks = Enumerable.Range(0, 20).Select(i => store.WriteSharedAsync(new SharedWatcherState { Watcher = "w", Cursor = JsonValue.Create(i) }, CancellationToken.None));

		await Task.WhenAll(tasks);

		var loaded = await store.ReadSharedAsync("w", CancellationToken.None);
		loaded.Cursor!.GetValue<int>().Should().BeInRange(0, 19);
		Directory.GetFiles(_root, "*.tmp").Should().BeEmpty();
	}
}

public class WatcherNamesTests
{
	[Theory]
	[InlineData("github-pr-comments", "github-pr-comments")]
	[InlineData("PR Comments/prod:v2", "pr-comments-prod-v2")]
	[InlineData("--weird--", "weird")]
	[InlineData("!!!", "watcher")]
	public void Sanitize_MatchesOrchestrationIdRules(string input, string expected)
	{
		WatcherNames.Sanitize(input).Should().Be(expected);
	}
}
