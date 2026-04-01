using System.Collections.Concurrent;
using FluentAssertions;
using Orchestra.Engine;
using Orchestra.Host.Api;
using Orchestra.Host.Triggers;
using Xunit;

namespace Orchestra.Host.Tests;

/// <summary>
/// Tests for preserving trigger runtime state on re-registration (Work Item 3).
/// Verifies that RunCount, LastFireTime, ActiveExecutionId, etc. are carried over
/// when an existing trigger is re-registered (e.g., after orchestration file reload).
/// </summary>
public class TriggerRuntimeStatePreservationTests : IDisposable
{
	private readonly string _tempDir;
	private readonly string _runsDir;
	private readonly string _orchestrationsDir;
	private readonly TriggerManager _triggerManager;

	public TriggerRuntimeStatePreservationTests()
	{
		_tempDir = Path.Combine(Path.GetTempPath(), $"orchestra-runtime-state-tests-{Guid.NewGuid():N}");
		_runsDir = Path.Combine(_tempDir, "runs");
		_orchestrationsDir = Path.Combine(_tempDir, "orchestrations");
		Directory.CreateDirectory(_runsDir);
		Directory.CreateDirectory(_orchestrationsDir);

		_triggerManager = CreateTriggerManager();
	}

	public void Dispose()
	{
		if (Directory.Exists(_tempDir))
		{
			try { Directory.Delete(_tempDir, recursive: true); }
			catch { /* best-effort cleanup */ }
		}
	}

	private TriggerManager CreateTriggerManager()
	{
		var loggerFactory = Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance;
		var logger = new Microsoft.Extensions.Logging.Abstractions.NullLogger<TriggerManager>();

		return new TriggerManager(
			new ConcurrentDictionary<string, CancellationTokenSource>(),
			new ConcurrentDictionary<string, ActiveExecutionInfo>(),
			agentBuilder: null!,
			scheduler: null!,
			loggerFactory: loggerFactory,
			logger: logger,
			runsDir: _runsDir,
			runStore: null!,
			checkpointStore: null!,
			dataPath: _tempDir);
	}

