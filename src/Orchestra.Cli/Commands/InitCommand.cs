using System.ComponentModel;
using Orchestra.Cli.Init;
using Orchestra.Exec;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Orchestra.Cli.Commands;

/// <summary>
/// Settings for <see cref="InitCommand"/>. Every prompted value has a matching flag, so a
/// fully-specified invocation is non-interactive without needing <c>--yes</c>, and a partly
/// specified one only prompts for what is still missing.
/// </summary>
public sealed class InitSettings : CommandSettings
{
	[CommandArgument(0, "[DIRECTORY]")]
	[Description("Directory to scaffold into (default: the current directory).")]
	public string? Directory { get; set; }

	[CommandOption("-t|--template <ID>")]
	[Description("Starter orchestration: hello | research | code-review.")]
	public string? Template { get; set; }

	[CommandOption("-p|--provider <PROVIDER>")]
	[Description("Default agent provider: copilot | opencode.")]
	public string? Provider { get; set; }

	[CommandOption("-m|--model <ID>")]
	[Description("Default model id written to orchestra.json.")]
	public string? Model { get; set; }

	[CommandOption("--no-config")]
	[Description("Skip writing orchestra.json.")]
	public bool NoConfig { get; set; }

	[CommandOption("--no-schemas")]
	[Description("Skip copying JSON schemas; reference the public GitHub schema URL instead.")]
	public bool NoSchemas { get; set; }

	[CommandOption("--with-skill")]
	[Description("Also copy the orchestration-authoring Agent Skill into .orchestra/skills/.")]
	public bool WithSkill { get; set; }

	[CommandOption("-f|--force")]
	[Description("Overwrite files that already exist.")]
	public bool Force { get; set; }

	[CommandOption("-y|--yes")]
	[Description("Accept defaults for anything not passed as a flag; never prompt.")]
	public bool Yes { get; set; }

	/// <summary>Providers accepted by <c>--provider</c>, matching the host's keyed registrations.</summary>
	internal static readonly string[] KnownProviders = ["copilot", "opencode"];

	/// <summary>Model written when the user neither passes <c>--model</c> nor is prompted.</summary>
	internal const string DefaultModel = "claude-opus-4.8";

	public override ValidationResult Validate()
	{
		if (Provider is not null
			&& !KnownProviders.Contains(Provider.Trim(), StringComparer.OrdinalIgnoreCase))
		{
			return ValidationResult.Error(
				$"Invalid --provider '{Provider}' (expected {string.Join(" or ", KnownProviders)}).");
		}

		return ValidationResult.Success();
	}
}

/// <summary>
/// <c>orchestra init</c> — scaffold a ready-to-run workspace: a starter orchestration under
/// <c>orchestrations/</c>, the JSON schemas under <c>.orchestra/schemas/</c>, and a project-local
/// <c>orchestra.json</c> whose <c>scan</c> block makes the orchestration runnable by name.
/// </summary>
/// <remarks>
/// Interactive by default. Anything supplied as a flag is not prompted for, so
/// <c>init --template hello --provider copilot --model x</c> runs unattended without
/// <c>--yes</c>; <c>--yes</c> exists to skip the remaining prompts in one go.
/// </remarks>
public sealed class InitCommand : AsyncCommand<InitSettings>
{
	public override async Task<int> ExecuteAsync(CommandContext context, InitSettings settings)
	{
		ArgumentNullException.ThrowIfNull(settings);

		var templatesDirectory = Path.Combine(AppContext.BaseDirectory, "templates");
		var templates = InitTemplateCatalog.Discover(templatesDirectory);

		if (templates.Count == 0)
		{
			AnsiConsole.MarkupLine(
				$"[red]Error:[/] no bundled templates found at '{Markup.Escape(templatesDirectory)}'.");
			AnsiConsole.MarkupLine("This usually means the Orchestra installation is incomplete; try reinstalling the tool.");
			return 1;
		}

		// stdin being a pipe means nobody is there to answer, so fall back to defaults even
		// without --yes. Matches how the run/attach verbs decide on HITL prompting.
		var interactive = !settings.Yes && !Console.IsInputRedirected;

		var target = Path.GetFullPath(
			string.IsNullOrWhiteSpace(settings.Directory)
				? System.IO.Directory.GetCurrentDirectory()
				: settings.Directory);

		var template = ResolveTemplate(settings, templates, interactive);
		if (template is null)
		{
			var known = string.Join(", ", templates.Select(t => t.Id));
			AnsiConsole.MarkupLine(
				$"[red]Error:[/] unknown template '{Markup.Escape(settings.Template ?? string.Empty)}'. Available: {Markup.Escape(known)}.");
			return 1;
		}

		var provider = ResolveProvider(settings, interactive);
		var model = ResolveModel(settings, interactive);

		var plan = new InitPlan(
			TargetDirectory: target,
			Template: template,
			Provider: settings.NoConfig ? null : provider,
			Model: settings.NoConfig ? null : model,
			WriteConfig: !settings.NoConfig,
			WriteSchemas: !settings.NoSchemas,
			Force: settings.Force,
			WriteSkill: settings.WithSkill);

		InitResult result;
		try
		{
			result = InitScaffolder.Scaffold(
				plan,
				Path.Combine(AppContext.BaseDirectory, "schemas"),
				Path.Combine(AppContext.BaseDirectory, "skills"));
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			AnsiConsole.MarkupLine($"[red]Error:[/] could not scaffold into '{Markup.Escape(target)}': {Markup.Escape(ex.Message)}");
			return 1;
		}

		Report(result, target);
		PrintNextSteps(result, plan);

		if (interactive && result.WrittenCount > 0 && Confirm($"Run '{result.OrchestrationName}' now?"))
		{
			AnsiConsole.WriteLine();
			return await ExecProgram.RunCoreAsync(new ExecOptions
			{
				RunFile = result.OrchestrationPath,
				Mode = ExecMode.Auto,
			});
		}

		return 0;
	}

