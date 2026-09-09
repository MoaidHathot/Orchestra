using FluentAssertions;
using Orchestra.Cli.Init;
using Orchestra.Engine;
using Xunit;

namespace Orchestra.Cli.Tests.Init;

/// <summary>
/// Tests for <see cref="InitScaffolder.NewOrchestration"/>, the deterministic half of
/// <c>orchestra new</c>.
/// </summary>
/// <remarks>
/// The output is put through the real parser and template-expression validator rather than
/// string-matched: the rename is done with regexes over YAML that carries comments and folded
/// blocks, and the only property that matters is that the result still runs.
/// </remarks>
public sealed class NewOrchestrationTests : IDisposable
{
	private readonly string _root;

	public NewOrchestrationTests()
	{
		_root = Path.Combine(Path.GetTempPath(), $"orchestra-new-tests-{Guid.NewGuid():N}");
		Directory.CreateDirectory(_root);
	}

	public void Dispose()
	{
		try { Directory.Delete(_root, recursive: true); }
		catch { /* best-effort cleanup */ }
	}

	private static string TemplatesDirectory => Path.Combine(AppContext.BaseDirectory, "templates");
	private static string SkillsDirectory => Path.Combine(AppContext.BaseDirectory, "skills");

	private static InitTemplate Template(string id)
	{
		var template = InitTemplateCatalog.Find(InitTemplateCatalog.Discover(TemplatesDirectory), id);
		template.Should().NotBeNull($"template '{id}' must ship in the build output");
		return template!;
	}

	public static TheoryData<string> TemplateIds()
	{
		var data = new TheoryData<string>();
		foreach (var t in InitTemplateCatalog.Discover(TemplatesDirectory))
			data.Add(t.Id);
		return data;
	}

	[Theory]
	[MemberData(nameof(TemplateIds))]
	public void NewOrchestration_FromEveryTemplate_ParsesAndValidates(string templateId)
	{
		var workspace = Path.Combine(_root, templateId);

		var result = InitScaffolder.NewOrchestration(workspace, "my-thing", Template(templateId), force: false, SkillsDirectory);

		var orchestration = OrchestrationParser.ParseOrchestrationFile(result.OrchestrationPath, availableMcps: []);
		orchestration.Name.Should().Be("my-thing", "the copy must be independently runnable by its new name");

		var validation = TemplateExpressionValidator.ValidateOrchestration(orchestration);
		validation.IsValid.Should().BeTrue($"renamed '{templateId}' has invalid expressions:\n{validation.FormatErrors()}");
	}

	[Fact]
	public void NewOrchestration_WritesUnderOrchestrationsDirectory()
	{
		var workspace = Path.Combine(_root, "layout");

		var result = InitScaffolder.NewOrchestration(workspace, "nightly-digest", Template("hello"), force: false);

		result.OrchestrationPath.Should().Be(
			Path.Combine(workspace, InitScaffolder.OrchestrationsDirectoryName, "nightly-digest.yaml"),
			"the host scans <root>/orchestrations, so the file must land exactly there");
	}

	[Fact]
	public void NewOrchestration_ReplacesTheDescription()
	{
		// A copied description still describing the template would mislead `orchestra list`.
		var workspace = Path.Combine(_root, "description");

		var result = InitScaffolder.NewOrchestration(workspace, "nightly-digest", Template("research"), force: false);

		var orchestration = OrchestrationParser.ParseOrchestrationFile(result.OrchestrationPath, availableMcps: []);
		orchestration.Description.Should().Contain("nightly-digest");
		orchestration.Description.Should().Contain("research", "the user should be able to tell which template they started from");
		orchestration.Description.Should().NotContain("Parallel research pipeline", "the template's own description must not survive the copy");
	}

	[Fact]
	public void NewOrchestration_KeepsTheTemplatesTeachingComments()
	{
		// The comments are the whole point of starting from a template rather than a blank file.
		var workspace = Path.Combine(_root, "comments");

		var result = InitScaffolder.NewOrchestration(workspace, "nightly-digest", Template("hello"), force: false);

		File.ReadAllText(result.OrchestrationPath).Should().Contain("`dependsOn` is what builds the DAG");
	}

