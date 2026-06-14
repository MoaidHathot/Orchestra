using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Spectre.Console;

namespace Orchestra.Client.Run;

/// <summary>
/// Builds a <see cref="RunSession"/> wired with the right observer (compact / quiet / verbose)
/// and prompter (interactive when stdin is a TTY, non-interactive otherwise) for the supplied
/// flags. Shared by the <c>orchestra</c> CLI's streaming commands and the <c>orchestra-exec</c>
/// one-shot runner so both surfaces render and answer HITL identically.
/// </summary>
public static class RunSessionFactory
{
	/// <summary>
	/// Builds a session for the given client and verbosity/interactivity flags.
	/// </summary>
	/// <param name="client">Client pointed at the target server (loopback for exec).</param>
	/// <param name="verbose">Print every SSE event (firehose). Wins over <paramref name="quiet"/>.</param>
	/// <param name="quiet">Suppress per-step chatter; show only HITL prompts and the final summary.</param>
	/// <param name="noInteractive">
	/// Force non-interactive prompting. Auto-enabled when stdin is redirected (CI / pipes) so a
	/// HITL pause aborts deterministically with exit code 2 instead of hanging.
	/// </param>
	/// <param name="respondedBy">Audit identifier recorded with any HITL responses submitted.</param>
	public static RunSession Build(
		OrchestraClient client,
		bool verbose,
		bool quiet,
		bool noInteractive,
		string? respondedBy,
		IHumanInputPrompter? prompterOverride = null,
		bool detailed = false)
	{
		var loggerFactory = NullLoggerFactory.Instance;
		var ansi = AnsiConsole.Console;

		IRunObserver observer;
		if (verbose)
		{
			var compact = new ConsoleRunObserver(ansi, loggerFactory.CreateLogger<ConsoleRunObserver>());
			observer = new VerboseRunObserver(ansi, loggerFactory.CreateLogger<VerboseRunObserver>(), compact);
		}
		else if (detailed)
		{
			var compact = new ConsoleRunObserver(ansi, loggerFactory.CreateLogger<ConsoleRunObserver>());
			observer = new DetailedRunObserver(ansi, loggerFactory.CreateLogger<DetailedRunObserver>(), compact);
		}
		else if (quiet)
		{
			observer = new QuietRunObserver(ansi, loggerFactory.CreateLogger<QuietRunObserver>());
		}
		else
		{
			observer = new ConsoleRunObserver(ansi, loggerFactory.CreateLogger<ConsoleRunObserver>());
		}

		// Auto-degrade to non-interactive when stdin is redirected (CI / pipes) so scripts
		// that pipe output still get a deterministic outcome instead of a hang. A caller-
		// supplied prompter (tests, embedding hosts) always wins.
		var stdinIsTty = !Console.IsInputRedirected;
		IHumanInputPrompter prompter = prompterOverride
			?? ((noInteractive || !stdinIsTty)
				? new NonInteractiveHumanInputPrompter(ansi, loggerFactory.CreateLogger<NonInteractiveHumanInputPrompter>())
				: new InteractiveHumanInputPrompter(ansi, respondedBy, loggerFactory.CreateLogger<InteractiveHumanInputPrompter>()));

		var responder = new HumanInputResponder(client, loggerFactory.CreateLogger<HumanInputResponder>());
		return new RunSession(observer, prompter, responder, loggerFactory.CreateLogger<RunSession>());
	}
}

/// <summary>
/// Pure translation of a <see cref="RunSessionResult"/> into a POSIX-style process exit code.
/// Kept side-effect free (no console writes) so it can be unit-tested and reused; callers that
/// want to print a re-attach hint do so themselves.
/// </summary>
public static class RunExitCode
{
	/// <summary>
	/// 0 = succeeded, 1 = errored / non-success terminal / disconnect, 2 = aborted because no
	/// interactive stdin was available to answer a HITL pause, 130 = the user pressed Ctrl+C
	/// (SIGINT convention, so shell pipelines observe it correctly).
	/// </summary>
	public static int Map(RunSessionResult result, bool ctrlCPressed)
	{
		if (ctrlCPressed && result.Outcome == RunSessionOutcome.Disconnected)
		{
			return 130;
		}

		return result.Outcome switch
		{
			RunSessionOutcome.Succeeded => 0,
			RunSessionOutcome.NonSuccessfulTerminal => 1,
			RunSessionOutcome.Errored => 1,
			RunSessionOutcome.Disconnected => 1,
			RunSessionOutcome.NonInteractiveAbort => 2,
			_ => 1,
		};
	}
}
