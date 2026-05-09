using FluentAssertions;

namespace Orchestra.Engine.Tests.Serialization;

/// <summary>
/// Verifies that the bundled HITL example orchestrations parse without errors.
/// </summary>
public class HitlExampleParsingTests
{
	[Fact]
	public void Parses_HitlApprovalDeployExample()
	{
		var path = Path.Combine(GetRepoRoot(), "examples", "hitl-approval-deploy.yaml");
		File.Exists(path).Should().BeTrue($"example file should exist at {path}");

		var orchestration = OrchestrationParser.ParseOrchestrationFile(path, []);

		orchestration.Steps.Should().HaveCount(3);
		orchestration.Steps.OfType<ApprovalOrchestrationStep>().Should().ContainSingle()
			.Which.Choices.Should().Equal("approve", "reject");
	}

	[Fact]
	public void Parses_HitlEngineToolClarifyExample()
	{
		var path = Path.Combine(GetRepoRoot(), "examples", "hitl-engine-tool-clarify.yaml");
		File.Exists(path).Should().BeTrue($"example file should exist at {path}");

		var orchestration = OrchestrationParser.ParseOrchestrationFile(path, []);

		orchestration.Steps.Should().ContainSingle();
		var promptStep = orchestration.Steps[0].Should().BeOfType<PromptOrchestrationStep>().Subject;
		promptStep.EnableTools.Should().NotBeNull();
		promptStep.EnableTools.Should().Contain("request_user_input");
	}

	private static string GetRepoRoot()
	{
		// Walk up until we find HITL-plan.md or the README
		var dir = AppContext.BaseDirectory;
		while (dir is not null)
		{
			if (File.Exists(Path.Combine(dir, "HITL-plan.md"))
				|| File.Exists(Path.Combine(dir, "README.md")) && Directory.Exists(Path.Combine(dir, "examples")))
			{
				return dir;
			}
			dir = Path.GetDirectoryName(dir);
		}
		throw new InvalidOperationException("Could not locate repo root from " + AppContext.BaseDirectory);
	}
}