	[Fact]
	public void NewOrchestration_PointsTheSchemaDirectiveAtLocalSchemasWhenPresent()
	{
		var workspace = Path.Combine(_root, "local-schema");
		var schemas = Path.Combine(workspace, ".orchestra", "schemas");
		Directory.CreateDirectory(schemas);
		File.WriteAllText(Path.Combine(schemas, "orchestration.schema.json"), "{}");

		var result = InitScaffolder.NewOrchestration(workspace, "x", Template("hello"), force: false);

		File.ReadLines(result.OrchestrationPath).First()
			.Should().Be("# yaml-language-server: $schema=../.orchestra/schemas/orchestration.schema.json");
	}

	[Fact]
	public void NewOrchestration_FallsBackToTheRemoteSchemaOutsideAWorkspace()
	{
		var workspace = Path.Combine(_root, "remote-schema");

		var result = InitScaffolder.NewOrchestration(workspace, "x", Template("hello"), force: false);

		File.ReadLines(result.OrchestrationPath).First()
			.Should().Be($"# yaml-language-server: $schema={InitScaffolder.RemoteSchemaBaseUrl}/orchestration.schema.json");
	}

	[Fact]
	public void NewOrchestration_DoesNotOverwriteWithoutForce()
	{
		var workspace = Path.Combine(_root, "preserve");
		InitScaffolder.NewOrchestration(workspace, "x", Template("hello"), force: false);
		File.WriteAllText(Path.Combine(workspace, "orchestrations", "x.yaml"), "MY EDITS");

		var second = InitScaffolder.NewOrchestration(workspace, "x", Template("hello"), force: false);

		second.Files.Last().Outcome.Should().Be(InitFileOutcome.Skipped);
		File.ReadAllText(second.OrchestrationPath).Should().Be("MY EDITS");
	}

	[Fact]
	public void NewOrchestration_WithForce_Overwrites()
	{
		var workspace = Path.Combine(_root, "forced");
		InitScaffolder.NewOrchestration(workspace, "x", Template("hello"), force: false);
		File.WriteAllText(Path.Combine(workspace, "orchestrations", "x.yaml"), "MY EDITS");

		var second = InitScaffolder.NewOrchestration(workspace, "x", Template("hello"), force: true);

		second.Files.Last().Outcome.Should().Be(InitFileOutcome.Written);
		File.ReadAllText(second.OrchestrationPath).Should().NotBe("MY EDITS");
	}

	[Fact]
	public void NewOrchestration_GenerateTemplate_CopiesTheSkillItDependsOn()
	{
		// generate's skillDirectories point at .orchestra/skills; a missing directory is skipped
		// silently at runtime, so the copy has to be made here or the orchestration degrades.
		var workspace = Path.Combine(_root, "skill");

		var result = InitScaffolder.NewOrchestration(workspace, "author", Template("generate"), force: false, SkillsDirectory);

		var skill = Path.Combine(workspace, ".orchestra", "skills", InitScaffolder.AuthoringSkillName, "SKILL.md");
		File.Exists(skill).Should().BeTrue();

		// And the relative path inside the file must actually resolve from orchestrations/.
		var orchestrationDir = Path.GetDirectoryName(result.OrchestrationPath)!;
		var referenced = File.ReadLines(result.OrchestrationPath)
			.Select(l => l.Trim())
			.Where(l => l.StartsWith("- ../", StringComparison.Ordinal) && l.Contains("orchestration-authoring"))
			.Select(l => l[2..])
			.Distinct()
			.ToArray();

		referenced.Should().NotBeEmpty();
		foreach (var rel in referenced)
			Directory.Exists(Path.GetFullPath(Path.Combine(orchestrationDir, rel))).Should().BeTrue($"'{rel}' must resolve from the orchestration file");
	}

	[Theory]
	[InlineData("My Thing")]
	[InlineData("UPPER")]
	[InlineData("has_underscore")]
	[InlineData("double--hyphen")]
	[InlineData("-leading")]
	[InlineData("trailing-")]
	[InlineData("../escape")]
	public void NewOrchestration_RejectsNamesThatAreNotKebabCase(string name)
	{
		var act = () => InitScaffolder.NewOrchestration(Path.Combine(_root, "bad"), name, Template("hello"), force: false);

		act.Should().Throw<ArgumentException>().WithMessage("*kebab-case*");
	}

	[Theory]
	[InlineData("hello")]
	[InlineData("nightly-digest")]
	[InlineData("pr-review-v2")]
	[InlineData("a1")]
	public void OrchestrationNamePattern_AcceptsKebabCase(string name)
		=> InitScaffolder.OrchestrationNamePattern.IsMatch(name).Should().BeTrue();
}
