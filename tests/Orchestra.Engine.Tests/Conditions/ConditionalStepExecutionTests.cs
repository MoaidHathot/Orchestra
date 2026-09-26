using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orchestra.Engine.Tests.TestHelpers;

namespace Orchestra.Engine.Tests.Conditions;

/// <summary>
/// End-to-end behaviour of step <c>when:</c> conditions through <see cref="OrchestrationExecutor"/>.
/// Transform steps are used because they are deterministic and need no agent; a Prompt step
/// on the mock agent supplies a real failure where <c>{{step.status}}</c> gating is exercised.
/// </summary>
public class ConditionalStepExecutionTests
{
	private readonly IScheduler _scheduler = new OrchestrationScheduler();
	private readonly IOrchestrationReporter _reporter = SerializingReporterProxy.CreateRecording();
	private IOrchestrationReporter _reporterSubstitute => SerializingReporterProxy.Recorded(_reporter);

	private OrchestrationExecutor CreateExecutor(MockAgentBuilder? agent = null)
		=> new(_scheduler, agent ?? new MockAgentBuilder().WithResponse("ok"), _reporter, NullLoggerFactory.Instance);

	private static TransformOrchestrationStep Transform(string name, string template, string[]? dependsOn = null, string? when = null, string[]? parameters = null) => new()
	{
		Name = name,
		Type = OrchestrationStepType.Transform,
		Template = template,
		DependsOn = dependsOn ?? [],
		When = when,
		Parameters = parameters ?? [],
	};

	private static Orchestration Orch(string name, params OrchestrationStep[] steps) => new()
	{
		Name = name,
		Description = name,
		Steps = steps,
		TimeoutSeconds = null,
	};

	[Fact]
	public async Task WhenTrue_StepRuns()
	{
		var executor = CreateExecutor();
		var result = await executor.ExecuteAsync(
			Orch("when-true", Transform("a", "ran", when: "{{param.go}} == 'yes'", parameters: ["go"])),
			new Dictionary<string, string> { ["go"] = "yes" });

		result.Status.Should().Be(ExecutionStatus.Succeeded);
		result.StepResults["a"].Status.Should().Be(ExecutionStatus.Succeeded);
		result.StepResults["a"].Content.Should().Be("ran");
	}

	[Fact]
	public async Task WhenFalse_StepIsSkippedWithConditionKind_AndReportedSkipped()
	{
		var executor = CreateExecutor();
		var result = await executor.ExecuteAsync(
			Orch("when-false", Transform("a", "ran", when: "{{param.go}} == 'yes'", parameters: ["go"])),
			new Dictionary<string, string> { ["go"] = "no" });

		var step = result.StepResults["a"];
		step.Status.Should().Be(ExecutionStatus.Skipped);
		step.SkipKind.Should().Be(SkipKind.Condition);
		step.BlocksDependents.Should().BeFalse();
		step.ErrorMessage.Should().Contain("Condition evaluated to false");
		step.Content.Should().BeEmpty();
		_reporterSubstitute.Received(1).ReportStepSkipped("a", Arg.Is<string>(r => r.Contains("Condition evaluated to false")));

		// A condition skip is not a failure: the orchestration as a whole is fine.
		result.Status.Should().NotBe(ExecutionStatus.Failed);
	}

	[Fact]
	public async Task WhenFalse_DependentsStillRun_AndSeeEmptyOutput()
	{
		// Unlike a dependency failure, a condition skip must not cascade (Argo-style), so
		// downstream steps can join two mutually exclusive branches.
		var executor = CreateExecutor();
		var result = await executor.ExecuteAsync(
			Orch("no-cascade",
				Transform("branch", "branch output", when: "false"),
				Transform("after", "[{{branch.output}}]", dependsOn: ["branch"])));

		result.StepResults["branch"].Status.Should().Be(ExecutionStatus.Skipped);
		result.StepResults["after"].Status.Should().Be(ExecutionStatus.Succeeded, "a condition skip does not block dependents");
		result.StepResults["after"].Content.Should().Be("[]", "the skipped step's output resolves to an empty string");
	}

