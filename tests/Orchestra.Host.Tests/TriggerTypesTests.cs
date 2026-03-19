using FluentAssertions;
using Orchestra.Engine;
using Orchestra.Host.Triggers;
using Xunit;

namespace Orchestra.Host.Tests;

/// <summary>
/// Unit tests for TriggerTypes (TriggerConfig classes and TriggerManager static methods).
/// </summary>
public class TriggerTypesTests
{
	[Fact]
	public void SchedulerTriggerConfig_DefaultValues()
	{
		// Act
		var config = new SchedulerTriggerConfig { Type = TriggerType.Scheduler };

		// Assert
		config.Type.Should().Be(TriggerType.Scheduler);
		config.Enabled.Should().BeTrue();
		config.Cron.Should().BeNull();
		config.IntervalSeconds.Should().BeNull();
		config.MaxRuns.Should().BeNull();
	}

	[Fact]
	public void LoopTriggerConfig_DefaultValues()
	{
		// Act
		var config = new LoopTriggerConfig { Type = TriggerType.Loop };

		// Assert
		config.Type.Should().Be(TriggerType.Loop);
		config.Enabled.Should().BeTrue();
		config.DelaySeconds.Should().Be(0);
		config.MaxIterations.Should().BeNull();
		config.ContinueOnFailure.Should().BeFalse();
	}

	[Fact]
	public void WebhookTriggerConfig_DefaultValues()
	{
		// Act
		var config = new WebhookTriggerConfig { Type = TriggerType.Webhook };

		// Assert
		config.Type.Should().Be(TriggerType.Webhook);
		config.Enabled.Should().BeTrue();
		config.Secret.Should().BeNull();
		config.MaxConcurrent.Should().Be(1);
	}

	[Fact]
	public void EmailTriggerConfig_DefaultValues()
	{
		// Act
		var config = new EmailTriggerConfig { Type = TriggerType.Email };

		// Assert
		config.Type.Should().Be(TriggerType.Email);
		config.Enabled.Should().BeTrue();
		config.FolderPath.Should().Be("Inbox");
		config.PollIntervalSeconds.Should().Be(60);
		config.MaxItemsPerPoll.Should().Be(10);
		config.SubjectContains.Should().BeNull();
		config.SenderContains.Should().BeNull();
	}

	[Fact]
	public void TriggerRegistration_DefaultStatus()
	{
		// Act
		var reg = new TriggerRegistration
		{
			Id = "test-id",
			OrchestrationPath = "/path/to/orch.json",
			Config = new LoopTriggerConfig { Type = TriggerType.Loop }
		};

		// Assert
		reg.Status.Should().Be(TriggerStatus.Idle);
		reg.RunCount.Should().Be(0);
		reg.Source.Should().Be(TriggerSource.User);
	}

	[Fact]
	public void ActiveExecutionInfo_DefaultValues()
	{
		// Act
		using var cts = new CancellationTokenSource();
		var info = new ActiveExecutionInfo
		{
			ExecutionId = "exec-123",
			OrchestrationId = "orch-456",
			OrchestrationName = "My Orchestration",
			StartedAt = DateTimeOffset.UtcNow,
			TriggeredBy = "manual",
			CancellationTokenSource = cts,
			Reporter = new TestOrchestrationReporter()
		};

		// Assert
		info.Status.Should().Be("Running");
		info.TotalSteps.Should().Be(0);
		info.CompletedSteps.Should().Be(0);
		info.CurrentStep.Should().BeNull();
	}

	[Fact]
	public void CloneTriggerConfigWithEnabled_SchedulerConfig_ClonesCorrectly()
	{
		// Arrange
		var original = new SchedulerTriggerConfig
		{
			Type = TriggerType.Scheduler,
			Enabled = true,
			Cron = "*/5 * * * *",
			IntervalSeconds = 300,
			MaxRuns = 10
		};

		// Act
		var cloned = TriggerManager.CloneTriggerConfigWithEnabled(original, false);

		// Assert
		cloned.Should().BeOfType<SchedulerTriggerConfig>();
		var schedulerClone = (SchedulerTriggerConfig)cloned;
		schedulerClone.Enabled.Should().BeFalse();
		schedulerClone.Cron.Should().Be("*/5 * * * *");
		schedulerClone.IntervalSeconds.Should().Be(300);
		schedulerClone.MaxRuns.Should().Be(10);
	}

