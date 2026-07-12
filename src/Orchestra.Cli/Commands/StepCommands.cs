using System.ComponentModel;
using System.Text.Json;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Orchestra.Cli.Commands;

// ─────────────────────────────────────────────────────────────────────────────
// step complete / step set-status
//
// The cross-language front door for a Script step to signal orchestration control —
// the non-LLM equivalent of the orchestra_complete / orchestra_set_status engine tools.
// Both write the control JSON to the file named by ORCHESTRA_CONTROL_FILE (which the
// engine sets for every Script step); the engine reads it after the script exits.
//
// These commands are purely local (no server connection) so they work inside any
// script environment. PowerShell steps can use the injected Orchestra-Complete /
// Orchestra-SetStatus helpers instead; bash/python/node use this CLI.
// ─────────────────────────────────────────────────────────────────────────────

internal static class StepControlFile
{
	public const string EnvVar = "ORCHESTRA_CONTROL_FILE";

	public static int Write(string verb, string action, string status, string? reason)
	{
		var path = Environment.GetEnvironmentVariable(EnvVar);
		if (string.IsNullOrWhiteSpace(path))
		{
			AnsiConsole.MarkupLine(
				$"[red]Error:[/] {EnvVar} is not set. `orchestra step {Markup.Escape(verb)}` only works inside an Orchestra Script step.");
			return 1;
		}

		var payload = JsonSerializer.Serialize(new Dictionary<string, string>(StringComparer.Ordinal)
		{
			["action"] = action,
			["status"] = status,
			["reason"] = reason ?? string.Empty,
		});

		File.WriteAllText(path, payload);
		return 0;
	}
}

public sealed class StepCompleteSettings : CommandSettings
{
	[CommandOption("--status <STATUS>")]
	[Description("Final orchestration status: success or failed.")]
	public string Status { get; set; } = string.Empty;

	[CommandOption("--reason <REASON>")]
	[Description("Why the orchestration should stop.")]
	public string? Reason { get; set; }

	public override ValidationResult Validate() =>
		Status.Trim().ToLowerInvariant() is "success" or "failed"
			? ValidationResult.Success()
			: ValidationResult.Error("--status must be 'success' or 'failed'.");
}

public sealed class StepCompleteCommand : Command<StepCompleteSettings>
{
	public override int Execute(CommandContext context, StepCompleteSettings settings)
		=> StepControlFile.Write("complete", "complete", settings.Status.Trim().ToLowerInvariant(), settings.Reason);
}

public sealed class StepSetStatusSettings : CommandSettings
{
	[CommandOption("--status <STATUS>")]
	[Description("Step status: success, failed, or no_action (no_action skips dependent steps).")]
	public string Status { get; set; } = string.Empty;

	[CommandOption("--reason <REASON>")]
	[Description("Explanation of the outcome.")]
	public string? Reason { get; set; }

	private static string Normalize(string status) => status.Trim().ToLowerInvariant().Replace('-', '_');

	public override ValidationResult Validate() =>
		Normalize(Status) is "success" or "failed" or "no_action"
			? ValidationResult.Success()
			: ValidationResult.Error("--status must be 'success', 'failed', or 'no_action'.");

	public string NormalizedStatus => Normalize(Status);
}

public sealed class StepSetStatusCommand : Command<StepSetStatusSettings>
{
	public override int Execute(CommandContext context, StepSetStatusSettings settings)
		=> StepControlFile.Write("set-status", "set_status", settings.NormalizedStatus, settings.Reason);
}
