using FluentAssertions;
using Orchestra.Engine;
using Xunit;

namespace Orchestra.Tool.Tests;

/// <summary>
/// Every shipped example and template must be something a new user can copy and run: it has
/// to parse, pass the same template-expression validation <c>orchestra validate</c> applies,
/// and use the repository's default model rather than a retired one.
/// </summary>
/// <remarks>
/// The model rule is enforced here because it drifted silently for a long time: 39 example
/// files and most of the playground orchestrations referenced <c>claude-opus-4.5</c> /
/// <c>claude-opus-4.6</c> / <c>gpt-4o</c> after the default moved to
/// <see cref="ModelDefaults.Model"/>. A test is the only thing that keeps a sweep from decaying.
/// </remarks>
public class ExamplesContractTests
{
	/// <summary>
	/// Model identifiers that must not appear anywhere in shipped examples, templates or the
	/// authoring skill. Extend when a model is retired.
	/// </summary>
	private static readonly string[] s_retiredModels =
	[
		"claude-opus-4.5",
		"claude-opus-4.6",
		"claude-opus-4.6-fast",
		"claude-opus-4.7",
		"claude-sonnet-4\"",   // bare claude-sonnet-4 (not 4.5) — the quote avoids matching 4.5
		"gpt-4o",
		"gpt-5.4",
	];

	public static TheoryData<string> ExampleAndTemplateFiles()
	{
		var data = new TheoryData<string>();
		foreach (var file in EnumerateOrchestrationFiles())
			data.Add(Path.GetRelativePath(RepoRoot(), file));
		return data;
	}

	[Theory]
	[MemberData(nameof(ExampleAndTemplateFiles))]
	public void Example_ParsesAndPassesTemplateValidation(string relativePath)
	{
		var path = Path.Combine(RepoRoot(), relativePath);

		Orchestration orchestration;
		try
		{
			orchestration = OrchestrationParser.ParseOrchestrationFile(path, availableMcps: []);
		}
		catch (Exception ex)
		{
			throw new Xunit.Sdk.XunitException($"'{relativePath}' failed to parse: {ex.Message}");
		}

		var result = TemplateExpressionValidator.ValidateOrchestration(orchestration);
		result.IsValid.Should().BeTrue(
			$"'{relativePath}' must pass `orchestra validate`:\n  - {string.Join("\n  - ", result.Errors.Select(e => e.Message))}");
	}

	[Theory]
	[MemberData(nameof(ExampleAndTemplateFiles))]
	public void Example_DoesNotReferenceARetiredModel(string relativePath)
	{
		var content = File.ReadAllText(Path.Combine(RepoRoot(), relativePath));

		foreach (var retired in s_retiredModels)
		{
			content.Should().NotContain(retired,
				$"'{relativePath}' references retired model '{retired.TrimEnd('"')}'; the repository default is '{ModelDefaults.Model}'");
		}
	}

	[Fact]
	public void AuthoringSkill_DoesNotReferenceARetiredModel()
	{
		var skillDir = Path.Combine(RepoRoot(), "skills", "orchestration-authoring");
		var files = Directory.GetFiles(skillDir, "*.md", SearchOption.AllDirectories);
		files.Should().NotBeEmpty();

		foreach (var file in files)
		{
			var content = File.ReadAllText(file);
			foreach (var retired in s_retiredModels)
			{
				content.Should().NotContain(retired,
					$"'{Path.GetRelativePath(RepoRoot(), file)}' references retired model '{retired.TrimEnd('"')}'");
			}
		}
	}

	[Fact]
	public void EngineDefaultModel_IsTheRepositoryDefault()
	{
		// AGENTS.md: "When creating orchestration examples, the default model should be claude-opus-4.8".
		ModelDefaults.Model.Should().Be("claude-opus-4.8");
	}

	private static IEnumerable<string> EnumerateOrchestrationFiles()
	{
		var root = RepoRoot();
		var roots = new[]
		{
			Path.Combine(root, "examples"),
			Path.Combine(root, "templates"),
		};

		foreach (var dir in roots)
		{
			if (!Directory.Exists(dir))
				continue;

			foreach (var file in Directory.EnumerateFiles(dir, "*.*", SearchOption.AllDirectories)
				.Where(f => f.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
					|| f.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase)
					|| f.EndsWith(".yml", StringComparison.OrdinalIgnoreCase))
				.Where(IsOrchestrationFile)
				.OrderBy(f => f, StringComparer.Ordinal))
			{
				yield return file;
			}
		}
	}

	/// <summary>
	/// The examples folder also holds profile definitions and MCP/config fixtures. Only files
	/// with a top-level <c>steps</c> array are orchestrations.
	/// </summary>
	private static bool IsOrchestrationFile(string path)
	{
		try
		{
			var text = File.ReadAllText(path);
			if (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
			{
				using var doc = System.Text.Json.JsonDocument.Parse(text, new System.Text.Json.JsonDocumentOptions { CommentHandling = System.Text.Json.JsonCommentHandling.Skip, AllowTrailingCommas = true });
				return doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
					&& doc.RootElement.TryGetProperty("steps", out _);
			}

			// YAML: a top-level `steps:` key at column 0.
			return text.Split('\n').Any(l => l.StartsWith("steps:", StringComparison.Ordinal));
		}
		catch
		{
			// Unparseable JSON is still "an orchestration candidate" so the parse test reports it.
			return true;
		}
	}

	private static string RepoRoot()
	{
		var current = new DirectoryInfo(AppContext.BaseDirectory);
		while (current is not null)
		{
			if (File.Exists(Path.Combine(current.FullName, "OrchestrationEngine.slnx")))
				return current.FullName;
			current = current.Parent;
		}

		throw new InvalidOperationException("test must run inside the Orchestra repository");
	}
}
