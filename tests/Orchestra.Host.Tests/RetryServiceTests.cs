using FluentAssertions;
using Orchestra.Engine;
using Orchestra.Host.Services;
using Xunit;

namespace Orchestra.Host.Tests;

/// <summary>
/// Unit tests for <see cref="RetryService"/> — checkpoint reconstruction and
/// downstream-closure DAG math used by the retry-execution endpoints.
/// </summary>
public class RetryServiceTests
{
	private static PromptOrchestrationStep Step(string name, params string[] deps) => new()
	{
		Name = name,
		Type = OrchestrationStepType.Prompt,
		DependsOn = deps,
		Parameters = [],
		SystemPrompt = "",
		UserPrompt = "",
		Model = "claude-opus-4.6",
	};

	private static Orchestration Orch(params OrchestrationStep[] steps) => new()
	{
		Name = "retry-svc-test",
		Description = "RetryService DAG fixture",
		Steps = steps,
	};

	private static OrchestrationRunRecord Run(IDictionary<string, ExecutionStatus> stepStatuses, IDictionary<string, string>? stepContent = null)
	{
		var records = stepStatuses.ToDictionary(
			kv => kv.Key,
			kv => new StepRunRecord
			{
				StepName = kv.Key,
				Status = kv.Value,
				StartedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
				CompletedAt = DateTimeOffset.UtcNow,
				Content = stepContent is not null && stepContent.TryGetValue(kv.Key, out var c) ? c : $"output-{kv.Key}",
			});

		return new OrchestrationRunRecord
		{
			RunId = "src000000000",
			OrchestrationName = "retry-svc-test",
			StartedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
			CompletedAt = DateTimeOffset.UtcNow,
			Status = ExecutionStatus.Failed,
			Parameters = new Dictionary<string, string> { ["foo"] = "bar" },
			StepRecords = records,
			AllStepRecords = records,
			FinalContent = "",
		};
	}

	#region ComputeDownstreamClosure

	[Fact]
	public void ComputeDownstreamClosure_LinearChain_IncludesRootAndAllDescendants()
	{
		var orch = Orch(Step("a"), Step("b", "a"), Step("c", "b"), Step("d", "c"));

		var closure = RetryService.ComputeDownstreamClosure(orch, "b");

		closure.Should().BeEquivalentTo(["b", "c", "d"]);
	}

	[Fact]
	public void ComputeDownstreamClosure_FanOut_IncludesAllBranches()
	{
		// a -> b, a -> c, b -> d, c -> d
		var orch = Orch(Step("a"), Step("b", "a"), Step("c", "a"), Step("d", "b", "c"));

		var closure = RetryService.ComputeDownstreamClosure(orch, "a");

		closure.Should().BeEquivalentTo(["a", "b", "c", "d"]);
	}

	[Fact]
	public void ComputeDownstreamClosure_LeafStep_OnlyIncludesItself()
	{
		var orch = Orch(Step("a"), Step("b", "a"), Step("c", "b"));

		var closure = RetryService.ComputeDownstreamClosure(orch, "c");

		closure.Should().BeEquivalentTo(["c"]);
	}

	[Fact]
	public void ComputeDownstreamClosure_DiamondGraph_DoesNotDoubleCount()
	{
		// a -> b, a -> c, b -> d, c -> d
		var orch = Orch(Step("a"), Step("b", "a"), Step("c", "a"), Step("d", "b", "c"));

		var closure = RetryService.ComputeDownstreamClosure(orch, "b");

		closure.Should().BeEquivalentTo(["b", "d"]);
	}

	#endregion

	#region ComputeStepsToRerun

	[Fact]
	public void ComputeStepsToRerun_AllMode_ReturnsEverything()
	{
		var orch = Orch(Step("a"), Step("b", "a"));
		var run = Run(new Dictionary<string, ExecutionStatus>
		{
			["a"] = ExecutionStatus.Succeeded,
			["b"] = ExecutionStatus.Succeeded,
		});

		var steps = RetryService.ComputeStepsToRerun(orch, run, RetryMode.All);

		steps.Should().BeEquivalentTo(["a", "b"]);
	}

