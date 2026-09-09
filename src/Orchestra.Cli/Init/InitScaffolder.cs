using System.Text;
using System.Text.RegularExpressions;

namespace Orchestra.Cli.Init;

/// <summary>
/// A fully-resolved <c>orchestra init</c> request. All interactive prompting and defaulting
/// happens before this is constructed, so <see cref="InitScaffolder"/> stays deterministic
/// and directly testable.
/// </summary>
/// <param name="TargetDirectory">Absolute path of the workspace to scaffold into.</param>
/// <param name="Template">The starter orchestration to copy.</param>
/// <param name="Provider">Agent provider written as <c>defaultProvider</c>, or null to omit.</param>
/// <param name="Model">Model id written as <c>defaultModel</c>, or null to omit.</param>
/// <param name="WriteConfig">Whether to write <c>orchestra.json</c>.</param>
/// <param name="WriteSchemas">Whether to copy the JSON schemas into <c>.orchestra/schemas/</c>.</param>
/// <param name="Force">Overwrite files that already exist instead of skipping them.</param>
/// <param name="WriteSkill">
/// Whether to copy the bundled <c>orchestration-authoring</c> skill into
/// <c>.orchestra/skills/</c>. Forced on when the template's own <c>skillDirectories</c>
/// reference it, because a missing skill directory is skipped silently at runtime.
/// </param>
public sealed record InitPlan(
	string TargetDirectory,
	InitTemplate Template,
	string? Provider,
	string? Model,
	bool WriteConfig,
	bool WriteSchemas,
	bool Force,
	bool WriteSkill = false);

/// <summary>What happened to a single scaffolded file.</summary>
public enum InitFileOutcome
{
	/// <summary>The file was created (or overwritten under <c>--force</c>).</summary>
	Written,

	/// <summary>The file already existed and <c>--force</c> was not set.</summary>
	Skipped,
}

/// <summary>One scaffolded path and its outcome.</summary>
public sealed record InitFileResult(string Path, InitFileOutcome Outcome);

/// <summary>Aggregate outcome of a scaffold, including the orchestration name to run next.</summary>
public sealed record InitResult(
	IReadOnlyList<InitFileResult> Files,
	string OrchestrationName,
	string OrchestrationPath)
{
	/// <summary>Count of files actually created.</summary>
	public int WrittenCount => Files.Count(f => f.Outcome is InitFileOutcome.Written);

	/// <summary>Count of files left alone because they already existed.</summary>
	public int SkippedCount => Files.Count(f => f.Outcome is InitFileOutcome.Skipped);
}

/// <summary>
/// Writes the <c>orchestra init</c> workspace: a starter orchestration under
/// <c>orchestrations/</c>, the JSON schemas under <c>.orchestra/schemas/</c>, and a
/// project-local <c>orchestra.json</c>.
/// </summary>
/// <remarks>
/// Scaffolding is additive and idempotent: an existing file is reported as
/// <see cref="InitFileOutcome.Skipped"/> rather than clobbered, so running <c>init</c> twice
/// (or in a folder that already has some of the pieces) is safe without <c>--force</c>.
/// </remarks>
public static class InitScaffolder
{
	/// <summary>Directory scanned for orchestration files; must be a child of the scan root.</summary>
	public const string OrchestrationsDirectoryName = "orchestrations";

	/// <summary>Directory under <c>.orchestra/</c> that scaffolded Agent Skills are copied into.</summary>
	public const string SkillsDirectoryName = "skills";

	/// <summary>The Agent Skill bundled with the tool and referenced by the `generate` template.</summary>
	public const string AuthoringSkillName = "orchestration-authoring";

	/// <summary>Public schema base URL used when schemas are not copied locally.</summary>
	public const string RemoteSchemaBaseUrl =
		"https://raw.githubusercontent.com/MoaidHathot/orchestra/main/schemas";

