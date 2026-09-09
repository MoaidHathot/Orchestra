using FluentAssertions;
using Orchestra.Engine;
using Xunit;

namespace Orchestra.Cli.Tests.Init;

/// <summary>
/// Validates every orchestration in the repository's <c>examples/</c> folder.
/// </summary>
/// <remarks>
/// These files are the primary reference material for anyone learning Orchestra, and they are
/// copied verbatim into user projects. Before this test existed, ten of them shipped with
/// unresolvable template expressions — bare <c>{{name}}</c> instead of <c>{{param.name}}</c>,
/// and a <c>{{workingDirectory}}</c> expression that was never implemented — which the runtime
/// silently passes through as literal text. Running the same parser and template validator the
/// executor uses catches that class of rot at build time.
/// </remarks>
public sealed class ExampleOrchestrationsTests
{
	/// <summary>
	/// Files under <c>examples/</c> that are configuration, not orchestrations.
	/// </summary>
	private static readonly HashSet<string> s_notOrchestrations =
		new(["orchestra.mcp.json", "orchestra.services.json"], StringComparer.OrdinalIgnoreCase);

	public static TheoryData<string> ExampleFiles()
	{
		var data = new TheoryData<string>();
		var dir = ExamplesDirectory();
		if (dir is null)
			return data;

		foreach (var path in Directory.EnumerateFiles(dir)
			.Where(p => p.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
				|| p.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase))
			.Where(p => !s_notOrchestrations.Contains(Path.GetFileName(p)))
			.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
		{
			data.Add(Path.GetFileName(path));
		}

		return data;
	}

	[Fact]
	public void ExamplesDirectory_IsDiscoverable()
	{
		ExamplesDirectory().Should().NotBeNull("the examples corpus is part of the repository");
		ExampleFiles().Should().NotBeEmpty();
	}

	[Theory]
	[MemberData(nameof(ExampleFiles))]
	public void Example_ParsesWithTheRuntimeParser(string fileName)
	{
		var act = () => Parse(fileName);

		act.Should().NotThrow($"'{fileName}' is published as a reference example");
	}

	[Theory]
	[MemberData(nameof(ExampleFiles))]
	public void Example_PassesTemplateExpressionValidation(string fileName)
	{
		var result = TemplateExpressionValidator.ValidateOrchestration(Parse(fileName));

		result.IsValid.Should().BeTrue(
			$"'{fileName}' has unresolvable template expressions, which reach the model as literal text:\n{result.FormatErrors()}");
	}

	[Theory]
	[MemberData(nameof(ExampleFiles))]
	public void Example_StepDependenciesResolve(string fileName)
	{
		var orchestration = Parse(fileName);
		var names = orchestration.Steps.Select(s => s.Name).ToHashSet(StringComparer.Ordinal);

		foreach (var step in orchestration.Steps)
		{
			foreach (var dependency in step.DependsOn ?? [])
				names.Should().Contain(dependency, $"step '{step.Name}' of '{fileName}' depends on an unknown step");
		}
	}

	[Theory]
	[MemberData(nameof(ExampleFiles))]
	public void Example_LoopExitPatternIsNotASubstringOfItsRejectionMarker(string fileName)
	{
		// The loop exit check is a case-insensitive substring match, so an exitPattern
		// contained in the rejection marker approves every rejection ("VALID" matches
		// "INVALID"). Derive the candidate markers from the checker's own prompts rather
		// than guessing, so APPROVED/REVISE passes and VALID/INVALID does not.
		foreach (var step in Parse(fileName).Steps.OfType<PromptOrchestrationStep>())
		{
			if (step.Loop is not { } loop)
				continue;

			foreach (var marker in ExtractMarkers(step))
			{
				if (marker.Equals(loop.ExitPattern, StringComparison.OrdinalIgnoreCase))
					continue;

				marker.Contains(loop.ExitPattern, StringComparison.OrdinalIgnoreCase)
					.Should().BeFalse(
						$"step '{step.Name}' of '{fileName}' uses exitPattern '{loop.ExitPattern}', which is a substring of '{marker}' — a rejection would be read as an approval");
			}
		}
	}

