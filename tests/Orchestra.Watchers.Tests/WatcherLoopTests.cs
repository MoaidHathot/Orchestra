using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Orchestra.Engine;
using Orchestra.Watchers.Models;
using Orchestra.Watchers.Runtime;
using Orchestra.Watchers.State;
using Xunit;

namespace Orchestra.Watchers.Tests;

public class WatcherLoopTests
{
	private readonly WatcherSubscriptionRegistry _registry = new();
	private readonly InMemoryStateStore _store = new();
	private readonly ScriptedFetcher _fetcher = new();
	private readonly RecordingDispatcher _dispatcher = new();
	private readonly CapturingLogger _logger = new();

	private WatcherLoop NewLoop(WatcherEntry entry) => new(entry, _registry, _store, _fetcher, new WatcherOutputValidator(), _dispatcher, _logger);

	private static JsonObject Item(string id) => ScriptedFetcher.Item(id);

	[Fact]
	public async Task Fifo_DispatchesNewItemsOnce_AndSkipsSeenOnes()
	{
		var entry = TestEntries.Command();
		_registry.Register(TestEntries.Subscription("orch-a"));
		_fetcher.Enqueue(Item("1"), Item("2")).Enqueue(Item("2"), Item("3"));
		var loop = NewLoop(entry);

		var first = await loop.RunTickAsync(CancellationToken.None);
		var second = await loop.RunTickAsync(CancellationToken.None);

		first.Should().BeEquivalentTo(new WatcherTickResult(true, null, 2, 2, 0, false));
		second.Should().BeEquivalentTo(new WatcherTickResult(true, null, 2, 1, 1, false));
		_dispatcher.Dispatches.Select(d => d.DedupKey).Should().Equal("1", "2", "3");
		_dispatcher.Dispatches.Should().OnlyContain(d => d.Subscription.OrchestrationId == "orch-a" && d.Watcher.Name == "w");
		_dispatcher.Dispatches[0].Parameters.Should().Equal(new Dictionary<string, string> { ["id"] = "1" });

		var state = _store.PeekSubscriber("w", "orch-a")!;
		state.SeenKeys.Select(k => k.Key).Should().Equal(["3", "2", "1"], "most recent first");
		state.Stats.LifetimeDispatched.Should().Be(3);
		state.Mode.Should().Be(WatcherDedupMode.Fifo);
	}

	[Fact]
	public async Task Fifo_DuplicateKeysWithinOneTick_DispatchOnce()
	{
		_registry.Register(TestEntries.Subscription("orch-a"));
		_fetcher.Enqueue(Item("1"), Item("1"), Item("1"));

		var result = await NewLoop(TestEntries.Command()).RunTickAsync(CancellationToken.None);

		result.Dispatched.Should().Be(1);
		result.ItemCount.Should().Be(3);
	}

	[Fact]
	public async Task Fifo_TrimEvictsOldestFirst_AndCountsEvictions()
	{
		var entry = TestEntries.Command(dedup: new WatcherDedup { Key = "{{item.id}}", Fifo = new WatcherDedupFifoOptions { MaxEntries = 2 } });
		_registry.Register(TestEntries.Subscription("orch-a"));
		_fetcher.Enqueue(Item("1"), Item("2"), Item("3"));
		var loop = NewLoop(entry);

		await loop.RunTickAsync(CancellationToken.None);

		var state = _store.PeekSubscriber("w", "orch-a")!;
		state.SeenKeys.Select(k => k.Key).Should().Equal("3", "2");
		state.Stats.Evictions.Should().Be(1);

		// "1" was evicted, so it is new again on the next tick - the documented FIFO trade-off.
		_fetcher.Enqueue(Item("1"));
		var again = await loop.RunTickAsync(CancellationToken.None);
		again.Dispatched.Should().Be(1);
	}