	[Fact]
	public void CloneTriggerConfigWithEnabled_LoopConfig_ClonesCorrectly()
	{
		// Arrange
		var original = new LoopTriggerConfig
		{
			Type = TriggerType.Loop,
			Enabled = false,
			DelaySeconds = 60,
			MaxIterations = 5,
			ContinueOnFailure = true
		};

		// Act
		var cloned = TriggerManager.CloneTriggerConfigWithEnabled(original, true);

		// Assert
		cloned.Should().BeOfType<LoopTriggerConfig>();
		var loopClone = (LoopTriggerConfig)cloned;
		loopClone.Enabled.Should().BeTrue();
		loopClone.DelaySeconds.Should().Be(60);
		loopClone.MaxIterations.Should().Be(5);
		loopClone.ContinueOnFailure.Should().BeTrue();
	}

	[Fact]
	public void CloneTriggerConfigWithEnabled_WebhookConfig_ClonesCorrectly()
	{
		// Arrange
		var original = new WebhookTriggerConfig
		{
			Type = TriggerType.Webhook,
			Enabled = true,
			Secret = "my-secret",
			MaxConcurrent = 3
		};

		// Act
		var cloned = TriggerManager.CloneTriggerConfigWithEnabled(original, false);

		// Assert
		cloned.Should().BeOfType<WebhookTriggerConfig>();
		var webhookClone = (WebhookTriggerConfig)cloned;
		webhookClone.Enabled.Should().BeFalse();
		webhookClone.Secret.Should().Be("my-secret");
		webhookClone.MaxConcurrent.Should().Be(3);
	}

	[Fact]
	public void CloneTriggerConfigWithEnabled_EmailConfig_ClonesCorrectly()
	{
		// Arrange
		var original = new EmailTriggerConfig
		{
			Type = TriggerType.Email,
			Enabled = true,
			FolderPath = "Custom/Folder",
			PollIntervalSeconds = 120,
			MaxItemsPerPoll = 20,
			SubjectContains = "[ALERT]",
			SenderContains = "@example.com"
		};

		// Act
		var cloned = TriggerManager.CloneTriggerConfigWithEnabled(original, false);

		// Assert
		cloned.Should().BeOfType<EmailTriggerConfig>();
		var emailClone = (EmailTriggerConfig)cloned;
		emailClone.Enabled.Should().BeFalse();
		emailClone.FolderPath.Should().Be("Custom/Folder");
		emailClone.PollIntervalSeconds.Should().Be(120);
		emailClone.MaxItemsPerPoll.Should().Be(20);
		emailClone.SubjectContains.Should().Be("[ALERT]");
		emailClone.SenderContains.Should().Be("@example.com");
	}

	[Fact]
	public void GenerateTriggerId_SamePath_ProducesSameId()
	{
		// Arrange
		var path = "/path/to/orchestration.json";
		var name = "my-orchestration";

		// Act
		var id1 = TriggerManager.GenerateTriggerId(path, name);
		var id2 = TriggerManager.GenerateTriggerId(path, name);

		// Assert
		id1.Should().Be(id2);
	}

	[Fact]
	public void GenerateTriggerId_DifferentPaths_ProducesDifferentIds()
	{
		// Arrange
		var name = "my-orchestration";

		// Act
		var id1 = TriggerManager.GenerateTriggerId("/path/one/orchestration.json", name);
		var id2 = TriggerManager.GenerateTriggerId("/path/two/orchestration.json", name);

		// Assert
		id1.Should().NotBe(id2);
	}

