using System.Collections.Concurrent;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Orchestra.Host.Api;
using Orchestra.Host.Profiles;
using Orchestra.Host.Registry;
using Orchestra.Host.Triggers;
using Xunit;

namespace Orchestra.Host.Tests;

/// <summary>
/// Tests for the <c>EnableScheduler</c> host toggle wired onto <see cref="TriggerManager"/>
/// and <see cref="ProfileManager"/> via their <c>SchedulingEnabled</c> property. When
/// disabled (API-only server / isolated <c>orchestra-exec</c> runner), the background loops
/// must not run and persisted triggers must not be loaded, so nothing auto-fires.
///
/// We use <see cref="Microsoft.Extensions.Hosting.BackgroundService.ExecuteTask"/> as the
/// signal: the disabled path returns synchronously (no await before the early return), so the
/// execute task is already complete; the enabled path enters an awaiting loop, so it is not.
/// </summary>
public class SchedulerToggleTests : IDisposable
{
	private readonly string _tempDir;
	private readonly string _dataPath;
	private readonly string _runsDir;

	public SchedulerToggleTests()
	{
		_tempDir = Path.Combine(Path.GetTempPath(), $"orchestra-scheduler-toggle-{Guid.NewGuid():N}");
		_dataPath = Path.Combine(_tempDir, "data");
		_runsDir = Path.Combine(_dataPath, "runs");
		Directory.CreateDirectory(_runsDir);
		Directory.CreateDirectory(Path.Combine(_dataPath, "triggers"));
	}

	public void Dispose()
	{
		if (Directory.Exists(_tempDir))
		{
			try { Directory.Delete(_tempDir, recursive: true); }
			catch { /* best-effort cleanup */ }
		}
	}

	private TriggerManager CreateTriggerManager(bool schedulingEnabled) =>
		new TriggerManager(
			new ConcurrentDictionary<string, CancellationTokenSource>(),
			new ConcurrentDictionary<string, ActiveExecutionInfo>(),
			agentBuilder: null!,
			scheduler: null!,
			loggerFactory: NullLoggerFactory.Instance,
			logger: NullLogger<TriggerManager>.Instance,
			runsDir: _runsDir,
			runStore: null!,
			checkpointStore: null!,
			launcher: null!,
			dataPath: _dataPath)
		{
			SchedulingEnabled = schedulingEnabled,
		};

	private ProfileManager CreateProfileManager(bool schedulingEnabled)
	{
		var profileStore = new ProfileStore(_dataPath, NullLogger<ProfileStore>.Instance);
		var tagStore = new OrchestrationTagStore(_dataPath, NullLogger<OrchestrationTagStore>.Instance);
		var registry = new OrchestrationRegistry(
			Path.Combine(_dataPath, "registered-orchestrations.json"),
			NullLogger<OrchestrationRegistry>.Instance);
		var pm = new ProfileManager(profileStore, tagStore, registry, NullLogger<ProfileManager>.Instance)
		{
			SchedulingEnabled = schedulingEnabled,
		};
		pm.Initialize();
		return pm;
	}

	[Fact]
	public async Task TriggerManager_SchedulingDisabled_DoesNotRunLoop()
	{
		var tm = CreateTriggerManager(schedulingEnabled: false);
		tm.SchedulingEnabled.Should().BeFalse();

		await tm.StartAsync(CancellationToken.None);
		try
		{
			// Disabled path returns from ExecuteAsync without entering the loop, so the execute
			// task completes; no triggers are loaded, so nothing can auto-fire.
			(await BecomesCompletedAsync(tm)).Should().BeTrue();
			tm.GetAllTriggers().Should().BeEmpty();
		}
		finally
		{
			await tm.StopAsync(CancellationToken.None);
		}
	}

	[Fact]
	public async Task TriggerManager_SchedulingEnabled_RunsLoop()
	{
		var tm = CreateTriggerManager(schedulingEnabled: true);

		await tm.StartAsync(CancellationToken.None);
		try
		{
			// Enabled path enters the 1s evaluation loop (awaits), so the execute task keeps
			// running rather than completing.
			(await StaysRunningAsync(tm)).Should().BeTrue();
		}
		finally
		{
			await tm.StopAsync(CancellationToken.None);
		}
	}

	[Fact]
	public async Task ProfileManager_SchedulingDisabled_DoesNotRunEvaluationLoop()
	{
		var pm = CreateProfileManager(schedulingEnabled: false);
		pm.SchedulingEnabled.Should().BeFalse();

		await pm.StartAsync(CancellationToken.None);
		try
		{
			(await BecomesCompletedAsync(pm)).Should()
				.BeTrue("the schedule-evaluation loop must not run when scheduling is disabled");
		}
		finally
		{
			await pm.StopAsync(CancellationToken.None);
		}
	}

	[Fact]
	public async Task ProfileManager_SchedulingEnabled_RunsEvaluationLoop()
	{
		var pm = CreateProfileManager(schedulingEnabled: true);

		await pm.StartAsync(CancellationToken.None);
		try
		{
			(await StaysRunningAsync(pm)).Should()
				.BeTrue("the schedule-evaluation loop should run when scheduling is enabled");
		}
		finally
		{
			await pm.StopAsync(CancellationToken.None);
		}
	}

	/// <summary>Polls until the hosted service's execute task completes, up to ~2s.</summary>
	private static async Task<bool> BecomesCompletedAsync(Microsoft.Extensions.Hosting.BackgroundService service)
	{
		for (var i = 0; i < 40; i++)
		{
			if (service.ExecuteTask?.IsCompleted == true)
			{
				return true;
			}
			await Task.Delay(50);
		}
		return service.ExecuteTask?.IsCompleted == true;
	}

	/// <summary>Confirms the execute task is still running after a short settle window.</summary>
	private static async Task<bool> StaysRunningAsync(Microsoft.Extensions.Hosting.BackgroundService service)
	{
		await Task.Delay(300);
		return service.ExecuteTask is { IsCompleted: false };
	}
}