	private static readonly string[] s_schemaFileNames =
	[
		"orchestration.schema.json",
		"orchestra.schema.json",
		"orchestra.mcp.schema.json",
		"orchestra.services.schema.json",
	];

	// Matches the `# yaml-language-server: $schema=...` directive the bundled templates carry.
	// In-repo the templates point at ../schemas/... (valid relative to templates/); on scaffold
	// the directive is rewritten to whatever is correct for the destination.
	private static readonly Regex s_schemaDirective = new(
		@"^#\s*yaml-language-server:\s*\$schema=.*$",
		RegexOptions.Multiline | RegexOptions.CultureInvariant);

	/// <summary>
	/// Executes <paramref name="plan"/>, creating directories as needed.
	/// </summary>
	/// <param name="plan">The resolved scaffold request.</param>
	/// <param name="schemasSourceDirectory">
	/// Directory holding the bundled JSON schemas (<c>schemas/</c> next to the executable).
	/// Ignored when <see cref="InitPlan.WriteSchemas"/> is false.
	/// </param>
	/// <param name="skillsSourceDirectory">
	/// Directory holding the bundled Agent Skills (<c>skills/</c> next to the executable).
	/// Ignored unless the plan (or its template) asks for the skill.
	/// </param>
	public static InitResult Scaffold(
		InitPlan plan,
		string schemasSourceDirectory,
		string? skillsSourceDirectory = null)
	{
		ArgumentNullException.ThrowIfNull(plan);
		ArgumentNullException.ThrowIfNull(schemasSourceDirectory);

		var files = new List<InitFileResult>();

		Directory.CreateDirectory(plan.TargetDirectory);

		// The scan root is the target directory itself; the host looks for `orchestrations/`
		// and `profiles/` *inside* it, so the orchestration file goes one level down.
		var orchestrationsDir = Path.Combine(plan.TargetDirectory, OrchestrationsDirectoryName);
		Directory.CreateDirectory(orchestrationsDir);

		var projectDir = Path.Combine(
			plan.TargetDirectory,
			Host.Hosting.OrchestraConfigLoader.ProjectDirectoryName);

		var schemasCopied = false;
		if (plan.WriteSchemas)
		{
			var schemasTarget = Path.Combine(projectDir, "schemas");
			schemasCopied = CopySchemas(schemasSourceDirectory, schemasTarget, plan.Force, files);
		}

		// A template whose skillDirectories reference the skill must get it, otherwise the
		// runtime skips the missing directory silently and the orchestration quietly degrades.
		var wantsSkill = plan.WriteSkill || plan.Template.RequiresSkill;
		var skillCopied = false;
		if (wantsSkill && skillsSourceDirectory is not null)
		{
			var skillSource = Path.Combine(skillsSourceDirectory, AuthoringSkillName);
			var skillTarget = Path.Combine(projectDir, SkillsDirectoryName, AuthoringSkillName);
			skillCopied = CopyTree(skillSource, skillTarget, plan.Force, files);
		}

		if (schemasCopied || skillCopied)
			files.Add(WriteFile(Path.Combine(projectDir, ".gitignore"), GitignoreContent(), plan.Force));

		var orchestrationPath = Path.Combine(orchestrationsDir, $"{plan.Template.Id}.yaml");
		var orchestrationBody = RewriteSchemaDirective(
			File.ReadAllText(plan.Template.SourcePath),
			schemasCopied);
		files.Add(WriteFile(orchestrationPath, orchestrationBody, plan.Force));

		if (plan.WriteConfig)
		{
			var configPath = Path.Combine(plan.TargetDirectory, Host.Hosting.OrchestraConfigLoader.ConfigFileName);
			files.Add(WriteFile(configPath, BuildConfig(plan), plan.Force));
		}

		return new InitResult(files, plan.Template.Id, orchestrationPath);
	}

