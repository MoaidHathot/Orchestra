using Orchestra.Cli.Commands;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Orchestra.Cli;

/// <summary>
/// Orchestra CLI entry point. Wires every verb against <see cref="CommandApp"/> from
/// Spectre.Console.Cli, which gives us per-command <c>--help</c>, typed settings,
/// validation, suggested-command typo correction, and pretty error rendering out of the box
/// — replacing the previous hand-rolled positional parser.
///
/// The <c>--server / -s</c> flag and <c>--format</c> flag are inherited by every leaf
/// command via <see cref="GlobalSettings"/> / <see cref="JsonOutputSettings"/> so they
/// appear once in the per-command help and resolve uniformly through
/// <see cref="ClientFactory"/>.
/// </summary>
public class Program
{
	public static int Main(string[] args)
	{
		var app = new CommandApp();
		app.Configure(Configure);

		try
		{
			return app.Run(args);
		}
		catch (CommandRuntimeException ex)
		{
			// Argument parse / validation errors. Spectre has already printed the rich
			// formatted error to stderr; we just translate the outcome to exit code 1.
			AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(ex.Message)}");
			return 1;
		}
		catch (HttpRequestException ex)
		{
			AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(ex.Message)}");
			return 1;
		}
		catch (Exception ex)
		{
			AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(ex.Message)}");
			return 1;
		}
	}

	/// <summary>
	/// Builds the command map for the Orchestra CLI. Public so tests can configure a
	/// <c>CommandAppTester</c> against the exact same wiring as production. Adding or
	/// removing commands here is the canonical place — keep it grouped by user-facing
	/// concept.
	/// </summary>
	public static void Configure(IConfigurator config)
	{
		config.SetApplicationName("orchestra");
		config.SetApplicationVersion(ThisAssembly.InformationalVersion());

		// PropagateExceptions lets our outer try/catch render uniform error text.
		// CaseSensitivity.None matches the legacy hand-rolled parser's lenience.
		config.PropagateExceptions();
		config.CaseSensitivity(CaseSensitivity.None);

		// ── Orchestration commands (top-level) ───────────────────────────────────
		config.AddCommand<ListCommand>("list")
			.WithAlias("ls")
			.WithDescription("List all orchestrations (supports --filter, --tag, --enabled/--disabled).")
			.WithExample("list")
			.WithExample("list", "--filter", "deploy")
			.WithExample("list", "--tag", "prod", "--enabled");

		config.AddCommand<GetCommand>("get")
			.WithDescription("Get details for a single orchestration by ID or declared name.")
			.WithExample("get", "research-assistant");

		config.AddCommand<RegisterCommand>("register")
			.WithDescription("Register an orchestration from a .json/.yaml file.")
			.WithExample("register", "./orchestrations/hello-world.json");

		config.AddCommand<RemoveCommand>("remove")
			.WithAlias("rm")
			.WithDescription("Remove an orchestration from the registry.");

		config.AddCommand<ScanCommand>("scan")
			.WithDescription("Scan a directory and register every orchestration file it contains.");

		config.AddCommand<EnableCommand>("enable")
			.WithDescription("Enable an orchestration's trigger.");

		config.AddCommand<DisableCommand>("disable")
			.WithDescription("Disable an orchestration's trigger.");

		// ── Execution ────────────────────────────────────────────────────────────
		config.AddCommand<RunCommand>("run")
			.WithDescription("Run a single orchestration to completion. Uses a running instance when one is configured and healthy (auto), else spawns an isolated one-shot host.")
			.WithExample("run", "research-assistant", "--param", "topic=AI")
			.WithExample("run", "--run-file", "./pipeline.yaml", "--report", "markdown")
			.WithExample("run", "deploy-pipeline", "--mode", "existing", "-q");

		config.AddCommand<ExecCommand>("exec")
			.WithDescription("Run a single orchestration in a self-contained, throwaway host (alias of `run --mode isolated`).")
			.WithExample("exec", "--run-file", "./pipeline.yaml", "--report", "markdown");

		config.AddCommand<AttachCommand>("attach")
			.WithDescription("Re-attach to a still-running run and stream the remaining events.")
			.WithExample("attach", "deploy-pipeline", "run-abc123");

		config.AddCommand<ActiveCommand>("active")
			.WithDescription("List currently active executions.");

		config.AddCommand<CancelCommand>("cancel")
			.WithDescription("Cancel a running execution.")
			.WithExample("cancel", "exec-abc123", "--reason", "superseded");

		config.AddCommand<ServerStatusCommand>("server-status")
			.WithDescription("Show the Orchestra server's status.");

		// ── Host / tooling ───────────────────────────────────────────────────────
		config.AddCommand<PortalCommand>("portal")
			.WithDescription("Launch the Orchestra host + Portal web UI (long-running).")
			.WithExample("portal")
			.WithExample("portal", "--urls", "http://localhost:5100");

		config.AddCommand<SchemasCliCommand>("schemas")
			.WithDescription("Copy the bundled JSON schemas into a local directory for editor $schema validation.")
			.WithExample("schemas", "--output", "./.orchestra/schemas");

		// ── Run history (branch) ─────────────────────────────────────────────────
		config.AddBranch("runs", branch =>
		{
			branch.SetDescription("Inspect, annotate, and manage past run history.");
			branch.AddCommand<RunsListCommand>("list")
				.WithDescription("List recent runs across all orchestrations.")
				.WithExample("runs", "list", "--limit", "50")
				.WithExample("runs", "list", "--favorites")
				.WithExample("runs", "list", "--tag", "connect");
			branch.AddCommand<RunsGetCommand>("get")
				.WithDescription("Get a specific run's full record.");
			branch.AddCommand<RunsDeleteCommand>("delete")
				.WithAlias("rm")
				.WithDescription("Delete a run record. Favorited runs require --force.");
			branch.AddCommand<RunsFavoriteCommand>("favorite")
				.WithAlias("star")
				.WithDescription("Mark a run as a favorite (exempt from retention deletion).");
			branch.AddCommand<RunsUnfavoriteCommand>("unfavorite")
				.WithAlias("unstar")
				.WithDescription("Remove a run's favorite mark.");
			branch.AddCommand<RunsAnnotateCommand>("annotate")
				.WithDescription("Set a run's title, tags, and note so it can be found later.")
				.WithExample("runs", "annotate", "my-orchestration", "a1b2c3d4e5f6",
					"--title", "Connect evidence pack", "--tag", "connect", "--favorite");
			branch.AddCommand<RunsAnnotationsCommand>("annotations")
				.WithDescription("List every annotated run and its tags.");
			branch.AddCommand<RunsAnnotationsPruneCommand>("prune-annotations")
				.WithDescription("Drop annotations whose run no longer exists.");
			branch.AddCommand<RunsExportCommand>("export")
				.WithDescription("Export a run (or every run matching --tag/--favorites) with its saved artifacts.")
				.WithExample("runs", "export", "my-orchestration", "a1b2c3d4e5f6", "--out", "./exports")
				.WithExample("runs", "export", "--tag", "connect", "--out", "./exports", "--zip");
		});

		// ── Triggers (branch) ────────────────────────────────────────────────────
		config.AddBranch("triggers", branch =>
		{
			branch.SetDescription("Manage orchestration triggers.");
			branch.AddCommand<TriggersListCommand>("list")
				.WithDescription("List all triggers and their state.");
			branch.AddCommand<TriggersEnableCommand>("enable")
				.WithDescription("Enable a trigger.");
			branch.AddCommand<TriggersDisableCommand>("disable")
				.WithDescription("Disable a trigger.");
			branch.AddCommand<TriggersFireCommand>("fire")
				.WithDescription("Fire a trigger manually with optional parameters.")
				.WithExample("triggers", "fire", "nightly-deploy", "--param", "env=staging");
		});

		// ── Profiles (branch) ────────────────────────────────────────────────────
		config.AddBranch("profiles", branch =>
		{
			branch.SetDescription("Manage profiles (named subsets of active orchestrations).");
			branch.AddCommand<ProfilesListCommand>("list")
				.WithDescription("List all profiles.");
			branch.AddCommand<ProfilesGetCommand>("get")
				.WithDescription("Get a profile's details.");
			branch.AddCommand<ProfilesActivateCommand>("activate")
				.WithDescription("Activate a profile (its orchestrations become eligible to run).");
			branch.AddCommand<ProfilesDeactivateCommand>("deactivate")
				.WithDescription("Deactivate a profile.");
			branch.AddCommand<ProfilesDeleteCommand>("delete")
				.WithAlias("rm")
				.WithDescription("Delete a profile.");
		});

		// ── Tags (branch) ────────────────────────────────────────────────────────
		config.AddBranch("tags", branch =>
		{
			branch.SetDescription("Manage orchestration tags.");
			branch.AddCommand<TagsListCommand>("list")
				.WithDescription("List all known tags with usage counts.");
			branch.AddCommand<TagsGetCommand>("get")
				.WithDescription("Show the effective tags on an orchestration.");
			branch.AddCommand<TagsAddCommand>("add")
				.WithDescription("Add comma-separated tags to an orchestration.")
				.WithExample("tags", "add", "research-assistant", "prod,nightly");
			branch.AddCommand<TagsRemoveCommand>("remove")
				.WithAlias("rm")
				.WithDescription("Remove a single tag from an orchestration.");
		});

		// ── Human-in-the-loop ────────────────────────────────────────────────────
		config.AddCommand<PendingCommand>("pending")
			.WithDescription("List runs awaiting human input.")
			.WithExample("pending", "--orchestration", "deploy-pipeline");

		config.AddCommand<RespondCommand>("respond")
			.WithDescription("Submit a response to a pending HITL wait.")
			.WithExample("respond", "deploy-pipeline", "run-abc123", "approve", "--choice", "approve")
			.WithExample("respond", "draft-summary", "run-xyz789", "clarify", "--reply", "AI angle");

		// ── Script-step control channel ───────────────────────────────────────────
		// Local-only verbs a Script step calls to signal orchestration control (writes
		// $ORCHESTRA_CONTROL_FILE, which the engine sets for every Script step). The
		// non-LLM equivalent of the orchestra_complete / orchestra_set_status engine tools.
		config.AddBranch("step", branch =>
		{
			branch.SetDescription("Signal orchestration control from inside a Script step (writes $ORCHESTRA_CONTROL_FILE).");
			branch.AddCommand<StepCompleteCommand>("complete")
				.WithDescription("Halt the whole orchestration (success|failed) — the non-LLM orchestra_complete.")
				.WithExample("step", "complete", "--status", "success", "--reason", "Inbox is empty, nothing to dispatch.");
			branch.AddCommand<StepSetStatusCommand>("set-status")
				.WithDescription("Set this step's status (success|failed|no_action); no_action skips dependent steps.")
				.WithExample("step", "set-status", "--status", "no_action", "--reason", "Nothing to do this tick.");
		});
	}
}

/// <summary>
/// Tiny helper to expose the assembly's informational version for Spectre's
/// <c>--version</c> flag without forcing a Microsoft.Extensions.Configuration dependency.
/// </summary>
internal static class ThisAssembly
{
	public static string InformationalVersion()
	{
		var asm = typeof(Program).Assembly;
		var attr = asm.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
			.OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
			.FirstOrDefault();
		return attr?.InformationalVersion ?? asm.GetName().Version?.ToString() ?? "0.0.0";
	}
}
