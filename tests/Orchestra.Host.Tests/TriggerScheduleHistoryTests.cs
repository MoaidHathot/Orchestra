using System.Collections.Concurrent;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Orchestra.Engine;
using Orchestra.Host.Api;
using Orchestra.Host.Persistence;
using Orchestra.Host.Triggers;
using Xunit;

namespace Orchestra.Host.Tests;

/// <summary>
/// Behavioural tests for the schedule-history fix: interval triggers should honor
/// the persisted <c>LastFireTime</c> across restarts and profile re-activation so a
/// trigger that runs every N hours does not silently reset its clock every time the
/// host process restarts or a profile toggles. Loop triggers configured with
/// <see cref="LoopTriggerConfig.AutoResume"/> should also re-fire on startup.
/// </summary>
/// <remarks>
/// These tests bypass the HTTP surface and exercise <see cref="TriggerManager"/>'s
/// scheduling decisions directly. The trigger manager's background loop is not started —
/// we drive seeding explicitly via the <c>internal</c> hook so the assertions are
/// deterministic.
/// </remarks>
public class TriggerScheduleHistoryTests : IDisposable
{
	private readonly string _tempDir;
	private readonly string _runsDir;
	private readonly FileSystemRunStore _runStore;

	public TriggerScheduleHistoryTests()
	{
		_tempDir = Path.Combine(Path.GetTempPath(), $"orchestra-schedule-history-tests-{Guid.NewGuid():N}");
		_runsDir = Path.Combine(_tempDir, "runs");
		Directory.CreateDirectory(_runsDir);
		_runStore = new FileSystemRunStore(_runsDir, NullLogger<FileSystemRunStore>.Instance);
	}

	public void Dispose()
	{
		if (Directory.Exists(_tempDir))
		{
			try { Directory.Delete(_tempDir, recursive: true); }
			catch { /* best-effort cleanup */ }
		}
	}

	private TriggerManager CreateTriggerManager() => new(
		new ConcurrentDictionary<string, CancellationTokenSource>(),
		new ConcurrentDictionary<string, ActiveExecutionInfo>(),
		agentBuilder: null!,
		scheduler: null!,
		loggerFactory: NullLoggerFactory.Instance,
		logger: new NullLogger<TriggerManager>(),
		runsDir: _runsDir,
		runStore: _runStore,
		checkpointStore: null!,
		launcher: null!,
		dataPath: _tempDir);

	private static OrchestrationRunRecord MakeRunRecord(string orchestrationName, DateTimeOffset startedAt)
	{
		var runId = Guid.NewGuid().ToString("N")[..12];
		return new OrchestrationRunRecord
		{
			RunId = runId,
			OrchestrationName = orchestrationName,
			StartedAt = startedAt,
			CompletedAt = startedAt.AddSeconds(1),
			Status = ExecutionStatus.Succeeded,
			TriggeredBy = "scheduler",
			FinalContent = "ok",
			HookExecutions = [],
			StepRecords = new Dictionary<string, StepRunRecord>(),
			AllStepRecords = new Dictionary<string, StepRunRecord>(),
		};
	}

	// Use IRunStore to disambiguate the SaveRunAsync overload set on FileSystemRunStore.
	private IRunStore Store => _runStore;

	// ─── RegisterTrigger / SetTriggerEnabled honoring preserved LastFireTime ───

	[Fact]
	public void RegisterTrigger_IntervalSchedulerWithNoExistingState_UsesNowPlusInterval()
	{
		var mgr = CreateTriggerManager();
		var config = new SchedulerTriggerConfig
		{
			Type = TriggerType.Scheduler,
			Enabled = true,
			IntervalSeconds = 60 * 60 * 10, // 10 hours
		};

		var before = DateTime.UtcNow;
		var reg = mgr.RegisterTrigger(
			orchestrationPath: Path.Combine(_tempDir, "fresh.json"),
			config: config,
			orchestrationId: "fresh-orch",
			preloadedOrchestration: new Orchestration { Name = "fresh", Description = "fresh", Steps = [] });
		var after = DateTime.UtcNow;

		reg.NextFireTime.Should().NotBeNull();
		var nf = reg.NextFireTime!.Value;
		nf.Should().BeOnOrAfter(before.AddSeconds(60 * 60 * 10 - 1));
		nf.Should().BeOnOrBefore(after.AddSeconds(60 * 60 * 10 + 1));
	}

