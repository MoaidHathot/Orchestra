using System.ComponentModel;
using Spectre.Console.Cli;

namespace Orchestra.Cli.Commands;

// ─────────────────────────────────────────────────────────────────────────────
// pending [--orchestration <name>]
// ─────────────────────────────────────────────────────────────────────────────

public sealed class PendingSettings : JsonOutputSettings
{
	[CommandOption("--orchestration <NAME>")]
	[Description("Only show pending inputs for this orchestration")]
	public string? Orchestration { get; set; }
}

public sealed class PendingCommand : AsyncCommand<PendingSettings>
{
	public override async Task<int> ExecuteAsync(CommandContext context, PendingSettings settings)
	{
		using var client = ClientFactory.Create(settings);
		var result = await client.ListPendingAsync(settings.Orchestration);
		OutputWriter.Write(result, settings.Format);
		return 0;
	}
}

// ─────────────────────────────────────────────────────────────────────────────
// respond <orchestration> <run-id> <step-name> [--choice X] [--reply "..."] [--by name]
// ─────────────────────────────────────────────────────────────────────────────

public sealed class RespondSettings : JsonOutputSettings
{
	[CommandArgument(0, "<ORCHESTRATION>")]
	[Description("Orchestration name")]
	public string OrchestrationName { get; set; } = string.Empty;

	[CommandArgument(1, "<RUN-ID>")]
	[Description("Run ID")]
	public string RunId { get; set; } = string.Empty;

	[CommandArgument(2, "<STEP-NAME>")]
	[Description("Pending step name")]
	public string StepName { get; set; } = string.Empty;

	[CommandOption("--choice <VALUE>")]
	[Description("Pre-defined choice to submit (matches one of the prompt's choices)")]
	public string? Choice { get; set; }

	[CommandOption("--reply <TEXT>")]
	[Description("Free-form reply text to submit")]
	public string? Reply { get; set; }

	[CommandOption("--by <NAME>")]
	[Description("Audit identifier recorded against the response")]
	public string? RespondedBy { get; set; }

	public override Spectre.Console.ValidationResult Validate()
	{
		if (string.IsNullOrEmpty(Choice) && string.IsNullOrEmpty(Reply))
		{
			return Spectre.Console.ValidationResult.Error("Must supply at least one of --choice or --reply.");
		}
		return Spectre.Console.ValidationResult.Success();
	}
}

public sealed class RespondCommand : AsyncCommand<RespondSettings>
{
	public override async Task<int> ExecuteAsync(CommandContext context, RespondSettings settings)
	{
		using var client = ClientFactory.Create(settings);
		var result = await client.RespondAsync(
			settings.OrchestrationName,
			settings.RunId,
			settings.StepName,
			settings.Choice,
			settings.Reply,
			settings.RespondedBy);
		OutputWriter.Write(result, settings.Format);
		return 0;
	}
}
