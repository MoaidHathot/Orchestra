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
		config.Response.Should().BeNull();
	}

	[Fact]
	public void WebhookResponseConfig_DefaultValues()
	{
		// Act
		var config = new WebhookResponseConfig();

		// Assert
		config.WaitForResult.Should().BeFalse();
		config.ResponseTemplate.Should().BeNull();
		config.TimeoutSeconds.Should().Be(120);
	}

	[Fact]
	public void WebhookTriggerConfig_WithResponseConfig()
	{
		// Arrange & Act
		var config = new WebhookTriggerConfig
		{
			Type = TriggerType.Webhook,
			Secret = "test-secret",
			MaxConcurrent = 2,
			Response = new WebhookResponseConfig
			{
				WaitForResult = true,
				ResponseTemplate = "Result: {{analyze.Content}}",
				TimeoutSeconds = 60,
			}
		};

		// Assert
		config.Response.Should().NotBeNull();
		config.Response!.WaitForResult.Should().BeTrue();
		config.Response.ResponseTemplate.Should().Be("Result: {{analyze.Content}}");
		config.Response.TimeoutSeconds.Should().Be(60);
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
		info.Status.Should().Be(HostExecutionStatus.Running);
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
			InputHandlerPrompt = "Transform the input",
			InputHandlerModel = "claude-sonnet-4",
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
		schedulerClone.InputHandlerPrompt.Should().Be("Transform the input");
		schedulerClone.InputHandlerModel.Should().Be("claude-sonnet-4");
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
			InputHandlerPrompt = "Parse the loop input",
			InputHandlerModel = "gpt-4.1-mini",
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
		loopClone.InputHandlerPrompt.Should().Be("Parse the loop input");
		loopClone.InputHandlerModel.Should().Be("gpt-4.1-mini");
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
			InputHandlerPrompt = "Extract webhook fields",
			InputHandlerModel = "claude-opus-4.6",
			Secret = "my-secret",
			MaxConcurrent = 3,
			Response = new WebhookResponseConfig
			{
				WaitForResult = true,
				ResponseTemplate = "Hello {{step1.Content}}",
				TimeoutSeconds = 30,
			}
		};

		// Act
		var cloned = TriggerManager.CloneTriggerConfigWithEnabled(original, false);

		// Assert
		cloned.Should().BeOfType<WebhookTriggerConfig>();
		var webhookClone = (WebhookTriggerConfig)cloned;
		webhookClone.Enabled.Should().BeFalse();
		webhookClone.InputHandlerPrompt.Should().Be("Extract webhook fields");
		webhookClone.InputHandlerModel.Should().Be("claude-opus-4.6");
		webhookClone.Secret.Should().Be("my-secret");
		webhookClone.MaxConcurrent.Should().Be(3);
		webhookClone.Response.Should().NotBeNull();
		webhookClone.Response!.WaitForResult.Should().BeTrue();
		webhookClone.Response.ResponseTemplate.Should().Be("Hello {{step1.Content}}");
		webhookClone.Response.TimeoutSeconds.Should().Be(30);
	}

	[Fact]
	public void CloneTriggerConfigWithEnabled_WebhookConfig_NullResponse_ClonesCorrectly()
	{
		// Arrange
		var original = new WebhookTriggerConfig
		{
			Type = TriggerType.Webhook,
			Enabled = true,
			Secret = "my-secret",
			MaxConcurrent = 3,
		};

		// Act
		var cloned = TriggerManager.CloneTriggerConfigWithEnabled(original, false);

		// Assert
		cloned.Should().BeOfType<WebhookTriggerConfig>();
		var webhookClone = (WebhookTriggerConfig)cloned;
		webhookClone.Enabled.Should().BeFalse();
		webhookClone.Secret.Should().Be("my-secret");
		webhookClone.MaxConcurrent.Should().Be(3);
		webhookClone.Response.Should().BeNull();
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
	public void GenerateTriggerId_IsDeterministicAcrossProcessRestarts()
	{
		// This test verifies the ID is based on a deterministic hash (SHA-256),
		// not string.GetHashCode() which is randomized per-process in .NET 6+.
		// The expected value is pre-computed and must remain stable across runs.
		var path = "/path/to/orchestration.json";
		var name = "my-orchestration";

		var id = TriggerManager.GenerateTriggerId(path, name);

		// SHA-256 of the path produces a fixed hash; first 4 hex chars = "1510"
		id.Should().Be("my-orchestration-1510");
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
		public void ReportStepCompleted(string stepName, AgentResult result, OrchestrationStepType stepType) { }
		public void ReportStepTrace(string stepName, StepExecutionTrace trace) { }
		public void ReportModelMismatch(ModelMismatchInfo mismatch) { }
		public void ReportStepOutput(string stepName, string content) { }
		public void ReportStepStarted(string stepName) { }
		public void ReportStepSkipped(string stepName, string reason) { }
		public void ReportStepRetry(string stepName, int attempt, int maxRetries, string error, TimeSpan delay) { }
		public void ReportLoopIteration(string checkerStepName, string targetStepName, int iteration, int maxIterations) { }
		public void ReportCheckpointSaved(string runId, string stepName, int completedSteps, int totalSteps) { }
		public void ReportSessionWarning(string warningType, string message) { }
		public void ReportSessionInfo(string infoType, string message) { }
		public void ReportMcpServersLoaded(IReadOnlyList<McpServerStatusInfo> servers) { }
		public void ReportMcpServerStatusChanged(string serverName, string status) { }
		public void ReportSubagentSelected(string stepName, string agentName, string? displayName, string[]? tools) { }
		public void ReportSubagentStarted(string stepName, string? toolCallId, string agentName, string? displayName, string? description) { }
		public void ReportSubagentCompleted(string stepName, string? toolCallId, string agentName, string? displayName) { }
		public void ReportSubagentFailed(string stepName, string? toolCallId, string agentName, string? displayName, string? error) { }
		public void ReportSubagentDeselected(string stepName) { }
		public void ReportRunContext(RunContext context) { }
		public void ReportAuditLogEntry(string stepName, AuditLogEntry entry) { }
	}
}