	[Fact]
	public async Task Fifo_HighTrimRate_WarnsOnce_NotBeforeThreshold()
	{
		var entry = TestEntries.Command(dedup: new WatcherDedup { Key = "{{item.id}}", Fifo = new WatcherDedupFifoOptions { MaxEntries = 10 } });
		_registry.Register(TestEntries.Subscription("orch-a"));
		var loop = NewLoop(entry);

		// Tick 1: 10 items, nothing evicted yet.
		_fetcher.Enqueue(Enumerable.Range(0, 10).Select(i => (object)Item($"a{i}")).ToArray());
		await loop.RunTickAsync(CancellationToken.None);
		_logger.Messages(LogLevel.Warning).Should().NotContain(m => m.Contains("FIFO trim rate"));

		// Tick 2: 1 more item -> 1 eviction (10% of 10 = 1, not above threshold).
		_fetcher.Enqueue(Item("b0"));
		await loop.RunTickAsync(CancellationToken.None);
		_logger.Messages(LogLevel.Warning).Should().NotContain(m => m.Contains("FIFO trim rate"));

		// Tick 3: 5 more -> 6 evictions in the window, above 1/hr.
		_fetcher.Enqueue(Enumerable.Range(0, 5).Select(i => (object)Item($"c{i}")).ToArray());
		await loop.RunTickAsync(CancellationToken.None);
		_logger.Messages(LogLevel.Warning).Where(m => m.Contains("FIFO trim rate")).Should().ContainSingle()
			.Which.Should().Contain("orch-a").And.Contain("maxEntries=10");

		// Tick 4: more evictions within the same hour -> no second warning.
		_fetcher.Enqueue(Enumerable.Range(0, 5).Select(i => (object)Item($"d{i}")).ToArray());
		await loop.RunTickAsync(CancellationToken.None);
		_logger.Messages(LogLevel.Warning).Where(m => m.Contains("FIFO trim rate")).Should().ContainSingle();
	}

	[Fact]
	public async Task Presence_ResetsAbsenceWhenPresent_PrunesAfterGraceTicks_AndRedispatchesOnReturn()
	{
		var entry = TestEntries.Command(dedup: new WatcherDedup { Key = "{{item.id}}", Mode = WatcherDedupMode.Presence, Presence = new WatcherDedupPresenceOptions { GraceTicks = 2 } });
		_registry.Register(TestEntries.Subscription("orch-a"));
		var loop = NewLoop(entry);

		_fetcher.Enqueue(Item("x"), Item("y"));
		(await loop.RunTickAsync(CancellationToken.None)).Dispatched.Should().Be(2);

		// x still present, y absent (1/2)
		_fetcher.Enqueue(Item("x"));
		(await loop.RunTickAsync(CancellationToken.None)).Dispatched.Should().Be(0);
		var state = _store.PeekSubscriber("w", "orch-a")!;
		state.Mode.Should().Be(WatcherDedupMode.Presence);
		state.AbsenceTickCounts.Should().Equal(new Dictionary<string, int> { ["y"] = 1 });
		state.SeenKeys.Select(k => k.Key).Should().BeEquivalentTo("x", "y");

		// y returns -> absence cleared, not re-dispatched (still in the seen-set)
		_fetcher.Enqueue(Item("x"), Item("y"));
		(await loop.RunTickAsync(CancellationToken.None)).Dispatched.Should().Be(0);
		_store.PeekSubscriber("w", "orch-a")!.AbsenceTickCounts.Should().BeEmpty();

		// y absent twice -> pruned
		_fetcher.Enqueue(Item("x"));
		await loop.RunTickAsync(CancellationToken.None);
		_fetcher.Enqueue(Item("x"));
		await loop.RunTickAsync(CancellationToken.None);
		state = _store.PeekSubscriber("w", "orch-a")!;
		state.SeenKeys.Select(k => k.Key).Should().Equal("x");
		state.AbsenceTickCounts.Should().BeEmpty();

		// y reappears -> dispatches again
		_fetcher.Enqueue(Item("x"), Item("y"));
		(await loop.RunTickAsync(CancellationToken.None)).Dispatched.Should().Be(1);
		_dispatcher.Dispatches.Select(d => d.DedupKey).Should().Equal("x", "y", "y");
		_logger.Messages(LogLevel.Warning).Should().NotContain(m => m.Contains("FIFO trim rate"), "presence mode never warns about trimming");
	}