	[Theory]
	[MemberData(nameof(ExampleFiles))]
	public void Example_LoopCheckerDoesNotStripItsOwnExitMarker(string fileName)
	{
		// The loop matches the checker's FINAL content, i.e. after the output handler. A
		// handler told to remove the marker makes the exit condition unobservable. Telling
		// the handler to *keep* the marker ("ensure APPROVED is the first word") is fine, so
		// only flag handlers that both mention the pattern and ask for its removal.
		string[] removalVerbs = ["strip", "skip", "remove", "exclude", "without", "omit"];

		foreach (var step in Parse(fileName).Steps.OfType<PromptOrchestrationStep>())
		{
			if (step.Loop is not { } loop || step.OutputHandlerPrompt is not { } handler)
				continue;

			if (!handler.Contains(loop.ExitPattern, StringComparison.OrdinalIgnoreCase))
				continue;

			var removal = removalVerbs.FirstOrDefault(v => handler.Contains(v, StringComparison.OrdinalIgnoreCase));
			removal.Should().BeNull(
				$"step '{step.Name}' of '{fileName}' has an outputHandlerPrompt that says '{removal}' about its own exitPattern '{loop.ExitPattern}' — the loop would never see the marker");
		}
	}

	[Theory]
	[MemberData(nameof(ExampleFiles))]
	public void Example_ReferencesToBundledSkillsResolve(string fileName)
	{
		// skillDirectories resolve against the orchestration FILE's directory, and a missing
		// directory is skipped SILENTLY — the step just runs without the skill. Illustrative
		// names (./skills/code-review) are fine in a docs example, but a reference to a skill
		// this repo actually ships must resolve, or the orchestration quietly degrades.
		var bundled = BundledSkillNames();

		foreach (var step in Parse(fileName).Steps.OfType<PromptOrchestrationStep>())
		{
			foreach (var dir in step.SkillDirectories ?? [])
			{
				var name = Path.GetFileName(dir.TrimEnd('/', '\\'));
				if (!bundled.Contains(name))
					continue;

				Directory.Exists(dir).Should().BeTrue(
					$"step '{step.Name}' of '{fileName}' references the bundled skill '{name}' at '{dir}', which does not exist — the runtime skips it silently, so the step loses the skill with no error");
			}
		}
	}

	/// <summary>
	/// ALL-CAPS tokens in a checker step's prompts, which is how these orchestrations spell
	/// their verdict markers (APPROVED, NEEDS_WORK, REVISE, VALID, INVALID).
	/// </summary>
	private static IReadOnlyList<string> ExtractMarkers(PromptOrchestrationStep step)
	{
		var text = string.Join('\n', new[] { step.SystemPrompt, step.UserPrompt }.Where(s => s is not null));

		return System.Text.RegularExpressions.Regex
			.Matches(text, @"\b[A-Z][A-Z_]{3,}\b")
			.Select(m => m.Value)
			.Distinct(StringComparer.Ordinal)
			.ToArray();
	}

	private static HashSet<string> BundledSkillNames()
	{
		var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		var current = new DirectoryInfo(AppContext.BaseDirectory);
		while (current is not null)
		{
			var skills = Path.Combine(current.FullName, "skills");
			if (File.Exists(Path.Combine(current.FullName, "OrchestrationEngine.slnx")) && Directory.Exists(skills))
			{
				foreach (var dir in Directory.GetDirectories(skills))
					result.Add(Path.GetFileName(dir));
				break;
			}

			current = current.Parent;
		}

		return result;
	}

	private static Orchestration Parse(string fileName)
	{
		var dir = ExamplesDirectory();
		dir.Should().NotBeNull();
		return OrchestrationParser.ParseOrchestrationFile(Path.Combine(dir!, fileName), availableMcps: []);
	}

	private static string? ExamplesDirectory()
	{
		var current = new DirectoryInfo(AppContext.BaseDirectory);
		while (current is not null)
		{
			if (File.Exists(Path.Combine(current.FullName, "OrchestrationEngine.slnx")))
			{
				var examples = Path.Combine(current.FullName, "examples");
				return Directory.Exists(examples) ? examples : null;
			}

			current = current.Parent;
		}

		return null;
	}
}
