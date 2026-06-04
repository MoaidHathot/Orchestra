using System.ComponentModel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Orchestra.Cli.Run;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Orchestra.Cli.Commands;

/// <summary>
/// Shared settings for the two SSE-streaming commands. Intentionally does NOT inherit
/// <see cref="JsonOutputSettings"/> — these commands render live event frames, not buffered
/// JSON, so a "table" view does not apply.
/// </summary>
public abstract class StreamingSettings : GlobalSettings
{
	[CommandOption("--no-interactive")]
	[Description("Don't prompt on HITL pauses; print the pending-input message and exit 2")]
	public bool NoInteractive { get; set; }

	[CommandOption("-q|--quiet")]
	[Description("Suppress per-step chatter; show only HITL prompts and final summary")]
	public bool Quiet { get; set; }

	[CommandOption("-V|--verbose")]
	[Description("Print every SSE event (firehose). Wins over --quiet if both are passed.")]
	public bool Verbose { get; set; }

	[CommandOption("--by <NAME>")]
	[Description("Audit identifier recorded with any HITL responses you submit")]
	public string? RespondedBy { get; set; }
}

/// <summary>
/// Settings for <see cref="RunCommand"/>: start a new run.
/// </summary>
public sealed class RunSettings : StreamingSettings
{
	[CommandArgument(0, "<ID>")]
	[Description("Orchestration ID (or declared name) to run")]
	public string Id { get; set; } = string.Empty;

	[CommandOption("--param <KEY=VALUE>")]
	[Description("Repeated runtime parameter. Example: --param topic=AI --param length=short")]
	public string[] Params { get; set; } = [];
}

public sealed class RunCommand : AsyncCommand<RunSettings>
{
	public override async Task<int> ExecuteAsync(CommandContext context, RunSettings settings)
	{
		using var client = ClientFactory.Create(settings);

		using var cts = new CancellationTokenSource();
		var ctrlCPressed = false;
		Console.CancelKeyPress += OnCancelKeyPress;
		void OnCancelKeyPress(object? _, ConsoleCancelEventArgs e)
		{
			// Don't kill the process; let us disconnect cleanly so the server's run keeps going.
			e.Cancel = true;
			ctrlCPressed = true;
			cts.Cancel();
		}

		try
		{
			using var response = await client.OpenRunStreamAsync(
				settings.Id,
				ParameterParser.Parse(settings.Params),
				cts.Token);
			var session = StreamingSessionFactory.Build(client, settings);
			var result = await session.RunAsync(response, settings.Id, cts.Token);
			return StreamingSessionFactory.MapOutcomeToExitCode(result, ctrlCPressed);
		}
		finally
		{
			Console.CancelKeyPress -= OnCancelKeyPress;
		}
	}
}

/// <summary>
/// Settings for <see cref="AttachCommand"/>: re-attach to a still-running run.
/// </summary>
public sealed class AttachSettings : StreamingSettings
{
	[CommandArgument(0, "<ORCHESTRATION>")]
	[Description("Orchestration name (as listed by `orchestra list`)")]
	public string OrchestrationName { get; set; } = string.Empty;

	[CommandArgument(1, "<RUN-ID>")]
	[Description("Run ID to attach to")]
	public string RunId { get; set; } = string.Empty;
}

public sealed class AttachCommand : AsyncCommand<AttachSettings>
{
	public override async Task<int> ExecuteAsync(CommandContext context, AttachSettings settings)
	{
		using var client = ClientFactory.Create(settings);

		using var cts = new CancellationTokenSource();
		var ctrlCPressed = false;
		Console.CancelKeyPress += OnCancelKeyPress;
		void OnCancelKeyPress(object? _, ConsoleCancelEventArgs e)
		{
			e.Cancel = true;
			ctrlCPressed = true;
			cts.Cancel();
		}

		try
		{
			using var response = await client.OpenAttachStreamAsync(
				settings.OrchestrationName,
				settings.RunId,
				cts.Token);
			var session = StreamingSessionFactory.Build(client, settings);
			var result = await session.RunAsync(response, settings.OrchestrationName, cts.Token);
			return StreamingSessionFactory.MapOutcomeToExitCode(result, ctrlCPressed);
		}
		finally
		{
			Console.CancelKeyPress -= OnCancelKeyPress;
		}
	}
}

/// <summary>
/// Shared wiring for <see cref="RunCommand"/> and <see cref="AttachCommand"/>: picks the
/// right observer (compact / quiet / verbose) and the right prompter (interactive when
/// stdin is a TTY, non-interactive otherwise) for the supplied flags.
/// </summary>
internal static class StreamingSessionFactory
{
	public static RunSession Build(OrchestraClient client, StreamingSettings settings)
	{
		var loggerFactory = NullLoggerFactory.Instance;
		var ansi = AnsiConsole.Console;

		IRunObserver observer;
		if (settings.Verbose)
		{
			var compact = new ConsoleRunObserver(ansi, loggerFactory.CreateLogger<ConsoleRunObserver>());
			observer = new VerboseRunObserver(ansi, loggerFactory.CreateLogger<VerboseRunObserver>(), compact);
		}
		else if (settings.Quiet)
		{
			observer = new QuietRunObserver(ansi, loggerFactory.CreateLogger<QuietRunObserver>());
		}
		else
		{
			observer = new ConsoleRunObserver(ansi, loggerFactory.CreateLogger<ConsoleRunObserver>());
		}

		// Auto-degrade to non-interactive when stdin is redirected (CI / pipes) so scripts
		// that previously used `orchestra run | jq` still get a deterministic outcome
		// instead of a hang.
		var stdinIsTty = !Console.IsInputRedirected;
		IHumanInputPrompter prompter = (settings.NoInteractive || !stdinIsTty)
			? new NonInteractiveHumanInputPrompter(ansi, loggerFactory.CreateLogger<NonInteractiveHumanInputPrompter>())
			: new InteractiveHumanInputPrompter(ansi, settings.RespondedBy, loggerFactory.CreateLogger<InteractiveHumanInputPrompter>());

		var responder = new HumanInputResponder(client, loggerFactory.CreateLogger<HumanInputResponder>());
		return new RunSession(observer, prompter, responder, loggerFactory.CreateLogger<RunSession>());
	}

	/// <summary>
	/// Translates the session outcome into a POSIX-style exit code:
	/// 0 = succeeded, 1 = errored / non-success terminal / disconnect, 2 = aborted because no
	/// interactive stdin was available to answer a HITL pause, 130 = the user pressed Ctrl+C
	/// (so the SIGINT convention is preserved and shell pipelines see it correctly).
	/// </summary>
	public static int MapOutcomeToExitCode(RunSessionResult result, bool ctrlCPressed)
	{
		if (ctrlCPressed && result.Outcome == RunSessionOutcome.Disconnected)
		{
			AnsiConsole.MarkupLine("[grey]Run continues on the server.[/]");
			if (result.OrchestrationName is not null && result.RunId is not null)
			{
				AnsiConsole.MarkupLine(
					$"[grey]Re-attach with:[/]  orchestra attach {Markup.Escape(result.OrchestrationName)} {Markup.Escape(result.RunId)}");
			}
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