	private static InitTemplate? ResolveTemplate(
		InitSettings settings,
		IReadOnlyList<InitTemplate> templates,
		bool interactive)
	{
		if (!string.IsNullOrWhiteSpace(settings.Template))
			return InitTemplateCatalog.Find(templates, settings.Template.Trim());

		var fallback = InitTemplateCatalog.Find(templates, InitTemplateCatalog.DefaultTemplateId) ?? templates[0];
		if (!interactive)
			return fallback;

		return AnsiConsole.Prompt(
			new SelectionPrompt<InitTemplate>()
				.Title("Which starter orchestration?")
				.AddChoices(templates)
				.UseConverter(t => $"{t.Title}\n    [dim]{Markup.Escape(t.Summary)}[/]"));
	}

	private static string ResolveProvider(InitSettings settings, bool interactive)
	{
		if (!string.IsNullOrWhiteSpace(settings.Provider))
			return settings.Provider.Trim().ToLowerInvariant();

		if (!interactive)
			return InitSettings.KnownProviders[0];

		return AnsiConsole.Prompt(
			new SelectionPrompt<string>()
				.Title("Default agent provider?")
				.AddChoices(InitSettings.KnownProviders)
				.UseConverter(p => p switch
				{
					"copilot" => "copilot — GitHub Copilot CLI (needs a Copilot subscription)",
					"opencode" => "opencode — spawns `opencode serve` (needs OpenCode on PATH)",
					_ => p,
				}));
	}

	private static string ResolveModel(InitSettings settings, bool interactive)
	{
		if (!string.IsNullOrWhiteSpace(settings.Model))
			return settings.Model.Trim();

		if (!interactive)
			return InitSettings.DefaultModel;

		return AnsiConsole.Prompt(
			new TextPrompt<string>("Default model?")
				.DefaultValue(InitSettings.DefaultModel));
	}

	private static bool Confirm(string question)
	{
		try
		{
			return AnsiConsole.Confirm(question, defaultValue: false);
		}
		catch (InvalidOperationException)
		{
			// No interactive terminal after all (e.g. output captured by a harness).
			return false;
		}
	}

	private static void Report(InitResult result, string target)
	{
		AnsiConsole.WriteLine();
		foreach (var file in result.Files)
		{
			var relative = Relative(target, file.Path);
			if (file.Outcome is InitFileOutcome.Written)
				AnsiConsole.MarkupLine($"  [green]created[/]  {Markup.Escape(relative)}");
			else
				AnsiConsole.MarkupLine($"  [yellow]exists[/]   {Markup.Escape(relative)} [dim](skipped)[/]");
		}

		AnsiConsole.WriteLine();
		AnsiConsole.MarkupLine($"[bold]{Markup.Escape(target)}[/]");
		AnsiConsole.MarkupLine($"{result.WrittenCount} created, {result.SkippedCount} skipped");

		if (result.SkippedCount > 0)
			AnsiConsole.MarkupLine("[dim]Pass --force to overwrite existing files.[/]");
	}

	private static void PrintNextSteps(InitResult result, InitPlan plan)
	{
		// Running by name only works when orchestra.json's scan block was written; without it
		// there is nothing to register the file, so point at the path instead.
		var runCommand = plan.WriteConfig
			? $"run {result.OrchestrationName}"
			: $"run --run-file ./{InitScaffolder.OrchestrationsDirectoryName}/{result.OrchestrationName}.yaml";

		var steps = new (string Command, string Note)[]
		{
			(InvocationStyle.Format("doctor"), "check prerequisites before the first run"),
			(InvocationStyle.Format(runCommand), "run it"),
			(InvocationStyle.Format("new my-workflow"), "add your own orchestration from a template"),
			(InvocationStyle.Format("portal"), "web UI, REST API, and MCP endpoints"),
		};

		var width = steps.Max(s => s.Command.Length);

		AnsiConsole.WriteLine();
		AnsiConsole.MarkupLine("[bold]Next steps[/]");
		foreach (var (command, note) in steps)
			AnsiConsole.MarkupLine($"  {Markup.Escape(command.PadRight(width))}   [dim]{Markup.Escape(note)}[/]");
	}

	private static string Relative(string from, string to)
	{
		try
		{
			return Path.GetRelativePath(from, to).Replace('\\', '/');
		}
		catch
		{
			return to;
		}
	}
}