	[Fact]
	public async Task IfElseJoin_ExactlyOneBranchRuns_AndTheJoinSeesIt()
	{
		var executor = CreateExecutor();
		var orchestration = Orch("if-else",
			Transform("decide", "prod", parameters: []),
			Transform("deploy-prod", "deployed to PROD", dependsOn: ["decide"], when: "{{decide.output}} == 'prod'"),
			Transform("deploy-staging", "deployed to STAGING", dependsOn: ["decide"], when: "{{decide.output}} != 'prod'"),
			Transform("notify", "{{deploy-prod.output}}{{deploy-staging.output}}", dependsOn: ["deploy-prod", "deploy-staging"]));

		var result = await executor.ExecuteAsync(orchestration);

		result.Status.Should().Be(ExecutionStatus.Succeeded);
		result.StepResults["deploy-prod"].Status.Should().Be(ExecutionStatus.Succeeded);
		result.StepResults["deploy-staging"].Status.Should().Be(ExecutionStatus.Skipped);
		result.StepResults["deploy-staging"].SkipKind.Should().Be(SkipKind.Condition);
		result.StepResults["notify"].Status.Should().Be(ExecutionStatus.Succeeded);
		result.StepResults["notify"].Content.Should().Be("deployed to PROD");
	}

	[Fact]
	public async Task StatusAccessor_LetsAnOnFailureHandlerRun_AfterItsDependencyFailed()
	{
		// {{tests.status}} is exposed on every step type so `when` can branch on outcome.
		// Referencing a step's status is "outcome-aware", which suspends the failure cascade
		// for the gated step; otherwise an on-failure handler could never run. The handler
		// gated on success is condition-skipped (not cascade-skipped), so a join step depending
		// on both still runs.
		var agent = new MockAgentBuilder().WithException(new InvalidOperationException("tests exploded"));
		var executor = CreateExecutor(agent);

		var tests = TestOrchestrations.CreatePromptStep("tests");
		var orchestration = Orch("status-gate",
			tests,
			Transform("on-failure", "FAILED: {{tests.errorMessage}}", dependsOn: ["tests"], when: "{{tests.status}} == 'failed'"),
			Transform("on-success", "PASSED", dependsOn: ["tests"], when: "{{tests.status}} == 'succeeded'"),
			Transform("report", "{{on-failure.output}}{{on-success.output}}", dependsOn: ["on-failure", "on-success"]));

		var result = await executor.ExecuteAsync(orchestration);

		result.StepResults["tests"].Status.Should().Be(ExecutionStatus.Failed);

		result.StepResults["on-failure"].Status.Should().Be(ExecutionStatus.Succeeded, "the outcome-aware condition decides, not the cascade");
		result.StepResults["on-failure"].Content.Should().Be("FAILED: tests exploded");

		result.StepResults["on-success"].Status.Should().Be(ExecutionStatus.Skipped);
		result.StepResults["on-success"].SkipKind.Should().Be(SkipKind.Condition, "its condition was consulted and was false");

		result.StepResults["report"].Status.Should().Be(ExecutionStatus.Succeeded);
		result.StepResults["report"].Content.Should().Be("FAILED: tests exploded");

		// The run is still Failed overall because `tests` failed — the handler does not paper over it.
		result.Status.Should().Be(ExecutionStatus.Failed);
	}

	[Fact]
	public async Task FailureFunction_RunsOnlyWhenADependencyDidNotSucceed()
	{
		var agent = new MockAgentBuilder().WithException(new InvalidOperationException("boom"));
		var executor = CreateExecutor(agent);

		var result = await executor.ExecuteAsync(Orch("failure-fn",
			TestOrchestrations.CreatePromptStep("build"),
			Transform("cleanup", "cleaned up after failure", dependsOn: ["build"], when: "failure()"),
			Transform("publish", "published", dependsOn: ["build"], when: "success()"),
			Transform("notify", "notified", dependsOn: ["build"], when: "always()")));

		result.StepResults["cleanup"].Status.Should().Be(ExecutionStatus.Succeeded);
		result.StepResults["publish"].Status.Should().Be(ExecutionStatus.Skipped);
		result.StepResults["publish"].SkipKind.Should().Be(SkipKind.Condition);
		result.StepResults["notify"].Status.Should().Be(ExecutionStatus.Succeeded, "always() runs regardless of dependency outcome");
	}

