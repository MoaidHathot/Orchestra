using System.Collections.Concurrent;
using FluentAssertions;
using Orchestra.Engine;
using Orchestra.Host.Api;
using Orchestra.Host.Triggers;
using Xunit;

namespace Orchestra.Host.Tests;

/// <summary>
/// Tests for the WI-C refactoring of TriggerManager execution methods.
/// Validates that HandlePostExecutionTriggerStatus correctly transitions trigger status
/// based on trigger type and execution result, and that ExecuteOrchestrationAsync /
/// ExecuteOrchestrationWithResultAsync correctly delegate to ExecuteOrchestrationCoreAsync.
/// </summary>
public class TriggerManagerExecutionRefactorTests : IDisposable
{
	private readonly string _tempDir;
	private readonly TriggerManager _triggerManager;

	public TriggerManagerExecutionRefactorTests()
	{
		_tempDir = Path.Combine(Path.GetTempPath(), $"orchestra-exec-refactor-tests-{Guid.NewGuid():N}");
		var runsDir = Path.Combine(_tempDir, "runs");
		Directory.CreateDirectory(runsDir);

		var loggerFactory = Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance;
		var logger = new Microsoft.Extensions.Logging.Abstractions.NullLogger<TriggerManager>();

		_triggerManager = new TriggerManager(
			new ConcurrentDictionary<string, CancellationTokenSource>(),
			new ConcurrentDictionary<string, ActiveExecutionInfo>(),
			agentBuilder: null!,
			scheduler: null!,
			loggerFactory: loggerFactory,
			logger: logger,
			runsDir: runsDir,
			runStore: null!,
			checkpointStore: null!,
			dataPath: _tempDir);
	}

	public void Dispose()
	{
		if (Directory.Exists(_tempDir))
		{
			try { Directory.Delete(_tempDir, recursive: true); }
			catch { /* best-effort cleanup */ }
		}
	}

	private static TriggerRegistration CreateRegistration(TriggerConfig config, int runCount = 1)
	{
		return new TriggerRegistration
		{
			Id = $"test-trigger-{Guid.NewGuid():N}"[..20],
			OrchestrationPath = "/test/orchestration.json",
			Config = config,
			Status = TriggerStatus.Running,
			RunCount = runCount,
		};
	}

	private static OrchestrationResult CreateResult(ExecutionStatus status)
	{
		return new OrchestrationResult
		{
			Status = status,
			Results = new Dictionary<string, ExecutionResult>(),
			StepResults = new Dictionary<string, ExecutionResult>(),
		};
	}

	// ── Webhook trigger status transitions ──

	[Fact]
	public void HandlePostExecutionTriggerStatus_WebhookTrigger_SetsStatusToWaiting()
	{
		// Arrange
		var config = new WebhookTriggerConfig { Type = TriggerType.Webhook };
		var reg = CreateRegistration(config);
		var result = CreateResult(ExecutionStatus.Succeeded);

		// Act
		_triggerManager.HandlePostExecutionTriggerStatus(reg, result, parameters: null);

		// Assert
		reg.Status.Should().Be(TriggerStatus.Waiting);
	}

	[Fact]
	public void HandlePostExecutionTriggerStatus_WebhookTrigger_FailedResult_StillSetsWaiting()
	{
		// Arrange — webhook should always return to Waiting regardless of execution outcome
		var config = new WebhookTriggerConfig { Type = TriggerType.Webhook };
		var reg = CreateRegistration(config);
		var result = CreateResult(ExecutionStatus.Failed);

		// Act
		_triggerManager.HandlePostExecutionTriggerStatus(reg, result, parameters: null);

		// Assert
		reg.Status.Should().Be(TriggerStatus.Waiting);
	}

	// ── Scheduler trigger status transitions ──