	[Fact]
	public void ComputeStepsToRerun_FailedMode_OnlyNonSucceededSteps()
	{
		var orch = Orch(Step("a"), Step("b", "a"), Step("c", "b"));
		var run = Run(new Dictionary<string, ExecutionStatus>
		{
			["a"] = ExecutionStatus.Succeeded,
			["b"] = ExecutionStatus.Failed,
			["c"] = ExecutionStatus.Skipped,
		});

		var steps = RetryService.ComputeStepsToRerun(orch, run, RetryMode.Failed);

		steps.Should().BeEquivalentTo(["b", "c"]);
	}

	[Fact]
	public void ComputeStepsToRerun_FailedMode_StepMissingFromRun_IsTreatedAsRequiringExecution()
	{
		// Orchestration has been edited since the run — a new step "c" was added.
		var orch = Orch(Step("a"), Step("b", "a"), Step("c", "b"));
		var run = Run(new Dictionary<string, ExecutionStatus>
		{
			["a"] = ExecutionStatus.Succeeded,
			["b"] = ExecutionStatus.Succeeded,
		});

		var steps = RetryService.ComputeStepsToRerun(orch, run, RetryMode.Failed);

		steps.Should().BeEquivalentTo(["c"]);
	}

	[Fact]
	public void ComputeStepsToRerun_FromStep_IncludesTargetAndDownstream()
	{
		var orch = Orch(Step("a"), Step("b", "a"), Step("c", "b"));
		var run = Run(new Dictionary<string, ExecutionStatus>
		{
			["a"] = ExecutionStatus.Succeeded,
			["b"] = ExecutionStatus.Succeeded,
			["c"] = ExecutionStatus.Succeeded,
		});

		var steps = RetryService.ComputeStepsToRerun(orch, run, RetryMode.FromStep, fromStep: "b");

		steps.Should().BeEquivalentTo(["b", "c"]);
	}

	[Fact]
	public void ComputeStepsToRerun_FromStep_TargetMissing_Throws()
	{
		var orch = Orch(Step("a"));
		var run = Run(new Dictionary<string, ExecutionStatus> { ["a"] = ExecutionStatus.Succeeded });

		var act = () => RetryService.ComputeStepsToRerun(orch, run, RetryMode.FromStep, fromStep: "missing");

		act.Should().Throw<InvalidOperationException>().WithMessage("*missing*");
	}

	[Fact]
	public void ComputeStepsToRerun_FromStep_NullTarget_Throws()
	{
		var orch = Orch(Step("a"));
		var run = Run(new Dictionary<string, ExecutionStatus> { ["a"] = ExecutionStatus.Succeeded });

		var act = () => RetryService.ComputeStepsToRerun(orch, run, RetryMode.FromStep, fromStep: null);

		act.Should().Throw<ArgumentException>();
	}

	#endregion

	#region BuildCheckpoint

	[Fact]
	public void BuildCheckpoint_AllMode_ReturnsNull()
	{
		var orch = Orch(Step("a"));
		var run = Run(new Dictionary<string, ExecutionStatus> { ["a"] = ExecutionStatus.Succeeded });

		var cp = RetryService.BuildCheckpoint(orch, run, RetryMode.All, "newrun123456", DateTimeOffset.UtcNow);

		cp.Should().BeNull();
	}

