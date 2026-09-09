using FluentAssertions;
using Orchestra.Cli.Init;
using Orchestra.Host.Hosting;
using Xunit;

namespace Orchestra.Cli.Tests.Init;

/// <summary>
/// Unit tests for <see cref="InitScaffolder"/> — the deterministic half of <c>orchestra init</c>.
/// </summary>
/// <remarks>
/// Shares the environment collection because <see cref="BuildConfig_ProducesConfigTheLoaderCanParse"/>
/// round-trips through the real loader, which means setting <c>ORCHESTRA_CONFIG_PATH</c>.
/// </remarks>
[Collection(OrchestraEnvironmentCollection.Name)]
public sealed class InitScaffolderTests : IDisposable
{
	private readonly string _root;
	private readonly string _templatesDir;
	private readonly string _schemasDir;

	public InitScaffolderTests()
	{
		_root = Path.Combine(Path.GetTempPath(), $"orchestra-init-tests-{Guid.NewGuid():N}");
		_templatesDir = Path.Combine(_root, "bundled-templates");
		_schemasDir = Path.Combine(_root, "bundled-schemas");

		Directory.CreateDirectory(_templatesDir);
		Directory.CreateDirectory(_schemasDir);

		File.WriteAllText(
			Path.Combine(_templatesDir, "hello.yaml"),
			"""
			# yaml-language-server: $schema=../schemas/orchestration.schema.json
			name: hello
			description: Test template.
			steps:
			  - name: only
			    type: Transform
			    template: hi

			""");

		foreach (var schema in new[]
		{
			"orchestration.schema.json",
			"orchestra.mcp.schema.json",
			"orchestra.services.schema.json",
		})
		{
			File.WriteAllText(Path.Combine(_schemasDir, schema), "{}");
		}
	}

	public void Dispose()
	{
		try { Directory.Delete(_root, recursive: true); }
		catch { /* best-effort cleanup */ }
	}

	private InitPlan PlanFor(string target, bool writeConfig = true, bool writeSchemas = true, bool force = false)
	{
		var template = InitTemplateCatalog.Discover(_templatesDir).Single();
		return new InitPlan(target, template, "copilot", "claude-opus-4.8", writeConfig, writeSchemas, force);
	}

	[Fact]
	public void Scaffold_CreatesOrchestrationSchemasAndConfig()
	{
		var target = Path.Combine(_root, "workspace");

		var result = InitScaffolder.Scaffold(PlanFor(target), _schemasDir);

		File.Exists(Path.Combine(target, "orchestrations", "hello.yaml")).Should().BeTrue();
		File.Exists(Path.Combine(target, OrchestraConfigLoader.ConfigFileName)).Should().BeTrue();
		File.Exists(Path.Combine(target, ".orchestra", "schemas", "orchestration.schema.json")).Should().BeTrue();
		File.Exists(Path.Combine(target, ".orchestra", ".gitignore")).Should().BeTrue();

		result.SkippedCount.Should().Be(0);
		result.OrchestrationName.Should().Be("hello");
	}

	[Fact]
	public void Scaffold_PlacesOrchestrationInsideScanRootsOrchestrationsSubdirectory()
	{
		// The host scans <scan.directory>/orchestrations, so the file must be exactly one level
		// under the workspace root or `orchestra run <name>` will never resolve.
		var target = Path.Combine(_root, "layout");

		var result = InitScaffolder.Scaffold(PlanFor(target), _schemasDir);

		Path.GetDirectoryName(result.OrchestrationPath)
			.Should().Be(Path.Combine(target, InitScaffolder.OrchestrationsDirectoryName));
	}

	[Fact]
	public void Scaffold_IsIdempotent_SecondRunSkipsEverything()
	{
		var target = Path.Combine(_root, "twice");

		InitScaffolder.Scaffold(PlanFor(target), _schemasDir);
		var second = InitScaffolder.Scaffold(PlanFor(target), _schemasDir);

		second.WrittenCount.Should().Be(0);
		second.SkippedCount.Should().Be(second.Files.Count);
	}

	[Fact]
	public void Scaffold_DoesNotClobberExistingFilesWithoutForce()
	{
		var target = Path.Combine(_root, "preserve");
		InitScaffolder.Scaffold(PlanFor(target), _schemasDir);

		var orchestration = Path.Combine(target, "orchestrations", "hello.yaml");
		File.WriteAllText(orchestration, "MY EDITS");

		InitScaffolder.Scaffold(PlanFor(target), _schemasDir);

		File.ReadAllText(orchestration).Should().Be("MY EDITS");
	}

	[Fact]
	public void Scaffold_WithForce_OverwritesExistingFiles()
	{
		var target = Path.Combine(_root, "forced");
		InitScaffolder.Scaffold(PlanFor(target), _schemasDir);

		var orchestration = Path.Combine(target, "orchestrations", "hello.yaml");
		File.WriteAllText(orchestration, "MY EDITS");

		var result = InitScaffolder.Scaffold(PlanFor(target, force: true), _schemasDir);

		File.ReadAllText(orchestration).Should().NotBe("MY EDITS");
		result.SkippedCount.Should().Be(0);
	}

