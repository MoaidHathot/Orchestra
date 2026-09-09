using Orchestra.Cli.Commands;
using Orchestra.Host.Hosting;
using Spectre.Console;

namespace Orchestra.Cli;

/// <summary>
/// Replaces the bare-invocation help wall with a short "start here" banner on a machine that
/// has never been set up.
/// </summary>
/// <remarks>
/// <para>
/// Typing <c>orchestra</c> with no arguments prints all twenty-plus verbs, which tells a new
/// user everything except what to do first. This narrows that to two commands, and only for
/// someone who genuinely has no Orchestra state yet — anyone with a config file or existing run
/// data gets the normal help.
/// </para>
/// <para>
/// Deliberately invoked from <c>Main</c> rather than registered through
/// <see cref="Program.Configure"/>, so the command map stays exactly what
/// <c>CommandAppTester</c> exercises.
/// </para>
/// </remarks>
internal static class FirstRunBanner
{
	/// <summary>
	/// True when the banner should replace the default root help.
	/// </summary>
	/// <param name="args">Raw process arguments.</param>
	/// <param name="outputRedirected">
	/// Whether stdout is piped. Redirected output must keep printing real help so scripts and
	/// harnesses see a stable command list.
	/// </param>
	internal static bool ShouldShow(string[] args, bool outputRedirected)
	{
		if (args is not { Length: 0 } || outputRedirected)
			return false;

		return !HasExistingState();
	}

	/// <summary>
	/// Cheap "has this machine used Orchestra before?" probe: any discovered configuration, or a
	/// non-empty data directory (registry, run history, checkpoints).
	/// </summary>
	private static bool HasExistingState()
	{
		try
		{
			if (OrchestraConfigLoader.ResolveConfigPath() is not null)
				return true;

			var dataPath = OrchestraConfigLoader.ResolveConfiguredDataPath()
				?? new OrchestrationHostOptions().DataPath;

			return Directory.Exists(dataPath) && Directory.EnumerateFileSystemEntries(dataPath).Any();
		}
		catch
		{
			// If we cannot tell, assume the user is established and show normal help — a banner
			// shown to an experienced user is worse than help shown to a new one.
			return true;
		}
	}

	/// <summary>
	/// Writes the banner.
	/// </summary>
	/// <param name="version">Tool version shown in the header.</param>
	/// <param name="console">
	/// Target console. Defaults to <see cref="AnsiConsole.Console"/>; tests pass a capturing
	/// console because <c>AnsiConsole</c> holds its own writer and does not follow
	/// <see cref="Console.SetOut"/>.
	/// </param>
	internal static void Write(string version, IAnsiConsole? console = null)
	{
		var target = console ?? AnsiConsole.Console;

		target.WriteLine();
		target.MarkupLine($"[bold]Orchestra[/] {Markup.Escape(version)} - deterministic AI agent orchestrations");
		target.WriteLine();
		target.MarkupLine("Looks like a first run. Start here:");
		target.WriteLine();

		var steps = new (string Command, string Note)[]
		{
			(InvocationStyle.Format("init"), "scaffold a workspace with a runnable example"),
			(InvocationStyle.Format("doctor"), "check prerequisites (agent CLI, credentials, config)"),
			(InvocationStyle.Format("--help"), "every command"),
		};

		var width = steps.Max(s => s.Command.Length);
		foreach (var (command, note) in steps)
			target.MarkupLine($"  [bold]{Markup.Escape(command.PadRight(width))}[/]   [dim]{Markup.Escape(note)}[/]");

		target.WriteLine();
	}
}
