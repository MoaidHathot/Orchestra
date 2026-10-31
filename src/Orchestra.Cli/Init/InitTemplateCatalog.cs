using Shared = Orchestra.Host.Workspaces;

namespace Orchestra.Cli.Init;

public sealed record InitTemplate(string Id, string Title, string Summary, string SourcePath, bool RequiresSkill = false)
{
	internal Shared.InitTemplate Shared() => new(Id, Title, Summary, SourcePath, RequiresSkill);
}
public static class InitTemplateCatalog
{
	public const string DefaultTemplateId = Shared.InitTemplateCatalog.DefaultTemplateId;
	public static IReadOnlyList<InitTemplate> Discover(string templatesDirectory)
		=> Shared.InitTemplateCatalog.Discover(templatesDirectory).Select(t => new InitTemplate(t.Id, t.Title, t.Summary, t.SourcePath, t.RequiresSkill)).ToArray();
	public static InitTemplate? Find(IReadOnlyList<InitTemplate> templates, string id)
		=> templates.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));
}
