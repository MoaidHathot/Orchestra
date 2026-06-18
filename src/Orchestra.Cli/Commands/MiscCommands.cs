using System.ComponentModel;
using Spectre.Console.Cli;

namespace Orchestra.Cli.Commands;

// ─────────────────────────────────────────────────────────────────────────────
// active
// ─────────────────────────────────────────────────────────────────────────────

public sealed class ActiveCommand : AsyncCommand<JsonOutputSettings>
{
	public override async Task<int> ExecuteAsync(CommandContext context, JsonOutputSettings settings)
		=> await LiveServerCommand.RunAsync(settings, "active", async client =>
		{
			var result = await client.GetActiveExecutionsAsync();
			OutputWriter.Write(result, settings.Format);
		});
}

// ─────────────────────────────────────────────────────────────────────────────
// cancel <execution-id>
// ─────────────────────────────────────────────────────────────────────────────

public sealed class CancelSettings : JsonOutputSettings
{
	[CommandArgument(0, "<EXECUTION-ID>")]
	[Description("Execution ID to cancel (from `orchestra active`)")]
	public string ExecutionId { get; set; } = string.Empty;

	[CommandOption("--reason <TEXT>")]
	[Description("Free-text reason recorded on the run record")]
	public string? Reason { get; set; }

	[CommandOption("--source <LABEL>")]
	[Description("Client-type label to record on the cancel (defaults to 'cli')")]
	public string? Source { get; set; }
}

public sealed class CancelCommand : AsyncCommand<CancelSettings>
{
	public override async Task<int> ExecuteAsync(CommandContext context, CancelSettings settings)
		=> await LiveServerCommand.RunAsync(settings, "cancel", async client =>
		{
			var result = await client.CancelExecutionAsync(
				settings.ExecutionId,
				reason: settings.Reason,
				source: settings.Source ?? "cli");
			OutputWriter.Write(result, settings.Format);
		});
}

// ─────────────────────────────────────────────────────────────────────────────
// server-status
// ─────────────────────────────────────────────────────────────────────────────

public sealed class ServerStatusCommand : AsyncCommand<JsonOutputSettings>
{
	public override async Task<int> ExecuteAsync(CommandContext context, JsonOutputSettings settings)
		=> await LiveServerCommand.RunAsync(settings, "server-status", async client =>
		{
			var result = await client.GetStatusAsync();
			OutputWriter.Write(result, settings.Format);
		});
}