	[Fact]
	public async Task Catchup_FromNow_SeedsNewSubscriberWithSharedSeenKeys()
	{
		var entry = TestEntries.Command();
		_registry.Register(TestEntries.Subscription("orch-a"));
		var loop = NewLoop(entry);
		_fetcher.Enqueue(Item("old-1"), Item("old-2"));
		await loop.RunTickAsync(CancellationToken.None);
		_dispatcher.Count.Should().Be(2);

		// A second orchestration subscribes later with catchup: from-now.
		_registry.Register(TestEntries.Subscription("orch-b", catchup: WatcherCatchupMode.FromNow));
		_fetcher.Enqueue(Item("old-1"), Item("old-2"), Item("new-3"));
		var result = await loop.RunTickAsync(CancellationToken.None);

		result.Dispatched.Should().Be(2, "one new item for each subscriber");
		_dispatcher.Dispatches.Where(d => d.Subscription.OrchestrationId == "orch-b").Select(d => d.DedupKey).Should().Equal("new-3");
	}

	[Fact]
	public async Task Catchup_All_LeavesNewSubscriberEmpty_SoBacklogDispatches()
	{
		var entry = TestEntries.Command();
		_registry.Register(TestEntries.Subscription("orch-a"));
		var loop = NewLoop(entry);
		_fetcher.Enqueue(Item("old-1"), Item("old-2"));
		await loop.RunTickAsync(CancellationToken.None);

		_registry.Register(TestEntries.Subscription("orch-b", catchup: WatcherCatchupMode.All));
		_fetcher.Enqueue(Item("old-1"), Item("old-2"));
		var result = await loop.RunTickAsync(CancellationToken.None);

		result.Dispatched.Should().Be(2);
		_dispatcher.Dispatches.Where(d => d.Subscription.OrchestrationId == "orch-b").Select(d => d.DedupKey).Should().Equal("old-1", "old-2");
		_dispatcher.Dispatches.Where(d => d.Subscription.OrchestrationId == "orch-a").Should().HaveCount(2, "the first subscriber does not re-dispatch");
	}

	[Fact]
	public async Task Subscribers_HaveIndependentDedupState()
	{
		var entry = TestEntries.Command();
		_registry.Register(TestEntries.Subscription("orch-a"));
		_registry.Register(TestEntries.Subscription("orch-b"));
		var loop = NewLoop(entry);

		_fetcher.Enqueue(Item("1"));
		await loop.RunTickAsync(CancellationToken.None);

		_dispatcher.Dispatches.Select(d => d.Subscription.OrchestrationId).Should().BeEquivalentTo("orch-a", "orch-b");
		_fetcher.Calls.Should().Be(1, "one fetch per tick regardless of subscriber count");
	}

	[Fact]
	public async Task Cursor_AdvancesOnSuccess_PersistsAcrossTicks_AndSurvivesFailure()
	{
		var entry = TestEntries.Command();
		_registry.Register(TestEntries.Subscription("orch-a"));
		var loop = NewLoop(entry);

		_fetcher.EnqueueWithCursor(JsonNode.Parse("""{ "page": 1 }"""), Item("1"));
		await loop.RunTickAsync(CancellationToken.None);
		_fetcher.Requests[0].Cursor.Should().BeNull();
		_store.PeekShared("w")!.Cursor!["page"]!.GetValue<int>().Should().Be(1);

		// null / omitted cursor leaves it unchanged
		_fetcher.Enqueue(Item("2"));
		await loop.RunTickAsync(CancellationToken.None);
		_fetcher.Requests[1].Cursor!["page"]!.GetValue<int>().Should().Be(1);
		_store.PeekShared("w")!.Cursor!["page"]!.GetValue<int>().Should().Be(1);

		_fetcher.EnqueueFailure();
		var failed = await loop.RunTickAsync(CancellationToken.None);
		failed.Succeeded.Should().BeFalse();
		_store.PeekShared("w")!.Cursor!["page"]!.GetValue<int>().Should().Be(1, "failures never touch the cursor");

		_fetcher.EnqueueWithCursor(JsonNode.Parse("""{ "page": 2 }"""));
		await loop.RunTickAsync(CancellationToken.None);
		_fetcher.Requests[3].Cursor!["page"]!.GetValue<int>().Should().Be(1);
		_store.PeekShared("w")!.Cursor!["page"]!.GetValue<int>().Should().Be(2);
	}