	private string CreateTestOrchestrationFile(string name, string? version = null)
	{
		var orchestration = new
		{
			name,
			description = $"Test: {name}",
			version = version ?? "1.0.0",
			model = "claude-opus-4.5",
			steps = new[]
			{
				new
				{
					name = "step1",
					type = "prompt",
					systemPrompt = "Test.",
					userPrompt = "Test prompt",
					model = "claude-opus-4.5"
				}
			}
		};

		var path = Path.Combine(_orchestrationsDir, $"{name}.json");
		var json = System.Text.Json.JsonSerializer.Serialize(orchestration,
			new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
		File.WriteAllText(path, json);
		return path;
	}

	[Fact]
	public void ReRegister_PreservesRunCount()
	{
		// Arrange
		var orchPath = CreateTestOrchestrationFile("preserve-runcount");
		var config = new SchedulerTriggerConfig
		{
			Type = TriggerType.Scheduler,
			Enabled = true,
			IntervalSeconds = 60,
		};
		var reg = _triggerManager.RegisterTrigger(orchPath, null, config, source: TriggerSource.Json);

		// Simulate some runs
		var trigger = _triggerManager.GetTrigger(reg.Id)!;
		trigger.RunCount = 5;
		trigger.LastFireTime = new DateTime(2025, 6, 15, 12, 0, 0, DateTimeKind.Utc);

		// Act — re-register with same ID (simulates orchestration file reload)
		var newConfig = new SchedulerTriggerConfig
		{
			Type = TriggerType.Scheduler,
			Enabled = true,
			IntervalSeconds = 120, // changed interval
		};
		var newReg = _triggerManager.RegisterTrigger(orchPath, null, newConfig, source: TriggerSource.Json);

		// Assert
		newReg.RunCount.Should().Be(5);
		newReg.LastFireTime.Should().Be(new DateTime(2025, 6, 15, 12, 0, 0, DateTimeKind.Utc));
	}

	[Fact]
	public void ReRegister_PreservesLastExecutionId()
	{
		// Arrange
		var orchPath = CreateTestOrchestrationFile("preserve-lastexec");
		var config = new WebhookTriggerConfig { Type = TriggerType.Webhook, Enabled = true };
		var reg = _triggerManager.RegisterTrigger(orchPath, null, config, source: TriggerSource.Json);

		var trigger = _triggerManager.GetTrigger(reg.Id)!;
		trigger.LastExecutionId = "exec-abc123";

		// Act
		var newReg = _triggerManager.RegisterTrigger(orchPath, null, config, source: TriggerSource.Json);

		// Assert
		newReg.LastExecutionId.Should().Be("exec-abc123");
	}

	[Fact]
	public void ReRegister_PreservesActiveExecutionIdAndStatus()
	{
		// Arrange
		var orchPath = CreateTestOrchestrationFile("preserve-active");
		var config = new LoopTriggerConfig
		{
			Type = TriggerType.Loop,
			Enabled = true,
			MaxIterations = 10,
		};
		var reg = _triggerManager.RegisterTrigger(orchPath, null, config, source: TriggerSource.Json);

		var trigger = _triggerManager.GetTrigger(reg.Id)!;
		trigger.ActiveExecutionId = "exec-running-456";
		trigger.Status = TriggerStatus.Running;
		trigger.RunCount = 3;

		// Act — re-register while execution is active
		var newConfig = new LoopTriggerConfig
		{
			Type = TriggerType.Loop,
			Enabled = true,
			MaxIterations = 20,
		};
		var newReg = _triggerManager.RegisterTrigger(orchPath, null, newConfig, source: TriggerSource.Json);

		// Assert — active execution state should be preserved
		newReg.ActiveExecutionId.Should().Be("exec-running-456");
		newReg.Status.Should().Be(TriggerStatus.Running);
		newReg.RunCount.Should().Be(3);
	}

	[Fact]
	public void ReRegister_PreservesLastError()
	{
		// Arrange
		var orchPath = CreateTestOrchestrationFile("preserve-error");
		var config = new SchedulerTriggerConfig
		{
			Type = TriggerType.Scheduler,
			Enabled = true,
			IntervalSeconds = 60,
		};
		var reg = _triggerManager.RegisterTrigger(orchPath, null, config, source: TriggerSource.Json);

		var trigger = _triggerManager.GetTrigger(reg.Id)!;
		trigger.LastError = "Previous execution failed: timeout";

		// Act
		var newReg = _triggerManager.RegisterTrigger(orchPath, null, config, source: TriggerSource.Json);

		// Assert
		newReg.LastError.Should().Be("Previous execution failed: timeout");
	}

	[Fact]
	public void ReRegister_WithNoActiveExecution_DoesNotOverrideStatus()
	{
		// Arrange — when no active execution, the new config determines the status
		var orchPath = CreateTestOrchestrationFile("no-active-status");
		var config = new SchedulerTriggerConfig
		{
			Type = TriggerType.Scheduler,
			Enabled = true,
			IntervalSeconds = 60,
		};
		var reg = _triggerManager.RegisterTrigger(orchPath, null, config, source: TriggerSource.Json);

		var trigger = _triggerManager.GetTrigger(reg.Id)!;
		trigger.RunCount = 2;
		trigger.LastFireTime = DateTime.UtcNow;
		// No active execution (ActiveExecutionId is null)

		// Act — re-register with disabled config
		var disabledConfig = new SchedulerTriggerConfig
		{
			Type = TriggerType.Scheduler,
			Enabled = false,
			IntervalSeconds = 60,
		};
		var newReg = _triggerManager.RegisterTrigger(orchPath, null, disabledConfig, source: TriggerSource.Json);

		// Assert — status comes from new config (Paused), runtime state preserved
		newReg.Status.Should().Be(TriggerStatus.Paused);
		newReg.RunCount.Should().Be(2);
	}

	[Fact]
	public void FirstRegister_HasZeroRuntimeState()
	{
		// Arrange & Act
		var orchPath = CreateTestOrchestrationFile("first-register");
		var config = new SchedulerTriggerConfig
		{
			Type = TriggerType.Scheduler,
			Enabled = true,
			IntervalSeconds = 60,
		};
		var reg = _triggerManager.RegisterTrigger(orchPath, null, config, source: TriggerSource.Json);

		// Assert — first registration should have clean runtime state
		reg.RunCount.Should().Be(0);
		reg.LastFireTime.Should().BeNull();
		reg.ActiveExecutionId.Should().BeNull();
		reg.LastExecutionId.Should().BeNull();
		reg.LastError.Should().BeNull();
	}

	[Fact]
	public void ReRegister_UpdatesConfigWhilePreservingState()
	{
		// Arrange
		var orchPath = CreateTestOrchestrationFile("update-config");
		var oldConfig = new SchedulerTriggerConfig
		{
			Type = TriggerType.Scheduler,
			Enabled = true,
			IntervalSeconds = 30,
			MaxRuns = 100,
		};
		var reg = _triggerManager.RegisterTrigger(orchPath, null, oldConfig, source: TriggerSource.Json);

		var trigger = _triggerManager.GetTrigger(reg.Id)!;
		trigger.RunCount = 10;

		// Act — re-register with different config
		var newConfig = new SchedulerTriggerConfig
		{
			Type = TriggerType.Scheduler,
			Enabled = true,
			IntervalSeconds = 120,
			MaxRuns = 200,
		};
		var newReg = _triggerManager.RegisterTrigger(orchPath, null, newConfig, source: TriggerSource.Json);

		// Assert — config is updated but runtime state preserved
		newReg.Config.Should().BeOfType<SchedulerTriggerConfig>();
		var schedConfig = (SchedulerTriggerConfig)newReg.Config;
		schedConfig.IntervalSeconds.Should().Be(120);
		schedConfig.MaxRuns.Should().Be(200);
		newReg.RunCount.Should().Be(10);
	}

	[Fact]
	public void ReRegister_WithExplicitOrchestrationId_PreservesState()
	{
		// Arrange — use explicit orchestration ID (as done in InitializeOrchestraHost)
		var orchPath = CreateTestOrchestrationFile("explicit-id");
		var config = new WebhookTriggerConfig { Type = TriggerType.Webhook, Enabled = true };
		var explicitId = "my-custom-trigger-id";

		var reg = _triggerManager.RegisterTrigger(orchPath, null, config,
			source: TriggerSource.Json, orchestrationId: explicitId);

		var trigger = _triggerManager.GetTrigger(explicitId)!;
		trigger.RunCount = 7;
		trigger.LastFireTime = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);

		// Act
		var newReg = _triggerManager.RegisterTrigger(orchPath, null, config,
			source: TriggerSource.Json, orchestrationId: explicitId);

		// Assert
		newReg.Id.Should().Be(explicitId);
		newReg.RunCount.Should().Be(7);
		newReg.LastFireTime.Should().Be(new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc));
	}
}
