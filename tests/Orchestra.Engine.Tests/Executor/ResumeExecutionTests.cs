using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orchestra.Engine.Tests.TestHelpers;

namespace Orchestra.Engine.Tests.Executor;

public class ResumeExecutionTests
{
	private static CheckpointData Checkpoint(string name) => new()
	{
		RunId = "resume", OrchestrationName = name, StartedAt = DateTimeOffset.UtcNow.AddDays(-1),
		CheckpointedAt = DateTimeOffset.UtcNow.AddHours(-1), CompletedSteps = [],
	};

	[Fact]
	public async Task Resume_AppliesOptionalInputDefaultsToTemplatesAndRuntimeValidation()
	{
		var definition = new Orchestration
		{
			Name = "resume-defaults", Description = "Defaults apply when resuming", TimeoutSeconds = null,
			Inputs = new() { ["greeting"] = new() { Required = false, Default = "hello" } },
			Variables = new() { ["message"] = "{{param.greeting}}" },
			Steps = [new TransformOrchestrationStep { Name = "result", Type = OrchestrationStepType.Transform, Template = "{{vars.message}}" }],
		};
		var executor = new OrchestrationExecutor(new OrchestrationScheduler(), new MockAgentBuilder(), NullOrchestrationReporter.Instance, NullLoggerFactory.Instance);

		var result = await executor.ResumeAsync(definition, Checkpoint(definition.Name));

		result.StepResults["result"].Content.Should().Be("hello");
	}

	[Fact]
	public async Task ResumedHumanWait_UsesTheNewTimeoutBudgetRatherThanTheHistoricalStartTime()
	{
		var registry = new StepExecutorRegistry().Register(new DelegateStepExecutor(OrchestrationStepType.Transform, async (step, context, token) =>
		{
			context.OnAwaitingInput!(new PendingInputRecord
			{
				RunId = context.OrchestrationInfo.RunId, OrchestrationName = context.OrchestrationInfo.Name,
				StepName = step.Name, Kind = PendingInputKind.Approval, Prompt = "Continue?", CreatedAt = DateTimeOffset.UtcNow,
			});
			await Task.Yield();
			context.OnInputResolved!(context.OrchestrationInfo.RunId, step.Name);
			await Task.Delay(50, token);
			return ExecutionResult.Succeeded("finished");
		}));
		var definition = new Orchestration
		{
			Name = "resume-clock", Description = "Resumed timeout clock", TimeoutSeconds = 10, PauseTimeoutDuringWait = true,
			Steps = [new TransformOrchestrationStep { Name = "wait", Type = OrchestrationStepType.Transform, Template = "" }],
		};
		var executor = new OrchestrationExecutor(new OrchestrationScheduler(), new MockAgentBuilder(), NullOrchestrationReporter.Instance,
			NullLoggerFactory.Instance, stepExecutorRegistry: registry);

		var result = await executor.ResumeAsync(definition, Checkpoint(definition.Name));

		result.Status.Should().Be(ExecutionStatus.Succeeded, "the day spent suspended must not consume this invocation's timeout");
	}

	[Fact]
	public async Task RestoredStepRecords_RetainTraceUsageAndModelMetadata()
	{
		var definition = new Orchestration
		{
			Name = "resume-fidelity", Description = "Retain restored diagnostics",
			Steps = [new TransformOrchestrationStep { Name = "done", Type = OrchestrationStepType.Transform, Template = "unused" }],
		};
		var trace = new StepExecutionTrace { FinalResponse = "original", ConfiguredProvider = "provider", ActualProvider = "provider" };
		var original = ExecutionResult.Succeeded("original") with
		{
			Trace = trace, SelectedModel = "selected", ActualModel = "actual", Usage = new TokenUsage { InputTokens = 30, OutputTokens = 10 },
			RetryHistory = [new() { Attempt = 1, Error = "transient", AttemptedAt = DateTimeOffset.UtcNow.AddMinutes(-5), DelaySeconds = 0 }],
		};
		var checkpoint = Checkpoint(definition.Name);
		checkpoint.CompletedSteps["done"] = CheckpointStepResult.FromExecutionResult(original);
		var runs = Substitute.For<IRunStore>();
		OrchestrationRunRecord? saved = null;
		runs.SaveRunAsync(Arg.Do<OrchestrationRunRecord>(r => saved = r), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
		var executor = new OrchestrationExecutor(new OrchestrationScheduler(), new MockAgentBuilder(), NullOrchestrationReporter.Instance,
			NullLoggerFactory.Instance, runStore: runs);

		await executor.ResumeAsync(definition, checkpoint);

		var restored = saved!.StepRecords["done"];
		restored.Trace.Should().BeSameAs(trace);
		restored.Usage.Should().BeEquivalentTo(original.Usage);
		restored.SelectedModel.Should().Be("selected");
		restored.RetryHistory.Should().ContainSingle();
		restored.CompletedAt.Should().Be(checkpoint.CheckpointedAt);
		saved.TotalUsage!.InputTokens.Should().Be(30);
	}
}
