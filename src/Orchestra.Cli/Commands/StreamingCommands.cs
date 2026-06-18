using System.ComponentModel;
using Orchestra.Client;
using Orchestra.Client.Run;
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
/// Thin CLI-side adapter over the shared <see cref="RunSessionFactory"/>: maps the CLI's
/// Spectre settings onto the factory's flags, and keeps the CLI-specific "re-attach" hint
/// when the user disconnects with Ctrl+C (the server-side run keeps going).
/// </summary>
internal static class StreamingSessionFactory
{
	public static RunSession Build(OrchestraClient client, StreamingSettings settings)
		=> RunSessionFactory.Build(
			client,
			verbose: settings.Verbose,
			quiet: settings.Quiet,
			noInteractive: settings.NoInteractive,
			respondedBy: settings.RespondedBy);

	/// <summary>
	/// Prints the CLI-specific re-attach hint on a Ctrl+C disconnect, then defers to the
	/// shared <see cref="RunExitCode.Map"/> for the POSIX-style exit code.
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
		}

		return RunExitCode.Map(result, ctrlCPressed);
	}
}