	[Fact]
	public void BuildCheckpoint_FailedMode_RestoresOnlySucceededStepsNotScheduledForRerun()
	{
		var orch = Orch(Step("a"), Step("b", "a"), Step("c", "b"));
		var run = Run(
			new Dictionary<string, ExecutionStatus>
			{
				["a"] = ExecutionStatus.Succeeded,
				["b"] = ExecutionStatus.Failed,
				["c"] = ExecutionStatus.Skipped,
			},
			new Dictionary<string, string>
			{
				["a"] = "a-output",
				["b"] = "[Failed]",
				["c"] = "[Skipped]",
			});

		var cp = RetryService.BuildCheckpoint(orch, run, RetryMode.Failed, "newrun123456", DateTimeOffset.UtcNow);

		cp.Should().NotBeNull();
		cp!.RunId.Should().Be("newrun123456");
		cp.OrchestrationName.Should().Be("retry-svc-test");
		cp.Parameters.Should().ContainKey("foo").WhoseValue.Should().Be("bar");
		cp.CompletedSteps.Should().HaveCount(1);
		cp.CompletedSteps.Should().ContainKey("a");
		cp.CompletedSteps["a"].Status.Should().Be(ExecutionStatus.Succeeded);
		cp.CompletedSteps["a"].Content.Should().Be("a-output");
	}

	[Fact]
	public void BuildCheckpoint_FromStep_ExcludesTargetAndDescendants_RestoresUpstream()
	{
		// a -> b -> c -> d, all succeeded; user wants to retry from c
		var orch = Orch(Step("a"), Step("b", "a"), Step("c", "b"), Step("d", "c"));
		var run = Run(new Dictionary<string, ExecutionStatus>
		{
			["a"] = ExecutionStatus.Succeeded,
			["b"] = ExecutionStatus.Succeeded,
			["c"] = ExecutionStatus.Succeeded,
			["d"] = ExecutionStatus.Succeeded,
		});

		var cp = RetryService.BuildCheckpoint(orch, run, RetryMode.FromStep, "newrun", DateTimeOffset.UtcNow, fromStep: "c");

		cp.Should().NotBeNull();
		cp!.CompletedSteps.Keys.Should().BeEquivalentTo(["a", "b"], "c and d are scheduled for re-execution");
	}

	[Fact]
	public void BuildCheckpoint_DropsSucceededStepsNoLongerInOrchestration()
	{
		// Original run had step "removed" succeed; orchestration no longer has it.
		var orch = Orch(Step("a"));
		var run = Run(new Dictionary<string, ExecutionStatus>
		{
			["a"] = ExecutionStatus.Succeeded,
			["removed"] = ExecutionStatus.Succeeded,
		});

		var cp = RetryService.BuildCheckpoint(orch, run, RetryMode.FromStep, "newrun", DateTimeOffset.UtcNow, fromStep: "a");

		cp.Should().NotBeNull();
		cp!.CompletedSteps.Should().NotContainKey("removed");
		// "a" is the rerun target so it is also excluded.
		cp.CompletedSteps.Should().BeEmpty();
	}

	#endregion

	#region TryParseMode / FormatRetryMode

	[Theory]
	[InlineData("failed", RetryMode.Failed)]
	[InlineData("FAILED", RetryMode.Failed)]
	[InlineData("all", RetryMode.All)]
	[InlineData("from-step", RetryMode.FromStep)]
	[InlineData("FromStep", RetryMode.FromStep)]
	public void TryParseMode_KnownValues(string raw, RetryMode expected)
	{
		RetryService.TryParseMode(raw, out var mode).Should().BeTrue();
		mode.Should().Be(expected);
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("garbage")]
	public void TryParseMode_UnknownValues_ReturnFalse(string? raw)
	{
		RetryService.TryParseMode(raw, out _).Should().BeFalse();
	}

	[Fact]
	public void FormatRetryMode_FromStep_IncludesStepName()
	{
		RetryService.FormatRetryMode(RetryMode.FromStep, "my-step").Should().Be("from-step:my-step");
		RetryService.FormatRetryMode(RetryMode.Failed).Should().Be("failed");
		RetryService.FormatRetryMode(RetryMode.All).Should().Be("all");
	}

	#endregion
}
