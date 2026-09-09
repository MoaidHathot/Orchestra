using FluentAssertions;
using Orchestra.Cli.Init;
using Xunit;

namespace Orchestra.Cli.Tests.Init;

/// <summary>
/// Verifies the orchestration-authoring Agent Skill ships with the tool and that scaffolding a
/// template which references it actually produces a resolvable path.
/// </summary>
/// <remarks>
/// The skill is Orchestra's mechanism for making an agent write correct orchestrations, but it
/// used to reach nobody who installed from NuGet — no csproj referenced <c>skills/**</c>. And
/// because a missing <c>skillDirectories</c> entry is skipped silently at runtime, a broken
/// path produces no error at all, only worse output. Both halves need pinning.
/// </remarks>
public sealed class BundledSkillTests : IDisposable
{
	private readonly string _tempDir;

	public BundledSkillTests()
	{
		_tempDir = Path.Combine(Path.GetTempPath(), $"orchestra-skill-tests-{Guid.NewGuid():N}");
		Directory.CreateDirectory(_tempDir);
	}

	public void Dispose()
	{
		try { Directory.Delete(_tempDir, recursive: true); }
		catch { /* best-effort cleanup */ }
	}

	private static string SkillsDirectory => Path.Combine(AppContext.BaseDirectory, "skills");
	private static string TemplatesDirectory => Path.Combine(AppContext.BaseDirectory, "templates");
	private static string SchemasDirectory => Path.Combine(AppContext.BaseDirectory, "schemas");

	[Fact]
	public void BuildOutput_ContainsTheAuthoringSkill()
	{
		var skill = Path.Combine(SkillsDirectory, InitScaffolder.AuthoringSkillName, "SKILL.md");

		File.Exists(skill).Should().BeTrue(
			$"the authoring skill must ship with the tool so `orchestra init` can scaffold it (looked at '{skill}')");
	}

	[Fact]
	public void BuildOutput_ContainsTheSkillReferenceFiles()
	{
		var references = Path.Combine(SkillsDirectory, InitScaffolder.AuthoringSkillName, "references");

		Directory.Exists(references).Should().BeTrue();
		Directory.GetFiles(references, "*.md").Should().NotBeEmpty("the skill's reference material is part of the skill");
	}

	[Fact]
	public void Scaffold_WithSkill_CopiesTheWholeSkillTree()
	{
		var target = Path.Combine(_tempDir, "with-skill");

		InitScaffolder.Scaffold(PlanFor(target, "hello", withSkill: true), SchemasDirectory, SkillsDirectory);

		var skillRoot = Path.Combine(target, ".orchestra", "skills", InitScaffolder.AuthoringSkillName);
		File.Exists(Path.Combine(skillRoot, "SKILL.md")).Should().BeTrue();
		Directory.GetFiles(Path.Combine(skillRoot, "references"), "*.md").Should().NotBeEmpty();
	}

	[Fact]
	public void Scaffold_WithoutSkill_DoesNotCopyIt()
	{
		var target = Path.Combine(_tempDir, "no-skill");

		InitScaffolder.Scaffold(PlanFor(target, "hello", withSkill: false), SchemasDirectory, SkillsDirectory);

		Directory.Exists(Path.Combine(target, ".orchestra", "skills")).Should().BeFalse(
			"the skill is opt-in for templates that do not reference it");
	}

	[Fact]
	public void Scaffold_TemplateThatNeedsTheSkill_CopiesItEvenWithoutTheFlag()
	{
		// The `generate` template's skillDirectories point into .orchestra/skills. If the flag
		// were required, scaffolding it alone would produce an orchestration that silently
		// runs with no skill loaded.
		var target = Path.Combine(_tempDir, "auto-skill");

		InitScaffolder.Scaffold(PlanFor(target, "generate", withSkill: false), SchemasDirectory, SkillsDirectory);

		File.Exists(Path.Combine(target, ".orchestra", "skills", InitScaffolder.AuthoringSkillName, "SKILL.md"))
			.Should().BeTrue();
	}

	[Fact]
	public void Scaffold_GenerateTemplate_SkillDirectoryPathResolvesFromTheOrchestrationFile()
	{
		// This is the exact defect that made the repo's generate-orchestration.yaml useless:
		// skillDirectories resolve against the orchestration FILE's directory, so the relative
		// depth has to account for orchestrations/ being one level down.
		var target = Path.Combine(_tempDir, "resolve");

		var result = InitScaffolder.Scaffold(PlanFor(target, "generate", withSkill: false), SchemasDirectory, SkillsDirectory);

		var orchestrationDir = Path.GetDirectoryName(result.OrchestrationPath)!;
		var yaml = File.ReadAllText(result.OrchestrationPath);

		var referenced = System.Text.RegularExpressions.Regex
			.Matches(yaml, @"^\s*-\s*(\S*skills/orchestration-authoring)\s*$", System.Text.RegularExpressions.RegexOptions.Multiline)
			.Select(m => m.Groups[1].Value)
			.Distinct(StringComparer.Ordinal)
			.ToArray();

		referenced.Should().NotBeEmpty("the generate template is supposed to load the authoring skill");

		foreach (var relative in referenced)
		{
			var resolved = Path.GetFullPath(Path.Combine(orchestrationDir, relative));
			Directory.Exists(resolved).Should().BeTrue(
				$"'{relative}' must resolve from '{orchestrationDir}' — a missing skill directory is skipped silently at runtime");
		}
	}

	[Fact]
	public void Scaffold_IsIdempotentForTheSkillTree()
	{
		var target = Path.Combine(_tempDir, "twice");

		InitScaffolder.Scaffold(PlanFor(target, "generate", withSkill: false), SchemasDirectory, SkillsDirectory);
		var second = InitScaffolder.Scaffold(PlanFor(target, "generate", withSkill: false), SchemasDirectory, SkillsDirectory);

		second.WrittenCount.Should().Be(0);
		second.SkippedCount.Should().Be(second.Files.Count);
	}

	private static InitPlan PlanFor(string target, string templateId, bool withSkill)
	{
		var template = InitTemplateCatalog.Find(InitTemplateCatalog.Discover(TemplatesDirectory), templateId);
		template.Should().NotBeNull($"template '{templateId}' must ship in the build output");

		return new InitPlan(
			target,
			template!,
			Provider: "copilot",
			Model: "claude-opus-4.8",
			WriteConfig: true,
			WriteSchemas: true,
			Force: false,
			WriteSkill: withSkill);
	}
}