	[Fact]
	public async Task FailureFunction_IsFalse_WhenEveryDependencySucceeded()
	{
		var executor = CreateExecutor();

		var result = await executor.ExecuteAsync(Orch("success-path",
			Transform("build", "built"),
			Transform("cleanup", "cleaned", dependsOn: ["build"], when: "failure()"),
			Transform("publish", "published", dependsOn: ["build"], when: "success()")));

		result.StepResults["cleanup"].Status.Should().Be(ExecutionStatus.Skipped);
		result.StepResults["cleanup"].SkipKind.Should().Be(SkipKind.Condition);
		result.StepResults["publish"].Status.Should().Be(ExecutionStatus.Succeeded);
	}

	[Fact]
	public async Task ConditionWithoutOutcomeReference_StillCascadesOnDependencyFailure()
	{
		// The default must stay safe: a `when` about something unrelated (a parameter) does
		// not opt the step out of the failure cascade.
		var agent = new MockAgentBuilder().WithException(new InvalidOperationException("boom"));
		var executor = CreateExecutor(agent);

		var result = await executor.ExecuteAsync(
			Orch("plain-when-cascades",
				TestOrchestrations.CreatePromptStep("build"),
				Transform("deploy", "deployed", dependsOn: ["build"], when: "{{param.deploy}} == 'yes'", parameters: ["deploy"])),
			new Dictionary<string, string> { ["deploy"] = "yes" });

		result.StepResults["deploy"].Status.Should().Be(ExecutionStatus.Skipped);
		result.StepResults["deploy"].SkipKind.Should().Be(SkipKind.DependencyFailure, "the cascade applies before an outcome-unaware condition");
	}

	[Fact]
	public async Task StatusAccessor_OnACompletedNonDependency_ResolvesForConditions()
	{
		// A step may reference the status of a step it does not depend on if that step has
		// already finished (here: sequenced through a shared dependency).
		var executor = CreateExecutor();
		var orchestration = Orch("status-non-dep",
			Transform("first", "one"),
			Transform("second", "two", dependsOn: ["first"]),
			Transform("third", "first={{first.status}}", dependsOn: ["second"], when: "{{first.status}} == 'succeeded'"));

		var result = await executor.ExecuteAsync(orchestration);

		result.StepResults["third"].Status.Should().Be(ExecutionStatus.Succeeded);
		result.StepResults["third"].Content.Should().Be("first=succeeded");
	}

	[Fact]
	public async Task ConditionSyntaxError_IsRejectedBeforeTheRunStarts()
	{
		// The executor runs the template validator first; a grammar error never reaches the
		// scheduler. (`orchestra validate` reports the same error without a server.)
		var executor = CreateExecutor();
		var act = () => executor.ExecuteAsync(Orch("bad-when", Transform("a", "ran", when: "this is not valid")));

		var ex = await act.Should().ThrowAsync<InvalidOperationException>();
		ex.Which.Message.Should().Contain("Invalid `when` condition").And.Contain("Unknown identifier 'this'");
	}

	[Fact]
	public async Task ConditionRuntimeError_FailsTheStep_WithConditionErrorCategory()
	{
		// An invalid regex passes structural validation (matches() is not evaluated at parse
		// time) and fails at run time. That must be a loud failure, not a silent skip.
		var executor = CreateExecutor();
		var result = await executor.ExecuteAsync(
			Orch("bad-regex", Transform("a", "ran", when: "matches('x', '(unclosed')")));

		var step = result.StepResults["a"];
		step.Status.Should().Be(ExecutionStatus.Failed);
		step.ErrorCategory.Should().Be(StepErrorCategory.ConditionError);
		step.ErrorMessage.Should().Contain("Failed to evaluate condition").And.Contain("invalid regular expression");
		result.Status.Should().Be(ExecutionStatus.Failed);
	}

	[Fact]
	public async Task ConditionError_CascadesToDependents_LikeAnyFailure()
	{
		var executor = CreateExecutor();
		var result = await executor.ExecuteAsync(
			Orch("bad-when-cascade",
				Transform("a", "ran", when: "matches('x', '(unclosed')"),
				Transform("b", "{{a.output}}", dependsOn: ["a"])));

		result.StepResults["a"].Status.Should().Be(ExecutionStatus.Failed);
		result.StepResults["b"].Status.Should().Be(ExecutionStatus.Skipped);
		result.StepResults["b"].SkipKind.Should().Be(SkipKind.DependencyFailure);
	}