	[Fact]
	public void RegisterTrigger_ReRegistrationWithPreservedLastFireTime_RecomputesNextFireFromHistory()
	{
		var mgr = CreateTriggerManager();
		var nineHoursAgo = DateTime.UtcNow - TimeSpan.FromHours(9);
		var path = Path.Combine(_tempDir, "interval.json");
		var orchestration = new Orchestration { Name = "interval", Description = "interval", Steps = [] };

		// First registration — simulates the steady-state where the trigger has already
		// fired in-process at some past time.
		var first = mgr.RegisterTrigger(
			orchestrationPath: path,
			config: new SchedulerTriggerConfig
			{
				Type = TriggerType.Scheduler,
				Enabled = true,
				IntervalSeconds = 60 * 60 * 10, // 10h
			},
			orchestrationId: "interval-orch",
			preloadedOrchestration: orchestration);
		first.LastFireTime = nineHoursAgo;

		// Re-register (simulates a config edit / file-watcher reload).
		var second = mgr.RegisterTrigger(
			orchestrationPath: path,
			config: new SchedulerTriggerConfig
			{
				Type = TriggerType.Scheduler,
				Enabled = true,
				IntervalSeconds = 60 * 60 * 10,
			},
			orchestrationId: "interval-orch",
			preloadedOrchestration: orchestration);

		second.LastFireTime.Should().Be(nineHoursAgo, "RunCount/LastFireTime are preserved across re-registration");

		var due = nineHoursAgo + TimeSpan.FromHours(10);
		second.NextFireTime.Should().BeCloseTo(due, TimeSpan.FromSeconds(2),
			"interval triggers must wait the REMAINING time after a re-register, not reset the full interval");
	}

	[Fact]
	public void SetTriggerEnabled_ReactivatedIntervalScheduler_HonorsPriorLastFireTime()
	{
		var mgr = CreateTriggerManager();
		var orchestration = new Orchestration { Name = "profiled", Description = "profiled", Steps = [] };
		var path = Path.Combine(_tempDir, "profiled.json");

		var reg = mgr.RegisterTrigger(
			orchestrationPath: path,
			config: new SchedulerTriggerConfig
			{
				Type = TriggerType.Scheduler,
				Enabled = true,
				IntervalSeconds = 60 * 60 * 10,
			},
			orchestrationId: "profiled-orch",
			preloadedOrchestration: orchestration);

		var nineHoursAgo = DateTime.UtcNow - TimeSpan.FromHours(9);
		reg.LastFireTime = nineHoursAgo;

		// Simulate profile deactivation → activation.
		mgr.SetTriggerEnabled("profiled-orch", false);
		reg.NextFireTime.Should().BeNull("disabling clears NextFireTime");

		mgr.SetTriggerEnabled("profiled-orch", true);

		var due = nineHoursAgo + TimeSpan.FromHours(10);
		reg.NextFireTime.Should().BeCloseTo(due, TimeSpan.FromSeconds(2),
			"profile re-activation must wait the remaining time, not reset the full interval");
	}