	[Fact]
	public void Scaffold_NoConfig_SkipsOrchestraJson()
	{
		var target = Path.Combine(_root, "no-config");

		InitScaffolder.Scaffold(PlanFor(target, writeConfig: false), _schemasDir);

		File.Exists(Path.Combine(target, OrchestraConfigLoader.ConfigFileName)).Should().BeFalse();
		File.Exists(Path.Combine(target, "orchestrations", "hello.yaml")).Should().BeTrue();
	}

	// ── Schema directive rewriting ──

	[Fact]
	public void Scaffold_RewritesSchemaDirectiveToLocalSchemasWhenCopied()
	{
		var target = Path.Combine(_root, "local-schema");

		InitScaffolder.Scaffold(PlanFor(target), _schemasDir);

		var first = File.ReadLines(Path.Combine(target, "orchestrations", "hello.yaml")).First();
		first.Should().Be("# yaml-language-server: $schema=../.orchestra/schemas/orchestration.schema.json");
	}

	[Fact]
	public void Scaffold_UsesRemoteSchemaUrlWhenSchemasAreNotCopied()
	{
		var target = Path.Combine(_root, "remote-schema");

		InitScaffolder.Scaffold(PlanFor(target, writeSchemas: false), _schemasDir);

		var first = File.ReadLines(Path.Combine(target, "orchestrations", "hello.yaml")).First();
		first.Should().Be($"# yaml-language-server: $schema={InitScaffolder.RemoteSchemaBaseUrl}/orchestration.schema.json");
	}

	[Fact]
	public void RewriteSchemaDirective_PrependsDirectiveWhenTemplateHasNone()
	{
		var rewritten = InitScaffolder.RewriteSchemaDirective("name: x\n", schemasCopied: true);

		rewritten.Should().StartWith("# yaml-language-server: $schema=../.orchestra/schemas/orchestration.schema.json");
		rewritten.Should().Contain("name: x");
	}

	[Fact]
	public void RewriteSchemaDirective_ReplacesOnlyTheFirstDirective()
	{
		var yaml = "# yaml-language-server: $schema=a\nname: x\n# yaml-language-server: $schema=b\n";

		var rewritten = InitScaffolder.RewriteSchemaDirective(yaml, schemasCopied: true);

		rewritten.Should().Contain("$schema=../.orchestra/schemas/orchestration.schema.json");
		rewritten.Should().Contain("$schema=b", "only the leading directive is authoritative");
	}

	// ── Generated orchestra.json ──

	[Fact]
	public void BuildConfig_ProducesConfigTheLoaderCanParse()
	{
		var target = Path.Combine(_root, "parseable");
		InitScaffolder.Scaffold(PlanFor(target), _schemasDir);

		// Round-trip through the real loader: JSONC comments, trailing commas, and the exact
		// property names all have to line up, which a hand-rolled writer can easily get wrong.
		Environment.SetEnvironmentVariable("ORCHESTRA_CONFIG_PATH", Path.Combine(target, OrchestraConfigLoader.ConfigFileName));
		try
		{
			var config = OrchestraConfigLoader.Load();

			config.Should().NotBeNull();
			config!.Scan.Should().NotBeNull();
			config.Scan!.Directory.Should().Be(".");
			config.Scan.Recursive.Should().BeTrue();
			config.DataPath.Should().Be("./.orchestra/data");
			config.DefaultProvider.Should().Be("copilot");
			config.DefaultModel.Should().Be("claude-opus-4.8");
		}
		finally
		{
			Environment.SetEnvironmentVariable("ORCHESTRA_CONFIG_PATH", null);
		}
	}

	[Fact]
	public void BuildConfig_ScanDirectoryIsWorkspaceRootNotOrchestrationsFolder()
	{
		// Regression guard: pointing scan.directory at ./orchestrations makes the host look for
		// ./orchestrations/orchestrations and silently register nothing.
		var plan = PlanFor(Path.Combine(_root, "scan-root"));

		var json = InitScaffolder.BuildConfig(plan);

		json.Should().Contain("\"directory\": \".\"");
		json.Should().NotContain("\"directory\": \"./orchestrations\"");
	}

	[Fact]
	public void BuildConfig_WithoutProviderOrModel_IsStillValidJson()
	{
		var template = InitTemplateCatalog.Discover(_templatesDir).Single();
		var plan = new InitPlan(Path.Combine(_root, "bare"), template, null, null, true, true, false);

		var json = InitScaffolder.BuildConfig(plan);

		var act = () => System.Text.Json.JsonDocument.Parse(
			json,
			new System.Text.Json.JsonDocumentOptions
			{
				CommentHandling = System.Text.Json.JsonCommentHandling.Skip,
				AllowTrailingCommas = true,
			});

		act.Should().NotThrow();
	}

	[Fact]
	public void BuildConfig_IsAsciiOnly()
	{
		// Generated config is meant to be hand-edited; non-ASCII risks mojibake in terminals or
		// editors that guess a legacy codepage.
		var json = InitScaffolder.BuildConfig(PlanFor(Path.Combine(_root, "ascii")));

		json.Where(c => c > 127).Should().BeEmpty();
	}
}
