namespace Orchestra.Cli.Init;

/// <summary>
/// A starter orchestration shipped with the tool and copied into a workspace by
/// <c>orchestra init</c>.
/// </summary>
/// <param name="Id">CLI-facing id (<c>--template hello</c>) and the scaffolded file's base name.</param>
/// <param name="Title">Short label shown in the interactive picker.</param>
/// <param name="Summary">One-line description of what the template demonstrates.</param>
/// <param name="SourcePath">Absolute path of the bundled template file.</param>
/// <param name="RequiresSkill">
/// True when the template's <c>skillDirectories</c> point at the bundled authoring skill, so
/// scaffolding it must also copy the skill or the orchestration silently runs without it.
/// </param>
public sealed record InitTemplate(
	string Id,
	string Title,
	string Summary,
	string SourcePath,
	bool RequiresSkill = false);

/// <summary>
/// Discovers the starter orchestrations bundled alongside the tool (published to
/// <c>templates/</c> next to the executable, mirroring how <c>schemas/</c> ships).
/// </summary>
/// <remarks>
/// Titles and summaries are curated here rather than parsed out of the YAML so the picker
/// stays readable and ordering is deliberate — <c>hello</c> must be first because it is the
/// default and the one that runs with no arguments.
/// </remarks>
public static class InitTemplateCatalog
{
	/// <summary>The template used when the caller does not choose one.</summary>
	public const string DefaultTemplateId = "hello";

	private static readonly (string Id, string Title, string Summary, bool RequiresSkill)[] s_known =
	[
		("hello", "hello — starter (recommended)",
			"Three-step DAG: research, brief, then a no-cost Transform. Runs with no arguments.", false),
		("research", "research — parallel fan-out",
			"Two analyses run concurrently, then a third step synthesizes both.", false),
		("code-review", "code-review — Command feeding an agent",
			"Captures `git diff` in a Command step and reviews the real output.", false),
		("approval", "approval — human-in-the-loop gate",
			"Pauses for a human decision, then branches on the answer. Survives restarts.", false),
		("generate", "generate — write orchestrations from a description",
			"Meta-orchestration: drafts a new orchestration, then validates it deterministically.", true),
	];

	/// <summary>
	/// Returns the bundled templates, in presentation order. Unknown files found in the
	/// templates directory are included after the curated ones so a template can be added by
	/// dropping in a file, but curated ordering still wins for the ones we ship.
	/// </summary>
	public static IReadOnlyList<InitTemplate> Discover(string templatesDirectory)
	{
		ArgumentNullException.ThrowIfNull(templatesDirectory);

		if (!Directory.Exists(templatesDirectory))
			return [];

		var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		foreach (var path in Directory.GetFiles(templatesDirectory, "*.yaml"))
		{
			var id = Path.GetFileNameWithoutExtension(path);
			if (!string.IsNullOrEmpty(id))
				files[id] = path;
		}

		var result = new List<InitTemplate>();

		foreach (var (id, title, summary, requiresSkill) in s_known)
		{
			if (files.Remove(id, out var path))
				result.Add(new InitTemplate(id, title, summary, path, requiresSkill));
		}

		foreach (var (id, path) in files.OrderBy(static kv => kv.Key, StringComparer.OrdinalIgnoreCase))
			result.Add(new InitTemplate(id, id, "Bundled template.", path));

		return result;
	}

	/// <summary>
	/// Finds a template by id (case-insensitive). Returns null when no such template ships.
	/// </summary>
	public static InitTemplate? Find(IReadOnlyList<InitTemplate> templates, string id)
	{
		ArgumentNullException.ThrowIfNull(templates);
		return templates.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));
	}
}