	/// <summary>
	/// Recursively copies <paramref name="sourceDirectory"/> into <paramref name="targetDirectory"/>,
	/// recording each file's outcome. Returns false when the source does not exist.
	/// </summary>
	private static bool CopyTree(string sourceDirectory, string targetDirectory, bool force, List<InitFileResult> files)
	{
		if (!Directory.Exists(sourceDirectory))
			return false;

		var any = false;

		foreach (var source in Directory.GetFiles(sourceDirectory, "*", SearchOption.AllDirectories))
		{
			var relative = Path.GetRelativePath(sourceDirectory, source);
			var target = Path.Combine(targetDirectory, relative);

			if (File.Exists(target) && !force)
			{
				files.Add(new InitFileResult(target, InitFileOutcome.Skipped));
				any = true;
				continue;
			}

			Directory.CreateDirectory(Path.GetDirectoryName(target)!);
			File.Copy(source, target, overwrite: true);
			files.Add(new InitFileResult(target, InitFileOutcome.Written));
			any = true;
		}

		return any;
	}

	/// <summary>
	/// Replaces the template's schema directive with one that resolves from the scaffolded
	/// location: a relative path into <c>.orchestra/schemas/</c> when schemas were copied,
	/// otherwise the public GitHub URL so editors still validate.
	/// </summary>
	internal static string RewriteSchemaDirective(string yaml, bool schemasCopied)
	{
		var target = schemasCopied
			? $"../{Host.Hosting.OrchestraConfigLoader.ProjectDirectoryName}/schemas/orchestration.schema.json"
			: $"{RemoteSchemaBaseUrl}/orchestration.schema.json";

		var directive = $"# yaml-language-server: $schema={target}";

		return s_schemaDirective.IsMatch(yaml)
			? s_schemaDirective.Replace(yaml, directive, count: 1)
			: directive + Environment.NewLine + yaml;
	}

