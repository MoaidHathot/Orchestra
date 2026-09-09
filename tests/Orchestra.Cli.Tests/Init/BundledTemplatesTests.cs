using FluentAssertions;
using Orchestra.Cli.Init;
using Orchestra.Engine;
using Xunit;

namespace Orchestra.Cli.Tests.Init;

/// <summary>
/// Contract tests for the starter orchestrations shipped with the tool.
/// </summary>
/// <remarks>
/// These are the very first Orchestra files a new user ever sees, so a template that no longer
/// parses (or references a step that was renamed) is worse than having no template at all. Each
/// one is put through the same parser and template-expression validator the executor runs before
/// a real orchestration starts, and the bundled files are checked to physically ship in the build
/// output that gets packed as the dotnet tool.
/// </remarks>
public sealed class BundledTemplatesTests
{
	private static string TemplatesDirectory => Path.Combine(AppContext.BaseDirectory, "templates");

	public static TheoryData<string> TemplateIds()
	{
		var data = new TheoryData<string>();
		foreach (var template in InitTemplateCatalog.Discover(TemplatesDirectory))
			data.Add(template.Id);
		return data;
	}

	[Fact]
	public void BuildOutput_ContainsTheTemplatesDirectory()
	{
		// Mirrors BundledSchemasTests: CopyToPublishDirectory is what lands these in
		// tools/net10.0/any/templates/ inside the packed tool.
		Directory.Exists(TemplatesDirectory)
			.Should().BeTrue($"`orchestra init` reads bundled templates from '{TemplatesDirectory}'.");
	}

	[Fact]
	public void Catalog_ExposesTheCuratedStarterTemplates()
	{
		var ids = InitTemplateCatalog.Discover(TemplatesDirectory).Select(t => t.Id).ToArray();

		ids.Should().Contain(["hello", "research", "code-review", "approval", "generate"]);
		ids[0].Should().Be(InitTemplateCatalog.DefaultTemplateId, "the default template must be offered first");
	}

	[Theory]
	[MemberData(nameof(TemplateIds))]
	public void Template_ParsesWithTheRuntimeParser(string id)
	{
		var act = () => Parse(id);

		act.Should().NotThrow($"template '{id}' is scaffolded by `orchestra init` and must be runnable as-is");
	}

	[Theory]
	[MemberData(nameof(TemplateIds))]
	public void Template_PassesTemplateExpressionValidation(string id)
	{
		// The same gate OrchestrationExecutor applies before a run: catches references to steps
		// or parameters that do not exist, which is exactly what a rename would break.
		var result = TemplateExpressionValidator.ValidateOrchestration(Parse(id));

		result.IsValid.Should().BeTrue($"template '{id}' has invalid expressions:\n{result.FormatErrors()}");
	}

	[Theory]
	[MemberData(nameof(TemplateIds))]
	public void Template_DeclaresNameDescriptionAndSteps(string id)
	{
		var orchestration = Parse(id);

		orchestration.Name.Should().Be(id, "the scaffolded file name and orchestration name must match so `orchestra run <id>` works");
		orchestration.Description.Should().NotBeNullOrWhiteSpace();
		orchestration.Steps.Should().NotBeEmpty();
	}

	[Theory]
	[MemberData(nameof(TemplateIds))]
	public void Template_RunsWithoutArguments(string id)
	{
		// Every starter must work on a bare `orchestra run <id>`, so no input may be required
		// without a default. This is the difference between "it just ran" and a usage error on
		// someone's very first command.
		var orchestration = Parse(id);

		foreach (var (name, definition) in orchestration.Inputs ?? [])
		{
			var satisfied = !definition.Required || definition.Default is not null;
			satisfied.Should().BeTrue($"input '{name}' of template '{id}' is required but has no default");
		}
	}

	[Theory]
	[MemberData(nameof(TemplateIds))]
	public void Template_StepDependenciesResolve(string id)
	{
		var orchestration = Parse(id);
		var names = orchestration.Steps.Select(s => s.Name).ToHashSet(StringComparer.Ordinal);

		foreach (var step in orchestration.Steps)
		{
			foreach (var dependency in step.DependsOn ?? [])
				names.Should().Contain(dependency, $"step '{step.Name}' of '{id}' depends on an unknown step");
		}
	}

	[Theory]
	[MemberData(nameof(TemplateIds))]
	public void Template_CarriesASchemaDirectiveForEditorValidation(string id)
	{
		var path = InitTemplateCatalog.Find(InitTemplateCatalog.Discover(TemplatesDirectory), id)!.SourcePath;

		var first = File.ReadLines(path).First();

		// InitScaffolder rewrites this line on scaffold; if it goes missing the scaffolded file
		// silently loses editor validation.
		first.Should().StartWith("# yaml-language-server: $schema=");
	}

	[Theory]
	[MemberData(nameof(TemplateIds))]
	public void Template_UsesTheRepositoryDefaultModel(string id)
	{
		var orchestration = Parse(id);

		orchestration.DefaultModel.Should().Be("claude-opus-4.8");
	}

	private static Orchestration Parse(string id)
	{
		var template = InitTemplateCatalog.Find(InitTemplateCatalog.Discover(TemplatesDirectory), id);
		template.Should().NotBeNull($"template '{id}' must ship in the build output");

		return OrchestrationParser.ParseOrchestrationFile(template!.SourcePath, availableMcps: []);
	}
}