	[Fact]
	public void GenerateTriggerId_WithoutName_UsesFilename()
	{
		// Arrange
		var path = "/path/to/my-file.json";

		// Act
		var id = TriggerManager.GenerateTriggerId(path);

		// Assert
		id.Should().Contain("my-file");
	}

	[Fact]
	public void GenerateTriggerId_SanitizesSpecialCharacters()
	{
		// Arrange
		var path = "/some/path.json";
		var name = "My Orchestration! With Special@Chars#123";

		// Act
		var id = TriggerManager.GenerateTriggerId(path, name);

		// Assert
		id.Should().NotContain(" ");
		id.Should().NotContain("!");
		id.Should().NotContain("@");
		id.Should().NotContain("#");
		id.Should().MatchRegex(@"^[a-z0-9\-]+$");
	}

	[Fact]
	public void TriggerStatus_AllValuesExist()
	{
		// Assert - verify all expected enum values exist
		var values = Enum.GetValues<TriggerStatus>();
		values.Should().Contain(TriggerStatus.Idle);
		values.Should().Contain(TriggerStatus.Waiting);
		values.Should().Contain(TriggerStatus.Running);
		values.Should().Contain(TriggerStatus.Paused);
		values.Should().Contain(TriggerStatus.Error);
		values.Should().Contain(TriggerStatus.Completed);
	}

	[Fact]
	public void TriggerType_AllValuesExist()
	{
		// Assert - verify all expected enum values exist
		var values = Enum.GetValues<TriggerType>();
		values.Should().Contain(TriggerType.Scheduler);
		values.Should().Contain(TriggerType.Loop);
		values.Should().Contain(TriggerType.Webhook);
		values.Should().Contain(TriggerType.Email);
	}

	[Fact]
	public void TriggerSource_AllValuesExist()
	{
		// Assert - verify all expected enum values exist
		var values = Enum.GetValues<TriggerSource>();
		values.Should().Contain(TriggerSource.Json);
		values.Should().Contain(TriggerSource.User);
	}

	/// <summary>
	/// Simple test implementation of IOrchestrationReporter for testing.
	/// </summary>
	private sealed class TestOrchestrationReporter : IOrchestrationReporter
	{
		public void ReportSessionStarted(string requestedModel, string? selectedModel) { }
		public void ReportModelChange(string? previousModel, string newModel) { }
		public void ReportUsage(string stepName, string model, AgentUsage usage) { }
		public void ReportContentDelta(string stepName, string chunk) { }
		public void ReportReasoningDelta(string stepName, string chunk) { }
		public void ReportToolExecutionStarted(string stepName, string toolName, string? arguments, string? mcpServer) { }
		public void ReportToolExecutionCompleted(string stepName, string toolName, bool success, string? result, string? error) { }
		public void ReportStepError(string stepName, string errorMessage) { }
		public void ReportStepCancelled(string stepName) { }
		public void ReportStepCompleted(string stepName, AgentResult result) { }
		public void ReportStepTrace(string stepName, StepExecutionTrace trace) { }
		public void ReportModelMismatch(ModelMismatchInfo mismatch) { }
		public void ReportStepOutput(string stepName, string content) { }
		public void ReportStepStarted(string stepName) { }
		public void ReportStepSkipped(string stepName, string reason) { }
		public void ReportStepRetry(string stepName, int attempt, int maxRetries, string error, TimeSpan delay) { }
		public void ReportLoopIteration(string checkerStepName, string targetStepName, int iteration, int maxIterations) { }
		public void ReportCheckpointSaved(string runId, string stepName, int completedSteps, int totalSteps) { }
		public void ReportSubagentSelected(string stepName, string agentName, string? displayName, string[]? tools) { }
		public void ReportSubagentStarted(string stepName, string? toolCallId, string agentName, string? displayName, string? description) { }
		public void ReportSubagentCompleted(string stepName, string? toolCallId, string agentName, string? displayName) { }
		public void ReportSubagentFailed(string stepName, string? toolCallId, string agentName, string? displayName, string? error) { }
		public void ReportSubagentDeselected(string stepName) { }
	}
}