	/// <summary>
	/// Builds the project-local <c>orchestra.json</c>. Written as JSONC — the loader skips
	/// comments and allows trailing commas — because <c>orchestra.json</c> has no published
	/// JSON Schema, so inline comments are the only in-editor documentation a newcomer gets.
	/// </summary>
	/// <remarks>
	/// Deliberately ASCII-only: this file is meant to be hand-edited, and a stray non-ASCII
	/// character in a generated config is a bad first impression in editors or terminals that
	/// guess the wrong codepage.
	/// </remarks>
	internal static string BuildConfig(InitPlan plan)
	{
		ArgumentNullException.ThrowIfNull(plan);

		// Each block is a comment preamble plus the property lines it documents. Building them
		// as a list lets the separator logic put a comma after every block but the last, which
		// is the only part of hand-rolling JSONC that is easy to get wrong.
		var blocks = new List<ConfigBlock>();

		// Bind the schema when we scaffolded a local copy, so an editor gives completion and
		// hover docs for every key below. Falls back to the public URL otherwise.
		var schemaTarget = plan.WriteSchemas
			? $"./{Host.Hosting.OrchestraConfigLoader.ProjectDirectoryName}/schemas/orchestra.schema.json"
			: $"{RemoteSchemaBaseUrl}/orchestra.schema.json";

		blocks.Add(new ConfigBlock(
			["// Editor completion and hover docs for every key in this file."],
			[$"\"$schema\": {JsonString(schemaTarget)}"]));

		blocks.Add(new ConfigBlock(
			[
				"// Register every orchestration under ./orchestrations automatically, so you",
				"// can run them by name instead of by path:",
				$"//   orchestra run {plan.Template.Id}",
				"//",
				"// NOTE: this is the workspace ROOT, not the orchestrations folder. The host",
				"// looks for `orchestrations/` and `profiles/` subdirectories inside it.",
			],
			[
				"\"scan\": {",
				"  \"directory\": \".\",",
				"  \"recursive\": true,",
				"  \"watch\": true",
				"}",
			]));

		blocks.Add(new ConfigBlock(
			[
				"// Run history, checkpoints, and the registry. Kept inside the project so this",
				"// workspace is self-contained; point it elsewhere to share state across projects.",
			],
			[$"\"dataPath\": \"./{Host.Hosting.OrchestraConfigLoader.ProjectDirectoryName}/data\""]));

		var agentDefaults = new List<string>();
		if (plan.Provider is not null)
			agentDefaults.Add($"\"defaultProvider\": {JsonString(plan.Provider)}");
		if (plan.Model is not null)
			agentDefaults.Add($"\"defaultModel\": {JsonString(plan.Model)}");

		if (agentDefaults.Count > 0)
		{
			// Every entry but the last needs its own separator inside the block too.
			for (var i = 0; i < agentDefaults.Count - 1; i++)
				agentDefaults[i] += ",";

			blocks.Add(new ConfigBlock(
				[
					"// Agent defaults. Any orchestration or individual step can override these",
					"// with its own `defaultProvider` / `provider` and `defaultModel` / `model`.",
				],
				agentDefaults));
		}

		var sb = new StringBuilder();
		sb.AppendLine("{");
		sb.AppendLine("  // Orchestra project configuration.");
		sb.AppendLine("  //");
		sb.AppendLine("  // Discovered by walking up from your working directory, so every `orchestra`");
		sb.AppendLine("  // command run inside this folder uses these settings. Comments and trailing");
		sb.AppendLine("  // commas are allowed. Full reference:");
		sb.AppendLine("  //   https://github.com/MoaidHathot/Orchestra/blob/main/docs/host.md");

		for (var i = 0; i < blocks.Count; i++)
		{
			sb.AppendLine();

			foreach (var comment in blocks[i].Comments)
				sb.AppendLine("  " + comment);

			var lines = blocks[i].Lines;
			for (var line = 0; line < lines.Count; line++)
			{
				var isLastLineOfBlock = line == lines.Count - 1;
				var needsSeparator = isLastLineOfBlock && i < blocks.Count - 1;
				sb.AppendLine("  " + lines[line] + (needsSeparator ? "," : string.Empty));
			}
		}

		sb.AppendLine("}");
		return sb.ToString();
	}

	/// <summary>A commented group of related properties in the generated JSONC config.</summary>
	private sealed record ConfigBlock(IReadOnlyList<string> Comments, IReadOnlyList<string> Lines);

	private static string GitignoreContent()
	{
		var sb = new StringBuilder();
		sb.AppendLine("# Orchestra run history, checkpoints, and registry — machine-local state.");
		sb.AppendLine("data/");
		return sb.ToString();
	}

	private static bool CopySchemas(string sourceDirectory, string targetDirectory, bool force, List<InitFileResult> files)
	{
		if (!Directory.Exists(sourceDirectory))
			return false;

		var any = false;
		Directory.CreateDirectory(targetDirectory);

		foreach (var fileName in s_schemaFileNames)
		{
			var source = Path.Combine(sourceDirectory, fileName);
			if (!File.Exists(source))
				continue;

			var target = Path.Combine(targetDirectory, fileName);
			if (File.Exists(target) && !force)
			{
				files.Add(new InitFileResult(target, InitFileOutcome.Skipped));
				any = true;
				continue;
			}

			File.Copy(source, target, overwrite: true);
			files.Add(new InitFileResult(target, InitFileOutcome.Written));
			any = true;
		}

		return any;
	}

	private static InitFileResult WriteFile(string path, string content, bool force)
	{
		var full = Path.GetFullPath(path);

		if (File.Exists(full) && !force)
			return new InitFileResult(full, InitFileOutcome.Skipped);

		Directory.CreateDirectory(Path.GetDirectoryName(full)!);
		File.WriteAllText(full, content);
		return new InitFileResult(full, InitFileOutcome.Written);
	}

	private static string JsonString(string value)
		=> System.Text.Json.JsonSerializer.Serialize(value);
}