	[Fact]
	public async Task Failures_IncrementConsecutiveCount_ResetOnSuccess_AndPersistStats()
	{
		var entry = TestEntries.Command();
		_registry.Register(TestEntries.Subscription("orch-a"));
		var loop = NewLoop(entry);
		_fetcher.EnqueueFailure("exit code 2.", 2).EnqueueFailure("timed out after 1s.", -1).Enqueue(Item("1"));

		await loop.RunTickAsync(CancellationToken.None);
		_store.PeekShared("w")!.Stats.ConsecutiveFailures.Should().Be(1);
		await loop.RunTickAsync(CancellationToken.None);
		var shared = _store.PeekShared("w")!;
		shared.Stats.ConsecutiveFailures.Should().Be(2);
		shared.Stats.FetchFailures.Should().Be(2);
		shared.Stats.Ticks.Should().Be(2);
		shared.Stats.LastSuccessAt.Should().BeNull();

		await loop.RunTickAsync(CancellationToken.None);
		shared = _store.PeekShared("w")!;
		shared.Stats.ConsecutiveFailures.Should().Be(0);
		shared.Stats.FetchFailures.Should().Be(2);
		shared.Stats.LastSuccessAt.Should().NotBeNull();

		_logger.Messages(LogLevel.Warning).Should().Contain(m => m.Contains("fetch failed (exit 2)") && m.Contains("exit code 2."));
		_logger.Messages(LogLevel.Warning).Should().Contain(m => m.Contains("stderr text"), "stderr is logged on failure");
		loop.IsFaulted.Should().BeFalse();
	}

	[Fact]
	public async Task FailurePolicy_Ignore_LogsDebugOnly()
	{
		var entry = TestEntries.Command(failurePolicy: WatcherFailurePolicy.Ignore);
		_registry.Register(TestEntries.Subscription("orch-a"));
		_fetcher.EnqueueFailure();

		await NewLoop(entry).RunTickAsync(CancellationToken.None);

		_logger.Messages(LogLevel.Warning).Should().NotContain(m => m.Contains("fetch failed (exit"));
		_logger.Messages(LogLevel.Debug).Should().Contain(m => m.Contains("ignored by policy"));
	}

	[Fact]
	public async Task FailurePolicy_Fail_StopsOnlyThisLoop()
	{
		var entry = TestEntries.Command(failurePolicy: WatcherFailurePolicy.Fail, intervalSeconds: 1);
		_registry.Register(TestEntries.Subscription("orch-a"));
		_fetcher.EnqueueFailure().Fallback(_ => ScriptedFetcher.Success(null, Item("should-not-run")));
		var loop = NewLoop(entry);

		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
		await loop.RunAsync(cts.Token);

		cts.IsCancellationRequested.Should().BeFalse("the loop must exit on its own");
		loop.IsFaulted.Should().BeTrue();
		loop.FaultReason.Should().Contain("exit code 1");
		_fetcher.Calls.Should().Be(1);
		_dispatcher.Count.Should().Be(0);
		_logger.Messages(LogLevel.Error).Should().Contain(m => m.Contains("failurePolicy=fail"));
	}

	[Fact]
	public async Task SchemaInvalidOutput_IsTreatedAsFetchFailure_WithPointerLogged()
	{
		var entry = TestEntries.Command();
		_registry.Register(TestEntries.Subscription("orch-a"));
		_fetcher.EnqueueRaw("""{ "items": { "bad": true } }""");

		var result = await NewLoop(entry).RunTickAsync(CancellationToken.None);

		result.Succeeded.Should().BeFalse();
		result.FailureReason.Should().Contain("schema validation").And.Contain("/items");
		_store.PeekShared("w")!.Stats.ConsecutiveFailures.Should().Be(1);
		_logger.Messages(LogLevel.Error).Should().Contain(m => m.Contains("failed schema validation") && m.Contains("/items"));
	}