	[Fact]
	public void SetTriggerEnabled_ReactivatedIntervalSchedulerWithOverdueHistory_FiresImmediately()
	{
		var mgr = CreateTriggerManager();
		var orchestration = new Orchestration { Name = "overdue", Description = "overdue", Steps = [] };
		var path = Path.Combine(_tempDir, "overdue.json");

		var reg = mgr.RegisterTrigger(
			orchestrationPath: path,
			config: new SchedulerTriggerConfig
			{
				Type = TriggerType.Scheduler,
				Enabled = true,
				IntervalSeconds = 60, // 1 minute interval
			},
			orchestrationId: "overdue-orch",
			preloadedOrchestration: orchestration);

		reg.LastFireTime = DateTime.UtcNow - TimeSpan.FromHours(2); // 120 missed intervals

		mgr.SetTriggerEnabled("overdue-orch", false);
		mgr.SetTriggerEnabled("overdue-orch", true);

		// Exactly-once catch-up: NextFireTime is "now", not "now + 120 intervals" and not
		// many queued executions. The scheduler tick will fire it on the next poll.
		reg.NextFireTime.Should().NotBeNull();
		reg.NextFireTime!.Value.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(2));
	}

	// ─── SeedTriggerHistoryFromRunStoreAsync ───────────────────────────────────

	[Fact]
	public async Task SeedTriggerHistoryFromRunStore_PopulatesRunCountAndLastFireFromPersistedStore()
	{
		var mgr = CreateTriggerManager();
		var orchestration = new Orchestration { Name = "seeded", Description = "seeded", Steps = [] };
		var path = Path.Combine(_tempDir, "seeded.json");

		var reg = mgr.RegisterTrigger(
			orchestrationPath: path,
			config: new SchedulerTriggerConfig
			{
				Type = TriggerType.Scheduler,
				Enabled = true,
				IntervalSeconds = 60 * 60 * 10,
			},
			orchestrationId: "seeded-orch",
			preloadedOrchestration: orchestration);

		// Cold start: in-memory counters are zero/null even though the persisted store
		// has three prior runs.
		reg.RunCount.Should().Be(0);
		reg.LastFireTime.Should().BeNull();

		var t0 = DateTime.UtcNow - TimeSpan.FromHours(11);
		var latest = t0 + TimeSpan.FromHours(2); // 9h ago
		await Store.SaveRunAsync(MakeRunRecord("seeded", t0));
		await Store.SaveRunAsync(MakeRunRecord("seeded", t0.AddHours(1)));
		await Store.SaveRunAsync(MakeRunRecord("seeded", latest));

		await mgr.SeedTriggerHistoryFromRunStoreAsync(CancellationToken.None);

		reg.RunCount.Should().Be(3, "RunCount should reflect the persisted store");
		reg.LastFireTime.Should().NotBeNull();
		reg.LastFireTime!.Value.Should().BeCloseTo(latest, TimeSpan.FromSeconds(2));

		var due = latest + TimeSpan.FromHours(10);
		reg.NextFireTime.Should().BeCloseTo(due, TimeSpan.FromSeconds(2),
			"interval NextFireTime should be recomputed as lastFire + interval");
	}

	[Fact]
	public async Task SeedTriggerHistoryFromRunStore_OverdueInterval_FiresExactlyOnce()
	{
		var mgr = CreateTriggerManager();
		var orchestration = new Orchestration { Name = "stale", Description = "stale", Steps = [] };
		var path = Path.Combine(_tempDir, "stale.json");

		var reg = mgr.RegisterTrigger(
			orchestrationPath: path,
			config: new SchedulerTriggerConfig
			{
				Type = TriggerType.Scheduler,
				Enabled = true,
				IntervalSeconds = 60, // 1 minute interval
			},
			orchestrationId: "stale-orch",
			preloadedOrchestration: orchestration);

		// 5 hours ago: 300 intervals missed.
		await Store.SaveRunAsync(MakeRunRecord("stale", DateTime.UtcNow - TimeSpan.FromHours(5)));

		await mgr.SeedTriggerHistoryFromRunStoreAsync(CancellationToken.None);

		reg.NextFireTime.Should().NotBeNull();
		reg.NextFireTime!.Value.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(2),
			"overdue interval triggers fire exactly once (no rapid catch-up loop)");
	}

	[Fact]
	public async Task SeedTriggerHistoryFromRunStore_CronTrigger_IgnoresHistoryAndPicksNextOccurrence()
	{
		var mgr = CreateTriggerManager();
		var orchestration = new Orchestration { Name = "cron", Description = "cron", Steps = [] };
		var path = Path.Combine(_tempDir, "cron.json");

		// "Every minute" — a deterministic schedule we can reason about.
		var reg = mgr.RegisterTrigger(
			orchestrationPath: path,
			config: new SchedulerTriggerConfig
			{
				Type = TriggerType.Scheduler,
				Enabled = true,
				Cron = "* * * * *",
			},
			orchestrationId: "cron-orch",
			preloadedOrchestration: orchestration);

		await Store.SaveRunAsync(MakeRunRecord("cron", DateTime.UtcNow - TimeSpan.FromHours(5)));

		await mgr.SeedTriggerHistoryFromRunStoreAsync(CancellationToken.None);

		// Cron is wall-clock-bound: the next occurrence of "* * * * *" is at most ~60s
		// out, regardless of history.
		reg.NextFireTime.Should().NotBeNull();
		var delta = reg.NextFireTime!.Value - DateTime.UtcNow;
		delta.Should().BeLessThan(TimeSpan.FromMinutes(2));
	}

	[Fact]
	public async Task SeedTriggerHistoryFromRunStore_NeverMovesCountersBackwards()
	{
		var mgr = CreateTriggerManager();
		var orchestration = new Orchestration { Name = "ahead", Description = "ahead", Steps = [] };
		var path = Path.Combine(_tempDir, "ahead.json");

		var reg = mgr.RegisterTrigger(
			orchestrationPath: path,
			config: new SchedulerTriggerConfig
			{
				Type = TriggerType.Scheduler,
				Enabled = true,
				IntervalSeconds = 3600,
			},
			orchestrationId: "ahead-orch",
			preloadedOrchestration: orchestration);

		// In-memory state is ahead of what's persisted (e.g. a fire happened but the
		// run record write is still in flight): RunCount = 5, last fire 1 minute ago.
		var inMemoryLastFire = DateTime.UtcNow - TimeSpan.FromMinutes(1);
		reg.RunCount = 5;
		reg.LastFireTime = inMemoryLastFire;

		// Store has only 1 older run.
		await Store.SaveRunAsync(MakeRunRecord("ahead", DateTime.UtcNow - TimeSpan.FromHours(2)));

		await mgr.SeedTriggerHistoryFromRunStoreAsync(CancellationToken.None);

		reg.RunCount.Should().Be(5, "seeding must not move RunCount backwards");
		reg.LastFireTime.Should().BeCloseTo(inMemoryLastFire, TimeSpan.FromSeconds(1),
			"seeding must not move LastFireTime backwards");
	}

	// ─── Auto-resume loops ────────────────────────────────────────────────────

	[Fact]
	public void LoopTriggerConfig_AutoResume_DefaultsToFalse()
	{
		// Sanity: keeping the historical default ensures existing orchestrations
		// don't suddenly start auto-resuming after this change.
		var loop = new LoopTriggerConfig
		{
			Type = TriggerType.Loop,
			Enabled = true,
			DelaySeconds = 0,
		};
		loop.AutoResume.Should().BeFalse();
	}

	[Fact]
	public async Task SeedTriggerHistoryFromRunStore_AutoResumeLoopDoesNotFireWhenDisabled()
	{
		var mgr = CreateTriggerManager();
		var orchestration = new Orchestration { Name = "no-resume", Description = "no-resume", Steps = [] };
		var path = Path.Combine(_tempDir, "no-resume.json");

		mgr.RegisterTrigger(
			orchestrationPath: path,
			config: new LoopTriggerConfig
			{
				Type = TriggerType.Loop,
				Enabled = true,
				DelaySeconds = 0,
				AutoResume = false, // default
			},
			orchestrationId: "no-resume-orch",
			preloadedOrchestration: orchestration);

		await Store.SaveRunAsync(MakeRunRecord("no-resume", DateTime.UtcNow - TimeSpan.FromHours(2)));

		// Without AutoResume, the loop stays paused on the next tick — seeding only
		// updates counters, it does not enqueue any background work.
		await mgr.SeedTriggerHistoryFromRunStoreAsync(CancellationToken.None);

		// No background execution should have been queued. We can't easily observe
		// "no background task" without internals, but we can assert the trigger is
		// still in a non-running state with no active execution attached.
		var reg = mgr.GetTrigger("no-resume-orch");
		reg.Should().NotBeNull();
		reg!.ActiveExecutionId.Should().BeNull();
	}
}