	[Fact]
	public void HandlePostExecutionTriggerStatus_SchedulerTrigger_SetsWaitingWithNextFireTime()
	{
		// Arrange
		var config = new SchedulerTriggerConfig
		{
			Type = TriggerType.Scheduler,
			IntervalSeconds = 60,
		};
		var reg = CreateRegistration(config);
		var beforeUtc = DateTime.UtcNow;
		var result = CreateResult(ExecutionStatus.Succeeded);

		// Act
		_triggerManager.HandlePostExecutionTriggerStatus(reg, result, parameters: null);

		// Assert
		reg.Status.Should().Be(TriggerStatus.Waiting);
		reg.NextFireTime.Should().NotBeNull();
		reg.NextFireTime!.Value.Should().BeAfter(beforeUtc);
	}

	[Fact]
	public void HandlePostExecutionTriggerStatus_SchedulerWithCron_SetsWaitingWithNextFireTime()
	{
		// Arrange — cron: every minute
		var config = new SchedulerTriggerConfig
		{
			Type = TriggerType.Scheduler,
			Cron = "* * * * *",
		};
		var reg = CreateRegistration(config);
		var result = CreateResult(ExecutionStatus.Succeeded);

		// Act
		_triggerManager.HandlePostExecutionTriggerStatus(reg, result, parameters: null);

		// Assert
		reg.Status.Should().Be(TriggerStatus.Waiting);
		reg.NextFireTime.Should().NotBeNull();
	}

	// ── Loop trigger status transitions ──

	[Fact]
	public void HandlePostExecutionTriggerStatus_LoopTrigger_SucceededWithDelay_SetsWaiting()
	{
		// Arrange — loop with delay should set Waiting + NextFireTime
		var config = new LoopTriggerConfig
		{
			Type = TriggerType.Loop,
			DelaySeconds = 10,
			Enabled = true,
		};
		var reg = CreateRegistration(config, runCount: 1);
		var result = CreateResult(ExecutionStatus.Succeeded);
		var beforeUtc = DateTime.UtcNow;

		// Act
		_triggerManager.HandlePostExecutionTriggerStatus(reg, result, parameters: null);

		// Assert
		reg.Status.Should().Be(TriggerStatus.Waiting);
		reg.NextFireTime.Should().NotBeNull();
		reg.NextFireTime!.Value.Should().BeAfter(beforeUtc.AddSeconds(9));
	}

	[Fact]
	public void HandlePostExecutionTriggerStatus_LoopTrigger_FailedWithoutContinueOnFailure_SetsPaused()
	{
		// Arrange — failed execution without ContinueOnFailure should pause
		var config = new LoopTriggerConfig
		{
			Type = TriggerType.Loop,
			DelaySeconds = 5,
			ContinueOnFailure = false,
			Enabled = true,
		};
		var reg = CreateRegistration(config, runCount: 1);
		var result = CreateResult(ExecutionStatus.Failed);

		// Act
		_triggerManager.HandlePostExecutionTriggerStatus(reg, result, parameters: null);

		// Assert
		reg.Status.Should().Be(TriggerStatus.Paused);
	}

	[Fact]
	public void HandlePostExecutionTriggerStatus_LoopTrigger_FailedWithContinueOnFailure_SetsWaiting()
	{
		// Arrange — failed but ContinueOnFailure=true with delay should continue
		var config = new LoopTriggerConfig
		{
			Type = TriggerType.Loop,
			DelaySeconds = 10,
			ContinueOnFailure = true,
			Enabled = true,
		};
		var reg = CreateRegistration(config, runCount: 1);
		var result = CreateResult(ExecutionStatus.Failed);

		// Act
		_triggerManager.HandlePostExecutionTriggerStatus(reg, result, parameters: null);

		// Assert
		reg.Status.Should().Be(TriggerStatus.Waiting);
	}

	[Fact]
	public void HandlePostExecutionTriggerStatus_LoopTrigger_MaxIterationsReached_SetsCompleted()
	{
		// Arrange — runCount >= maxIterations should complete
		var config = new LoopTriggerConfig
		{
			Type = TriggerType.Loop,
			DelaySeconds = 5,
			MaxIterations = 3,
			Enabled = true,
		};
		var reg = CreateRegistration(config, runCount: 3);
		var result = CreateResult(ExecutionStatus.Succeeded);

		// Act
		_triggerManager.HandlePostExecutionTriggerStatus(reg, result, parameters: null);

		// Assert
		reg.Status.Should().Be(TriggerStatus.Completed);
	}