	[Fact]
	public async Task UnsupportedProtocolVersion_IsTreatedAsFetchFailure()
	{
		_registry.Register(TestEntries.Subscription("orch-a"));
		_fetcher.EnqueueRaw("""{ "protocolVersion": "9", "items": [] }""");

		var result = await NewLoop(TestEntries.Command()).RunTickAsync(CancellationToken.None);

		result.Succeeded.Should().BeFalse();
		result.FailureReason.Should().Contain("unsupported protocolVersion: 9");
	}

	[Fact]
	public async Task CustomItemsPath_SelectsItemsFromNonStandardShape()
	{
		var entry = TestEntries.Command(items: new WatcherItemsConfig { JsonPath = "$.cursor.data.results[*]" });
		_registry.Register(TestEntries.Subscription("orch-a"));
		_fetcher.EnqueueRaw("""{ "cursor": { "data": { "results": [ { "id": "r1" }, { "id": "r2" } ] } } }""");

		var result = await NewLoop(entry).RunTickAsync(CancellationToken.None);

		result.ItemCount.Should().Be(2);
		result.Dispatched.Should().Be(2);
	}

	[Fact]
	public async Task DefaultDedupKey_IsCanonicalHash_WhenNoTemplate()
	{
		var entry = TestEntries.Command(dedup: new WatcherDedup());
		_registry.Register(TestEntries.Subscription("orch-a"));
		_fetcher.Enqueue(JsonNode.Parse("""{ "b": 1, "a": 2 }""")!).Enqueue(JsonNode.Parse("""{ "a": 2, "b": 1 }""")!);
		var loop = NewLoop(entry);

		await loop.RunTickAsync(CancellationToken.None);
		var second = await loop.RunTickAsync(CancellationToken.None);

		second.Dispatched.Should().Be(0, "same content in a different key order is the same item");
		_dispatcher.Dispatches.Should().ContainSingle().Which.DedupKey.Should().HaveLength(16);
	}

	[Fact]
	public async Task Dispatch_FailuresAreLoggedAndCounted_ItemStillMarkedSeen()
	{
		var entry = TestEntries.Command();
		_registry.Register(TestEntries.Subscription("orch-a"));
		_dispatcher.FailWhen = d => d.DedupKey == "bad" ? new InvalidOperationException("launcher exploded") : null;
		_fetcher.Enqueue(Item("bad"), Item("good")).Enqueue(Item("bad"), Item("good"));
		var loop = NewLoop(entry);

		var first = await loop.RunTickAsync(CancellationToken.None);
		var second = await loop.RunTickAsync(CancellationToken.None);

		first.Dispatched.Should().Be(1);
		second.Dispatched.Should().Be(0, "a failed launch is not retried every tick");
		_logger.Entries.Should().Contain(e => e.Level == LogLevel.Error && e.Message.Contains("dispatch failed") && e.Exception is InvalidOperationException);
	}

	[Fact]
	public async Task MaxConcurrent_BoundsParallelLaunches()
	{
		var entry = TestEntries.Command();
		_registry.Register(TestEntries.Subscription("orch-a", maxConcurrent: 2));
		_dispatcher.Gate = new SemaphoreSlim(0);
		_fetcher.Enqueue(Enumerable.Range(0, 6).Select(i => (object)Item($"i{i}")).ToArray());
		var loop = NewLoop(entry);

		var tick = loop.RunTickAsync(CancellationToken.None);
		await Task.Delay(200);
		tick.IsCompleted.Should().BeFalse("launches are gated");
		_dispatcher.MaxObservedConcurrency.Should().Be(2);

		_dispatcher.Gate.Release(6);
		var result = await tick.WaitAsync(TimeSpan.FromSeconds(10));

		result.Dispatched.Should().Be(6);
		_dispatcher.MaxObservedConcurrency.Should().Be(2);
	}

