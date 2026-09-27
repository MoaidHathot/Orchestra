using FluentAssertions;
using Orchestra.Engine.Tests.TestHelpers;

namespace Orchestra.Engine.Tests;

/// <summary>
/// <see cref="RunPlan"/>: the executor's pre-flight plus the layered plan, without running.
/// </summary>
public class RunPlanTests
{
	private static Orchestration Diamond() => new()
	{
		Name = "diamond",
		Description = "a -> b, c -> d",
		Steps =
		[
			TestOrchestrations.CreatePromptStep("a"),
			TestOrchestrations.CreatePromptStep("b", dependsOn: ["a"]),
			TestOrchestrations.CreatePromptStep("c", dependsOn: ["a"]),
			TestOrchestrations.CreatePromptStep("d", dependsOn: ["b", "c"]),
		],
	};

	[Fact]
	public void Build_ValidOrchestration_YieldsLayersAndNoErrors()
	{
		var plan = RunPlan.Build(Diamond());

		plan.IsValid.Should().BeTrue(string.Join("; ", plan.Errors));
		plan.Layers.Select(l => l.Select(s => s.Name).OrderBy(n => n).ToArray())
			.Should().BeEquivalentTo(new[] { new[] { "a" }, new[] { "b", "c" }, new[] { "d" } }, o => o.WithStrictOrdering());
		plan.Warnings.Should().BeEmpty();
	}

	[Fact]
	public void Build_AppliesInputDefaults_AndReportsMissingRequired()
	{
		var orchestration = new Orchestration
		{
			Name = "inputs",
			Description = "d",
			Inputs = new Dictionary<string, InputDefinition>
			{
				["topic"] = new() { Required = false, Default = "things" },
				["owner"] = new() { Required = true },
			},
			Steps = [new TransformOrchestrationStep { Name = "t", Type = OrchestrationStepType.Transform, Template = "{{param.topic}} {{param.owner}}" }],
		};

		var missing = RunPlan.Build(orchestration);
		var supplied = RunPlan.Build(orchestration, new Dictionary<string, string> { ["owner"] = "me" });

		missing.IsValid.Should().BeFalse();
		missing.Errors.Should().ContainSingle().Which.Should().Contain("owner");
		supplied.IsValid.Should().BeTrue(string.Join("; ", supplied.Errors));
		supplied.EffectiveParameters.Should().Contain("topic", "things").And.Contain("owner", "me");
	}

	[Fact]
	public void Build_UndeclaredParameter_IsAWarningNotAnError()
	{
		var plan = RunPlan.Build(Diamond(), new Dictionary<string, string> { ["typo"] = "x" });

		plan.IsValid.Should().BeTrue();
		plan.Warnings.Should().ContainSingle().Which.Should().Contain("'typo'");
	}

	[Fact]
	public void Build_TemplateReferencingUnknownStep_IsAnError()
	{
		var orchestration = new Orchestration
		{
			Name = "bad-ref",
			Description = "d",
			Steps = [TestOrchestrations.CreatePromptStep("s", userPrompt: "{{nope.output}}")],
		};

		var plan = RunPlan.Build(orchestration);

		plan.IsValid.Should().BeFalse();
		plan.Errors.Should().Contain(e => e.Contains("nope"));
	}

	[Fact]
	public void Build_DependencyCycle_IsAnError_WithNoLayers()
	{
		var orchestration = new Orchestration
		{
			Name = "cycle",
			Description = "d",
			Steps =
			[
				TestOrchestrations.CreatePromptStep("a", dependsOn: ["b"]),
				TestOrchestrations.CreatePromptStep("b", dependsOn: ["a"]),
			],
		};

		var plan = RunPlan.Build(orchestration);

		plan.IsValid.Should().BeFalse();
		plan.Layers.Should().BeEmpty();
	}

	[Fact]
	public void Build_MissingEnvironmentVariable_IsCaughtByRuntimeValidation()
	{
		var name = $"ORCHESTRA_TEST_UNSET_{Guid.NewGuid():N}";
		var orchestration = new Orchestration
		{
			Name = "env",
			Description = "d",
			Steps = [new TransformOrchestrationStep { Name = "t", Type = OrchestrationStepType.Transform, Template = $"{{{{env.{name}}}}}" }],
		};

		var plan = RunPlan.Build(orchestration);

		plan.IsValid.Should().BeFalse();
		plan.Errors.Should().Contain(e => e.Contains(name));
	}
}