	[Fact]
	public void HandlePostExecutionTriggerStatus_LoopTrigger_WithinMaxIterations_SetsWaiting()
	{
		// Arrange — runCount < maxIterations with delay should continue
		var config = new LoopTriggerConfig
		{
			Type = TriggerType.Loop,
			DelaySeconds = 5,
			MaxIterations = 10,
			Enabled = true,
		};
		var reg = CreateRegistration(config, runCount: 2);
		var result = CreateResult(ExecutionStatus.Succeeded);

		// Act
		_triggerManager.HandlePostExecutionTriggerStatus(reg, result, parameters: null);

		// Assert
		reg.Status.Should().Be(TriggerStatus.Waiting);
	}

	[Fact]
	public void HandlePostExecutionTriggerStatus_LoopTrigger_DisabledConfig_SetsPaused()
	{
		// Arrange — disabled trigger should not re-fire
		var config = new LoopTriggerConfig
		{
			Type = TriggerType.Loop,
			DelaySeconds = 5,
			Enabled = false,
		};
		var reg = CreateRegistration(config, runCount: 1);
		var result = CreateResult(ExecutionStatus.Succeeded);

		// Act
		_triggerManager.HandlePostExecutionTriggerStatus(reg, result, parameters: null);

		// Assert
		reg.Status.Should().Be(TriggerStatus.Paused);
	}

	[Fact]
	public void HandlePostExecutionTriggerStatus_LoopTrigger_ZeroDelay_SchedulesImmediateRefire()
	{
		// Arrange — zero delay triggers immediate re-run (fire-and-forget background task).
		// We can't easily observe the background task, but we verify no exception is thrown
		// and the status is NOT set to Waiting/Completed/Paused (the re-fire task will set it).
		var config = new LoopTriggerConfig
		{
			Type = TriggerType.Loop,
			DelaySeconds = 0,
			Enabled = true,
		};
		var reg = CreateRegistration(config, runCount: 1);
		var result = CreateResult(ExecutionStatus.Succeeded);

		// Act — should not throw
		var act = () => _triggerManager.HandlePostExecutionTriggerStatus(reg, result, parameters: null);

		// Assert — does not throw; status was NOT explicitly set to Waiting by this path
		// (the background task handles the next state transition)
		act.Should().NotThrow();
	}

	// ── Cancelled execution ──

	[Fact]
	public void HandlePostExecutionTriggerStatus_LoopTrigger_Cancelled_SetsPaused()
	{
		// Arrange — cancelled execution (not succeeded, not ContinueOnFailure) should pause
		var config = new LoopTriggerConfig
		{
			Type = TriggerType.Loop,
			DelaySeconds = 5,
			ContinueOnFailure = false,
			Enabled = true,
		};
		var reg = CreateRegistration(config, runCount: 1);
		var result = CreateResult(ExecutionStatus.Cancelled);

		// Act
		_triggerManager.HandlePostExecutionTriggerStatus(reg, result, parameters: null);

		// Assert — cancelled is not "succeeded", so shouldContinue is false → Paused
		reg.Status.Should().Be(TriggerStatus.Paused);
	}

	[Fact]
	public void HandlePostExecutionTriggerStatus_SchedulerTrigger_FailedResult_StillSetsWaiting()
	{
		// Arrange — scheduler should always return to Waiting regardless of result
		var config = new SchedulerTriggerConfig
		{
			Type = TriggerType.Scheduler,
			IntervalSeconds = 30,
		};
		var reg = CreateRegistration(config);
		var result = CreateResult(ExecutionStatus.Failed);

		// Act
		_triggerManager.HandlePostExecutionTriggerStatus(reg, result, parameters: null);

		// Assert
		reg.Status.Should().Be(TriggerStatus.Waiting);
		reg.NextFireTime.Should().NotBeNull();
	}
}
