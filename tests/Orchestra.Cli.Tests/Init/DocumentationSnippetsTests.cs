using System.Text.RegularExpressions;
using FluentAssertions;
using Orchestra.Engine;
using Xunit;

namespace Orchestra.Cli.Tests.Init;

/// <summary>
/// Validates the orchestration snippets embedded in the README and the docs site.
/// </summary>
/// <remarks>
/// Documentation examples are the ones users copy first, and they rot silently: nothing compiles
/// a fenced code block. Two snippets in these files shipped with a required <c>systemPrompt</c>
/// missing and a bare <c>{{topic}}</c> that the resolver never supported, so they could not have
/// run as written. Putting them through the same parser and template-expression validator the
/// executor uses turns that into a build failure.
/// </remarks>
public sealed class DocumentationSnippetsTests
{
    /// <summary>
    /// Matches fenced YAML/JSON blocks. Group 1 is the language, group 2 the body.
    /// </summary>
    private static readonly Regex s_fencedBlock = new(
        @"```(yaml|json)\r?\n(.*?)```",
        RegexOptions.Singleline | RegexOptions.Compiled);

    public static TheoryData<string> DocumentationFiles()
    {
        var data = new TheoryData<string>();
        var root = RepositoryRoot();
        if (root is null)
            return data;

        data.Add("README.md");

        var docs = Path.Combine(root, "docs");
        if (Directory.Exists(docs))
        {
            foreach (var path in Directory.EnumerateFiles(docs, "*.md").OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
                data.Add(Path.Combine("docs", Path.GetFileName(path)));
        }

        return data;
    }

    [Fact]
    public void Documentation_IsDiscoverable()
    {
        RepositoryRoot().Should().NotBeNull();
        DocumentationFiles().Should().NotBeEmpty();
    }

    [Theory]
    [MemberData(nameof(DocumentationFiles))]
    public void EmbeddedOrchestrations_AreValid(string relativePath)
    {
        var root = RepositoryRoot();
        root.Should().NotBeNull();

        var full = Path.Combine(root!, relativePath);
        if (!File.Exists(full))
            return;

        var index = 0;
        foreach (var snippet in ExtractOrchestrationSnippets(File.ReadAllText(full)))
        {
            index++;

            // Round-trip through a temp file: the parser resolves relative paths against the
            // file's directory, and YAML vs JSON is chosen by extension.
            var temp = Path.Combine(
                Path.GetTempPath(),
                $"orchestra-doc-snippet-{Guid.NewGuid():N}.{snippet.Language}");

            try
            {
                File.WriteAllText(temp, snippet.Body);

                Orchestration orchestration;
                try
                {
                    orchestration = OrchestrationParser.ParseOrchestrationFile(temp, availableMcps: []);
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException(
                        $"{relativePath} snippet #{index} does not parse: {ex.Message}\n\n{snippet.Body}", ex);
                }

                var result = TemplateExpressionValidator.ValidateOrchestration(orchestration);
                result.IsValid.Should().BeTrue(
                    $"{relativePath} snippet #{index} ('{orchestration.Name}') has invalid template expressions:\n{result.FormatErrors()}");
            }
            finally
            {
                try { File.Delete(temp); }
                catch { /* best-effort cleanup */ }
            }
        }
    }

    private sealed record Snippet(string Language, string Body);

    /// <summary>
    /// Returns fenced blocks that look like a complete orchestration document.
    /// </summary>
    /// <remarks>
    /// Docs are mostly fragments — a step, a hook, an MCP entry — which cannot be parsed on their
    /// own, so only blocks declaring all three required top-level fields are checked. Blocks
    /// containing <c>...</c> are skipped as well: those are deliberately elided API payloads,
    /// not something a reader would copy verbatim.
    /// </remarks>
    private static IEnumerable<Snippet> ExtractOrchestrationSnippets(string markdown)
    {
        foreach (Match match in s_fencedBlock.Matches(markdown))
        {
            var language = match.Groups[1].Value;
            var body = match.Groups[2].Value;

            if (body.Contains("..."))
                continue;

            if (!DeclaresTopLevelField(body, "name")
                || !DeclaresTopLevelField(body, "description")
                || !DeclaresTopLevelField(body, "steps"))
            {
                continue;
            }

            yield return new Snippet(language, body);
        }
    }

    private static bool DeclaresTopLevelField(string body, string field)
        => Regex.IsMatch(body, $@"(?m)^\s*""?{field}""?\s*:");

    private static string? RepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "OrchestrationEngine.slnx")))
                return current.FullName;

            current = current.Parent;
        }

        return null;
    }
}
