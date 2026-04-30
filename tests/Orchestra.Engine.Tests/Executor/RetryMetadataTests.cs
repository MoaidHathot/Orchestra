using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orchestra.Engine.Tests.TestHelpers;

namespace Orchestra.Engine.Tests.Executor;

/// <summary>
/// Verifies that <see cref="RetryMetadata"/> is honoured by the executor:
/// run ID, lineage fields, and TriggeredBy are all populated on the saved run record.
/// </summary>
public class RetryMetadataTests
{
	private readonly IScheduler _scheduler = new OrchestrationScheduler();

	private static PromptOrchestrationStep Step(string name, string[]? deps = null) => new()
	{
		Name = name,
		Type = OrchestrationStepType.Prompt,
		DependsOn = deps ?? [],
		Parameters = [],
		SystemPrompt = "Test",
		UserPrompt = "Test",
		Model = "claude-opus-4.6",
	};

	private static Orchestration Orch(params OrchestrationStep[] steps) => new()
	{
		Name = "retry-meta-test",
		Description = "Retry metadata test",
		Steps = steps,
		TimeoutSeconds = 60,
	};

	[Fact]
	public async Task ExecuteAsync_WithRetryMetadata_SetsLineageFieldsAndOverridesRunId()
	{
		// Arrange
		OrchestrationRunRecord? saved = null;
		var runStore = Substitute.For<IRunStore>();
		runStore
			.SaveRunAsync(Arg.Do<OrchestrationRunRecord>(r => saved = r), Arg.Any<CancellationToken>())
			.Returns(Task.CompletedTask);

		var agentBuilder = new MockAgentBuilder().WithResponse("ok");
		var executor = new OrchestrationExecutor(_scheduler, agentBuilder, NullOrchestrationReporter.Instance, NullLoggerFactory.Instance, runStore: runStore);

		var meta = new RetryMetadata
		{
			RetriedFromRunId = "abcdef123456",
			RetryMode = "all",
			OverrideRunId = "newrun000000",
			TriggeredBy = "retry",
		};

		// Act
		await executor.ExecuteAsync(Orch(Step("only-step")), retryMetadata: meta);

		// Assert
		saved.Should().NotBeNull();
		saved!.RunId.Should().Be("newrun000000");
		saved.RetriedFromRunId.Should().Be("abcdef123456");
		saved.RetryMode.Should().Be("all");
		saved.TriggeredBy.Should().Be("retry");
	}

	[Fact]
	public async Task ResumeAsync_WithRetryMetadata_StartsFreshRunIdAndKeepsLineage()
	{
		// Arrange
		OrchestrationRunRecord? saved = null;
		var runStore = Substitute.For<IRunStore>();
		runStore
			.SaveRunAsync(Arg.Do<OrchestrationRunRecord>(r => saved = r), Arg.Any<CancellationToken>())
			.Returns(Task.CompletedTask);

		var agentBuilder = new MockAgentBuilder().WithResponse("ok-from-resume");
		var executor = new OrchestrationExecutor(_scheduler, agentBuilder, NullOrchestrationReporter.Instance, NullLoggerFactory.Instance, runStore: runStore);

		var checkpoint = new CheckpointData
		{
			RunId = "should-be-overridden",
			OrchestrationName = "retry-meta-test",
			StartedAt = DateTimeOffset.UtcNow,
			CheckpointedAt = DateTimeOffset.UtcNow,
			CompletedSteps = new Dictionary<string, CheckpointStepResult>
			{
				["step-a"] = new CheckpointStepResult { Status = ExecutionStatus.Succeeded, Content = "previous-output" },
			},
		};

		var meta = new RetryMetadata
		{
			RetriedFromRunId = "sourceabcdef",
			RetryMode = "failed",
			OverrideRunId = "retryrunxxxx",
			TriggeredBy = "retry",
		};

		// Act
		await executor.ResumeAsync(
			Orch(Step("step-a"), Step("step-b", deps: ["step-a"])),
			checkpoint,
			retryMetadata: meta);

		// Assert
		saved.Should().NotBeNull();
		saved!.RunId.Should().Be("retryrunxxxx");
		saved.RetriedFromRunId.Should().Be("sourceabcdef");
		saved.RetryMode.Should().Be("failed");
		saved.TriggeredBy.Should().Be("retry");

		// step-a was restored from the checkpoint, step-b was executed fresh
		saved.StepRecords.Should().ContainKey("step-a");
		saved.StepRecords["step-a"].Status.Should().Be(ExecutionStatus.Succeeded);
		saved.StepRecords["step-a"].Content.Should().Be("previous-output", "restored from checkpoint");
		saved.StepRecords.Should().ContainKey("step-b");
		saved.StepRecords["step-b"].Content.Should().Be("ok-from-resume", "freshly executed");
	}

	[Fact]
	public async Task ExecuteAsync_WithoutRetryMetadata_LeavesLineageFieldsNull()
	{
		OrchestrationRunRecord? saved = null;
		var runStore = Substitute.For<IRunStore>();
		runStore
			.SaveRunAsync(Arg.Do<OrchestrationRunRecord>(r => saved = r), Arg.Any<CancellationToken>())
			.Returns(Task.CompletedTask);

		var agentBuilder = new MockAgentBuilder().WithResponse("ok");
		var executor = new OrchestrationExecutor(_scheduler, agentBuilder, NullOrchestrationReporter.Instance, NullLoggerFactory.Instance, runStore: runStore);

		await executor.ExecuteAsync(Orch(Step("a")));

		saved.Should().NotBeNull();
		saved!.RetriedFromRunId.Should().BeNull();
		saved.RetryMode.Should().BeNull();
		saved.TriggeredBy.Should().Be("manual", "default for non-retry, non-trigger runs");
	}
}
