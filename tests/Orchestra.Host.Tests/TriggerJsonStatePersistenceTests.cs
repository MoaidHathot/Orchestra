using System.Collections.Concurrent;
using FluentAssertions;
using Orchestra.Engine;
using Orchestra.Host.Api;
using Orchestra.Host.Triggers;
using Xunit;

namespace Orchestra.Host.Tests;

/// <summary>
/// Tests for JSON trigger enabled-state persistence (Work Item 2).
/// Verifies that disabling/enabling a JSON-sourced trigger is persisted
/// via sidecar override files and survives re-registration.
/// </summary>
public class TriggerJsonStatePersistenceTests : IDisposable
{
	private readonly string _tempDir;
	private readonly string _runsDir;
	private readonly string _orchestrationsDir;
	private readonly TriggerManager _triggerManager;

	public TriggerJsonStatePersistenceTests()
	{
		_tempDir = Path.Combine(Path.GetTempPath(), $"orchestra-trigger-state-tests-{Guid.NewGuid():N}");
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

	private string CreateTestOrchestrationFile(string name)
	{
		var orchestration = new
		{
			name,
			description = $"Test: {name}",
			version = "1.0.0",
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
	public void SetTriggerEnabled_JsonTrigger_Disabled_PersistsOverride()
	{
		// Arrange
		var orchPath = CreateTestOrchestrationFile("json-disable-test");
		var config = new SchedulerTriggerConfig
		{
			Type = TriggerType.Scheduler,
			Enabled = true,
			IntervalSeconds = 60,
		};
		var reg = _triggerManager.RegisterTrigger(orchPath, null, config, source: TriggerSource.Json);

		// Act
		var result = _triggerManager.SetTriggerEnabled(reg.Id, false);

		// Assert
		result.Should().BeTrue();
		_triggerManager.GetJsonTriggerEnabledOverride(reg.Id).Should().BeFalse();
	}

	[Fact]
	public void SetTriggerEnabled_JsonTrigger_ReEnabled_PersistsOverride()
	{
		// Arrange
		var orchPath = CreateTestOrchestrationFile("json-reenable-test");
		var config = new WebhookTriggerConfig
		{
			Type = TriggerType.Webhook,
			Enabled = true,
		};
		var reg = _triggerManager.RegisterTrigger(orchPath, null, config, source: TriggerSource.Json);

		// Disable then re-enable
		_triggerManager.SetTriggerEnabled(reg.Id, false);
		_triggerManager.SetTriggerEnabled(reg.Id, true);

		// Assert
		_triggerManager.GetJsonTriggerEnabledOverride(reg.Id).Should().BeTrue();
	}

	[Fact]
	public void SetTriggerEnabled_UserTrigger_DoesNotCreateJsonOverride()
	{
		// Arrange
		var orchPath = CreateTestOrchestrationFile("user-trigger-test");
		var config = new SchedulerTriggerConfig
		{
			Type = TriggerType.Scheduler,
			Enabled = true,
			IntervalSeconds = 30,
		};
		var reg = _triggerManager.RegisterTrigger(orchPath, null, config, source: TriggerSource.User);

		// Act
		_triggerManager.SetTriggerEnabled(reg.Id, false);

		// Assert — should not have a JSON trigger override (user triggers use their own sidecar)
		_triggerManager.GetJsonTriggerEnabledOverride(reg.Id).Should().BeNull();
	}

	[Fact]
	public void GetJsonTriggerEnabledOverride_NoOverride_ReturnsNull()
	{
		// Act
		var result = _triggerManager.GetJsonTriggerEnabledOverride("nonexistent-trigger");

		// Assert
		result.Should().BeNull();
	}

	[Fact]
	public void RemoveTrigger_JsonTrigger_CleansUpOverrideFile()
	{
		// Arrange
		var orchPath = CreateTestOrchestrationFile("json-remove-cleanup");
		var config = new WebhookTriggerConfig
		{
			Type = TriggerType.Webhook,
			Enabled = true,
		};
		var reg = _triggerManager.RegisterTrigger(orchPath, null, config, source: TriggerSource.Json);

		// Disable it (creates override file)
		_triggerManager.SetTriggerEnabled(reg.Id, false);
		_triggerManager.GetJsonTriggerEnabledOverride(reg.Id).Should().BeFalse();

		// Act — remove the trigger
		var removed = _triggerManager.RemoveTrigger(reg.Id);

		// Assert
		removed.Should().BeTrue();
		_triggerManager.GetJsonTriggerEnabledOverride(reg.Id).Should().BeNull();
	}

	[Fact]
	public void SetTriggerEnabled_JsonTrigger_OverrideSurvivesNewInstance()
	{
		// Arrange — create a trigger and disable it
		var orchPath = CreateTestOrchestrationFile("json-persist-across-instances");
		var config = new LoopTriggerConfig
		{
			Type = TriggerType.Loop,
			Enabled = true,
			MaxIterations = 5,
		};
		var reg = _triggerManager.RegisterTrigger(orchPath, null, config, source: TriggerSource.Json);
		_triggerManager.SetTriggerEnabled(reg.Id, false);

		// Act — create a new TriggerManager instance (simulates restart)
		var newManager = CreateTriggerManager();

		// Assert — override should be readable from the new instance
		newManager.GetJsonTriggerEnabledOverride(reg.Id).Should().BeFalse();
	}

	[Fact]
	public void SetTriggerEnabled_JsonTrigger_DisabledUpdatesStatusToPaused()
	{
		// Arrange
		var orchPath = CreateTestOrchestrationFile("json-status-paused");
		var config = new SchedulerTriggerConfig
		{
			Type = TriggerType.Scheduler,
			Enabled = true,
			IntervalSeconds = 60,
		};
		var reg = _triggerManager.RegisterTrigger(orchPath, null, config, source: TriggerSource.Json);
		reg.Status.Should().Be(TriggerStatus.Waiting);

		// Act
		_triggerManager.SetTriggerEnabled(reg.Id, false);

		// Assert
		var trigger = _triggerManager.GetTrigger(reg.Id);
		trigger.Should().NotBeNull();
		trigger!.Status.Should().Be(TriggerStatus.Paused);
		trigger.Config.Enabled.Should().BeFalse();
	}

	[Fact]
	public void SetTriggerEnabled_JsonTrigger_EnabledUpdatesStatusToWaiting()
	{
		// Arrange
		var orchPath = CreateTestOrchestrationFile("json-status-waiting");
		var config = new WebhookTriggerConfig
		{
			Type = TriggerType.Webhook,
			Enabled = false,
		};
		var reg = _triggerManager.RegisterTrigger(orchPath, null, config, source: TriggerSource.Json);
		reg.Status.Should().Be(TriggerStatus.Paused);

		// Act
		_triggerManager.SetTriggerEnabled(reg.Id, true);

		// Assert
		var trigger = _triggerManager.GetTrigger(reg.Id);
		trigger.Should().NotBeNull();
		trigger!.Status.Should().Be(TriggerStatus.Waiting);
		trigger.Config.Enabled.Should().BeTrue();
	}
}
