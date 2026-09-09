using System.ComponentModel;
using Orchestra.Cli.Init;
using Orchestra.Host.Hosting;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Orchestra.Cli.Commands;

/// <summary>
/// Settings for <see cref="NewCommand"/>.
/// </summary>
public sealed class NewSettings : CommandSettings
{
	[CommandArgument(0, "[NAME]")]
	[Description("Kebab-case name for the new orchestration; also its file name. Omit with --list.")]
	public string? Name { get; set; }

	[CommandOption("-t|--template <ID>")]
	[Description("Template to start from: hello | smoke-test | research | code-review | approval | generate.")]
	public string? Template { get; set; }

	[CommandOption("-l|--list")]
	[Description("List the available templates and exit.")]
	public bool List { get; set; }

	[CommandOption("-f|--force")]
	[Description("Overwrite the file if it already exists.")]
	public bool Force { get; set; }

	public override ValidationResult Validate()
	{
		if (List)
			return ValidationResult.Success();

		if (string.IsNullOrWhiteSpace(Name))
			return ValidationResult.Error("Specify a <NAME> for the new orchestration, or --list to see templates.");

		if (!InitScaffolder.OrchestrationNamePattern.IsMatch(Name.Trim()))
		{
			return ValidationResult.Error(
				$"'{Name}' is not a valid orchestration name. Use kebab-case: lowercase letters, digits, and single hyphens (e.g. 'nightly-digest').");
		}

		return ValidationResult.Success();
	}
}

/// <summary>
/// <c>orchestra new</c> - add another orchestration to the current workspace, copied from a
/// bundled template and renamed so it runs independently.
/// </summary>
/// <remarks>
/// <para>
/// The workspace is the nearest directory up the tree holding an <c>orchestra.json</c>, which is
/// where the <c>scan</c> block will pick the new file up. Outside a workspace the file is created
/// under <c>./orchestrations/</c> and the next-step hint switches to <c>--run-file</c>, since
/// nothing registers it by name.
/// </para>
/// <para>
/// Local-only: no server, no agent, no network.
/// </para>
/// </remarks>
public sealed class NewCommand : Command<NewSettings>
{
	private readonly IAnsiConsole _console;

	public NewCommand(IAnsiConsole console) => _console = console;

	public override int Execute(CommandContext context, NewSettings settings)
	{
		ArgumentNullException.ThrowIfNull(settings);

		var templatesDirectory = Path.Combine(AppContext.BaseDirectory, "templates");
		var templates = InitTemplateCatalog.Discover(templatesDirectory);

		if (templates.Count == 0)
		{
			_console.MarkupLine($"[red]Error:[/] no bundled templates found at '{Markup.Escape(templatesDirectory)}'.");
			_console.MarkupLine("This usually means the Orchestra installation is incomplete; try reinstalling the tool.");
			return 1;
		}

		if (settings.List)
		{
			PrintTemplates(templates);
			return 0;
		}

		var templateId = string.IsNullOrWhiteSpace(settings.Template)
			? InitTemplateCatalog.DefaultTemplateId
			: settings.Template.Trim();

		var template = InitTemplateCatalog.Find(templates, templateId);
		if (template is null)
		{
			var known = string.Join(", ", templates.Select(t => t.Id));
			_console.MarkupLine($"[red]Error:[/] unknown template '{Markup.Escape(templateId)}'. Available: {Markup.Escape(known)}.");
			return 1;
		}

		var name = settings.Name!.Trim();
		var configPath = OrchestraConfigLoader.ResolveProjectConfigPath();
		var inWorkspace = configPath is not null;

		// A config under .orchestra/ means the workspace root is one level up from it.
		var workspaceRoot = configPath is null
			? Directory.GetCurrentDirectory()
			: WorkspaceRootFor(configPath);

		InitResult result;
		try
		{
			result = InitScaffolder.NewOrchestration(
				workspaceRoot,
				name,
				template,
				settings.Force,
				Path.Combine(AppContext.BaseDirectory, "skills"));
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
		{
			_console.MarkupLine($"[red]Error:[/] {Markup.Escape(ex.Message)}");
			return 1;
		}

		var orchestrationFile = result.Files.Last();
		var relative = Relative(Directory.GetCurrentDirectory(), orchestrationFile.Path);

		_console.WriteLine();
		if (orchestrationFile.Outcome is InitFileOutcome.Written)
		{
			_console.MarkupLine($"  [green]created[/]  {Markup.Escape(relative)}");
		}
		else
		{
			_console.MarkupLine($"  [yellow]exists[/]   {Markup.Escape(relative)} [dim](not overwritten; pass --force)[/]");
			return 1;
		}

		foreach (var extra in result.Files.Where(f => !ReferenceEquals(f, orchestrationFile) && f.Outcome is InitFileOutcome.Written))
			_console.MarkupLine($"  [green]created[/]  {Markup.Escape(Relative(Directory.GetCurrentDirectory(), extra.Path))}");

		PrintNextSteps(name, relative, inWorkspace);
		return 0;
	}

	private static string WorkspaceRootFor(string configPath)
	{
		var dir = Path.GetDirectoryName(Path.GetFullPath(configPath))!;
		return string.Equals(Path.GetFileName(dir), OrchestraConfigLoader.ProjectDirectoryName, StringComparison.OrdinalIgnoreCase)
			? Path.GetDirectoryName(dir)!
			: dir;
	}

	private void PrintTemplates(IReadOnlyList<InitTemplate> templates)
	{
		var width = templates.Max(t => t.Id.Length);

		_console.WriteLine();
		foreach (var t in templates)
		{
			var marker = t.Id == InitTemplateCatalog.DefaultTemplateId ? " [dim](default)[/]" : string.Empty;
			_console.MarkupLine($"  [bold]{Markup.Escape(t.Id.PadRight(width))}[/]  {Markup.Escape(t.Summary)}{marker}");
		}

		_console.WriteLine();
		_console.MarkupLine($"[dim]{Markup.Escape(InvocationStyle.Format("new <name> --template <id>"))}[/]");
	}

	private void PrintNextSteps(string name, string relativePath, bool inWorkspace)
	{
		// Only a workspace's scan block registers the file, so by-name only works inside one.
		var runCommand = inWorkspace ? $"run {name}" : $"run --run-file {relativePath}";

		var steps = new (string Command, string Note)[]
		{
			(InvocationStyle.Format($"validate {relativePath}"), "check it after editing"),
			(InvocationStyle.Format(runCommand), "run it"),
		};

		var width = steps.Max(s => s.Command.Length);

		_console.WriteLine();
		_console.MarkupLine($"Edit [bold]{Markup.Escape(relativePath)}[/] - start with the [bold]description[/] and the step prompts.");
		_console.WriteLine();
		foreach (var (command, note) in steps)
			_console.MarkupLine($"  {Markup.Escape(command.PadRight(width))}   [dim]{Markup.Escape(note)}[/]");

		if (!inWorkspace)
		{
			_console.WriteLine();
			_console.MarkupLine($"[dim]No orchestra.json found above this directory, so the file is not registered by name. Run `{Markup.Escape(InvocationStyle.Format("init"))}` to make this a workspace.[/]");
		}
	}

	private static string Relative(string from, string to)
	{
		try
		{
			var rel = Path.GetRelativePath(from, to).Replace('\\', '/');
			return rel.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(rel) ? rel : "./" + rel;
		}
		catch
		{
			return to;
		}
	}
}