	[Fact]
	public async Task Cancellation_DuringDispatch_AbortsWithoutWritingSubscriberState()
	{
		var entry = TestEntries.Command();
		_registry.Register(TestEntries.Subscription("orch-a"));
		using var cts = new CancellationTokenSource();
		_dispatcher.Gate = new SemaphoreSlim(0);
		_fetcher.Enqueue(Item("1"));
		var loop = NewLoop(entry);

		var tick = loop.RunTickAsync(cts.Token);
		await Task.Delay(100);
		cts.Cancel();

		var act = () => tick;
		await act.Should().ThrowAsync<OperationCanceledException>();
		_store.SubscriberWrites.Should().Be(0);
		_store.SharedWrites.Should().Be(0);
		_dispatcher.Count.Should().Be(0);
	}

	[Fact]
	public async Task RunAsync_IntervalSchedule_FiresImmediately_ThenHonoursInterval()
	{
		var entry = TestEntries.Command(intervalSeconds: 1);
		_registry.Register(TestEntries.Subscription("orch-a"));
		_fetcher.Enqueue(Item("1")).Fallback(_ => ScriptedFetcher.Success(null, Item("1")));
		var loop = NewLoop(entry);
		using var cts = new CancellationTokenSource();

		var run = loop.RunAsync(cts.Token);
		await _dispatcher.WaitForCountAsync(1, TimeSpan.FromSeconds(5));
		var started = DateTime.UtcNow;
		while (loop.TickCount < 3 && DateTime.UtcNow - started < TimeSpan.FromSeconds(10))
			await Task.Delay(50);

		cts.Cancel();
		var act = () => run;
		await act.Should().ThrowAsync<OperationCanceledException>();

		loop.TickCount.Should().BeGreaterThanOrEqualTo(3);
		_dispatcher.Count.Should().Be(1, "the same item is not re-dispatched on later ticks");
		(DateTime.UtcNow - started).Should().BeGreaterThan(TimeSpan.FromSeconds(1.5), "ticks 2 and 3 waited for the interval");
	}

	[Fact]
	public async Task SharedSeenKeys_AreMaintainedWithFifoRules_EvenForPresenceSubscribers()
	{
		var entry = TestEntries.Command(dedup: new WatcherDedup { Key = "{{item.id}}", Mode = WatcherDedupMode.Presence });
		_registry.Register(TestEntries.Subscription("orch-a"));
		_fetcher.Enqueue(Item("1"), Item("2")).Enqueue(Item("3"));
		var loop = NewLoop(entry);

		await loop.RunTickAsync(CancellationToken.None);
		await loop.RunTickAsync(CancellationToken.None);

		_store.PeekShared("w")!.SharedSeenKeys.Select(k => k.Key).Should().Equal("3", "2", "1");
	}

	[Fact]
	public async Task ModeChange_BetweenRuns_ResetsPresenceBookkeeping()
	{
		_registry.Register(TestEntries.Subscription("orch-a"));
		var presence = TestEntries.Command(dedup: new WatcherDedup { Key = "{{item.id}}", Mode = WatcherDedupMode.Presence });
		_fetcher.Enqueue(Item("1"), Item("2")).Enqueue(Item("1"));
		var loop = NewLoop(presence);
		await loop.RunTickAsync(CancellationToken.None);
		await loop.RunTickAsync(CancellationToken.None);
		_store.PeekSubscriber("w", "orch-a")!.AbsenceTickCounts.Should().ContainKey("2");

		var fifo = TestEntries.Command();
		_fetcher.Enqueue(Item("1"));
		await NewLoop(fifo).RunTickAsync(CancellationToken.None);

		var state = _store.PeekSubscriber("w", "orch-a")!;
		state.Mode.Should().Be(WatcherDedupMode.Fifo);
		state.AbsenceTickCounts.Should().BeEmpty();
		state.SeenKeys.Select(k => k.Key).Should().Contain("1", "existing seen keys carry over");
	}

	[Fact]
	public async Task NoSubscribers_StillFetchesAndAdvancesSharedState()
	{
		_fetcher.EnqueueWithCursor(JsonNode.Parse("""{ "c": 1 }"""), Item("1"));

		var result = await NewLoop(TestEntries.Command()).RunTickAsync(CancellationToken.None);

		result.Dispatched.Should().Be(0);
		_store.PeekShared("w")!.SharedSeenKeys.Should().ContainSingle();
		_store.PeekShared("w")!.Cursor!["c"]!.GetValue<int>().Should().Be(1);
	}
}
