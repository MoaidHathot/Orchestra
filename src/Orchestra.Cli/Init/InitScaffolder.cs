using Shared = Orchestra.Host.Workspaces;

namespace Orchestra.Cli.Init;

// Compatibility facade: CLI consumers retain their existing types; the Portal uses
// the same underlying workspace writer instead of a second scaffolding implementation.
public sealed record InitPlan(string TargetDirectory, InitTemplate Template, string? Provider, string? Model,
	bool WriteConfig, bool WriteSchemas, bool Force, bool WriteSkill = false);
public enum InitFileOutcome { Written, Skipped }
public sealed record InitFileResult(string Path, InitFileOutcome Outcome);
public sealed record InitResult(IReadOnlyList<InitFileResult> Files, string OrchestrationName, string OrchestrationPath)
{
	public int WrittenCount => Files.Count(f => f.Outcome == InitFileOutcome.Written);
	public int SkippedCount => Files.Count(f => f.Outcome == InitFileOutcome.Skipped);
}
public static class InitScaffolder
{
	public const string OrchestrationsDirectoryName = Shared.InitScaffolder.OrchestrationsDirectoryName;
	public const string SkillsDirectoryName = Shared.InitScaffolder.SkillsDirectoryName;
	public const string AuthoringSkillName = Shared.InitScaffolder.AuthoringSkillName;
	public const string RemoteSchemaBaseUrl = Shared.InitScaffolder.RemoteSchemaBaseUrl;
	public const int DefaultRetentionMaxRunsPerOrchestration = Shared.InitScaffolder.DefaultRetentionMaxRunsPerOrchestration;
	public const int DefaultRetentionMaxRunAgeDays = Shared.InitScaffolder.DefaultRetentionMaxRunAgeDays;
	public static readonly System.Text.RegularExpressions.Regex OrchestrationNamePattern = Shared.InitScaffolder.OrchestrationNamePattern;
	public static InitResult Scaffold(InitPlan plan, string schemasSourceDirectory, string? skillsSourceDirectory = null)
		=> Convert(Shared.InitScaffolder.Scaffold(Convert(plan), schemasSourceDirectory, skillsSourceDirectory));
	public static InitResult NewOrchestration(string workspaceRoot, string name, InitTemplate template, bool force, string? skillsSourceDirectory = null)
		=> Convert(Shared.InitScaffolder.NewOrchestration(workspaceRoot, name, template.Shared(), force, skillsSourceDirectory));
	internal static string RewriteSchemaDirective(string yaml, bool schemasCopied) => Shared.InitScaffolder.RewriteSchemaDirective(yaml, schemasCopied);
	internal static string BuildConfig(InitPlan plan) => Shared.InitScaffolder.BuildConfig(Convert(plan));
	private static Shared.InitPlan Convert(InitPlan plan) => new(plan.TargetDirectory, plan.Template.Shared(), plan.Provider, plan.Model, plan.WriteConfig, plan.WriteSchemas, plan.Force, plan.WriteSkill);
	private static InitResult Convert(Shared.InitResult result) => new(result.Files.Select(f => new InitFileResult(f.Path, (InitFileOutcome)f.Outcome)).ToArray(), result.OrchestrationName, result.OrchestrationPath);
}
