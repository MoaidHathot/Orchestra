using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orchestra.Host.Persistence;

namespace Orchestra.Host.Hosting;

/// <summary>
/// Background hosted service that periodically applies the retention policy
/// to clean up old orchestration run records.
/// </summary>
public partial class RunRetentionService : BackgroundService
{
	private readonly FileSystemRunStore _runStore;
	private readonly RetentionPolicy _retentionPolicy;
	private readonly ILogger<RunRetentionService> _logger;
	private readonly TimeSpan _interval;

	/// <summary>
	/// Default interval between retention sweeps (1 hour).
	/// </summary>
	internal static readonly TimeSpan DefaultInterval = TimeSpan.FromHours(1);

	public RunRetentionService(
		FileSystemRunStore runStore,
		RetentionPolicy retentionPolicy,
		ILogger<RunRetentionService> logger)
		: this(runStore, retentionPolicy, logger, DefaultInterval)
	{
	}

	internal RunRetentionService(
		FileSystemRunStore runStore,
		RetentionPolicy retentionPolicy,
		ILogger<RunRetentionService> logger,
		TimeSpan interval)
	{
		_runStore = runStore;
		_retentionPolicy = retentionPolicy;
		_logger = logger;
		_interval = interval;
	}

	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		// When no retention limits are configured, exit immediately instead of
		// running periodic no-op sweeps.  This allows unconditional DI registration
		// (needed because options are resolved lazily) while avoiding wasted work.
		if (_retentionPolicy.IsForever)
			return;

		LogRetentionServiceStarted(_retentionPolicy.MaxRunsPerOrchestration, _retentionPolicy.MaxRunAgeDays);

		// Run an initial sweep shortly after startup (5 seconds delay to let things settle)
		try
		{
			await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
		}
		catch (OperationCanceledException)
		{
			return;
		}

		while (!stoppingToken.IsCancellationRequested)
		{
			try
			{
				var deleted = await _runStore.ApplyRetentionAsync(_retentionPolicy, stoppingToken);
				if (deleted > 0)
				{
					LogRetentionSweepCompleted(deleted);
				}
			}
			catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
			{
				break;
			}
			catch (Exception ex)
			{
				LogRetentionSweepFailed(ex);
			}

			try
			{
				await Task.Delay(_interval, stoppingToken);
			}
			catch (OperationCanceledException)
			{
				break;
			}
		}

		LogRetentionServiceStopped();
	}

	[LoggerMessage(Level = LogLevel.Information, Message = "Run retention service started (maxRuns={MaxRuns}, maxAgeDays={MaxAgeDays})")]
	private partial void LogRetentionServiceStarted(int? maxRuns, int? maxAgeDays);

	[LoggerMessage(Level = LogLevel.Information, Message = "Retention sweep completed: deleted {DeletedCount} run(s)")]
	private partial void LogRetentionSweepCompleted(int deletedCount);

	[LoggerMessage(Level = LogLevel.Error, Message = "Retention sweep failed")]
	private partial void LogRetentionSweepFailed(Exception ex);

	[LoggerMessage(Level = LogLevel.Information, Message = "Run retention service stopped")]
	private partial void LogRetentionServiceStopped();
}