	[Fact]
	public async Task UndeclaredParameterInCondition_IsRejectedBeforeTheRunStarts()
	{
		var executor = CreateExecutor();
		var act = () => executor.ExecuteAsync(Orch("undeclared-when", Transform("a", "ran", when: "{{param.undeclared}}")));

		var ex = await act.Should().ThrowAsync<InvalidOperationException>();
		ex.Which.Message.Should().Contain("Parameter 'undeclared' is not declared");
	}

	[Fact]
	public async Task UnresolvableReferenceAtRunTime_IsNull_SoTheStepIsSkippedNotFailed()
	{
		// A JSON-path reference passes validation (the output's shape is data, not schema) but
		// the field is absent at run time. It resolves to null, which is falsy: the step is
		// condition-skipped rather than failed.
		var executor = CreateExecutor();
		var result = await executor.ExecuteAsync(
			Orch("missing-json-field",
				Transform("decide", """{"other":"value"}"""),
				Transform("a", "ran", dependsOn: ["decide"], when: "{{decide.output.flag}}")));

		result.StepResults["a"].Status.Should().Be(ExecutionStatus.Skipped);
		result.StepResults["a"].SkipKind.Should().Be(SkipKind.Condition);
	}

	[Fact]
	public async Task ConditionSkippedStep_IsNotCheckpointed_AndIsReEvaluatedOnResume()
	{
		// Only Succeeded steps are checkpointed; a condition skip must be re-evaluated on
		// resume because its inputs may legitimately differ.
		var store = new CapturingCheckpointStore();
		var executor = new OrchestrationExecutor(_scheduler, new MockAgentBuilder(), _reporter, NullLoggerFactory.Instance, checkpointStore: store);

		await executor.ExecuteAsync(
			Orch("ckpt", Transform("a", "ran", when: "false"), Transform("b", "b", dependsOn: ["a"])));

		store.LastSaved.Should().NotBeNull();
		store.LastSaved!.CompletedSteps.Keys.Should().Contain("b");
		store.LastSaved.CompletedSteps.Keys.Should().NotContain("a", "a condition-skipped step is not a completed step");
	}

	[Fact]
	public async Task RunRecord_PersistsSkipKindAndCondition()
	{
		var runStore = Substitute.For<IRunStore>();
		OrchestrationRunRecord? captured = null;
		runStore.SaveRunAsync(Arg.Do<OrchestrationRunRecord>(r => captured = r), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

		var executor = new OrchestrationExecutor(_scheduler, new MockAgentBuilder(), _reporter, NullLoggerFactory.Instance, runStore: runStore);
		await executor.ExecuteAsync(Orch("record", Transform("a", "ran", when: "1 > 2")));

		captured.Should().NotBeNull();
		var record = captured!.AllStepRecords["a"];
		record.Status.Should().Be(ExecutionStatus.Skipped);
		record.SkipKind.Should().Be(SkipKind.Condition);
		record.Condition.Should().Be("1 > 2");
	}

	private sealed class CapturingCheckpointStore : ICheckpointStore
	{
		public CheckpointData? LastSaved { get; private set; }

		public Task SaveCheckpointAsync(CheckpointData checkpoint, CancellationToken cancellationToken = default)
		{
			LastSaved = checkpoint;
			return Task.CompletedTask;
		}

		public Task<CheckpointData?> LoadCheckpointAsync(string orchestrationName, string runId, CancellationToken cancellationToken = default)
			=> Task.FromResult<CheckpointData?>(null);

		public Task DeleteCheckpointAsync(string orchestrationName, string runId, CancellationToken cancellationToken = default)
			=> Task.CompletedTask;

		public Task<IReadOnlyList<CheckpointData>> ListCheckpointsAsync(string? orchestrationName = null, CancellationToken cancellationToken = default)
			=> Task.FromResult<IReadOnlyList<CheckpointData>>([]);
	}
}
