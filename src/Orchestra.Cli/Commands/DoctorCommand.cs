using System.ComponentModel;
using System.Text.Json;
using Orchestra.Cli.Doctor;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Orchestra.Cli.Commands;

/// <summary>
/// Settings for <see cref="DoctorCommand"/>.
/// </summary>
/// <remarks>
/// Extends <see cref="GlobalSettings"/> rather than <see cref="JsonOutputSettings"/> so
/// <c>--format</c> can default to <c>text</c>. Doctor is read by a human deciding whether their
/// machine is ready; the data verbs default to JSON because they are meant to be piped.
/// </remarks>
public sealed class DoctorSettings : GlobalSettings
{
	[CommandOption("--format <FORMAT>")]
	[Description("Output format: 'text' (default) or 'json'.")]
	[DefaultValue("text")]
	public string Format { get; set; } = "text";

	[CommandOption("-p|--provider <PROVIDER>")]
	[Description("Check only one agent provider: copilot | opencode.")]
	public string? Provider { get; set; }

	[CommandOption("--fix")]
	[Description("Download the Copilot CLI now instead of during the first run.")]
	public bool Fix { get; set; }

	[CommandOption("--offline")]
	[Description("Skip checks that touch the network (agent auth, server health).")]
	public bool Offline { get; set; }

	public override ValidationResult Validate()
	{
		if (Provider is not null
			&& !InitSettings.KnownProviders.Contains(Provider.Trim(), StringComparer.OrdinalIgnoreCase))
		{
			return ValidationResult.Error(
				$"Invalid --provider '{Provider}' (expected {string.Join(" or ", InitSettings.KnownProviders)}).");
		}

		if (!string.Equals(Format, "text", StringComparison.OrdinalIgnoreCase)
			&& !string.Equals(Format, "json", StringComparison.OrdinalIgnoreCase))
		{
			return ValidationResult.Error($"Invalid --format '{Format}' (expected text or json).");
		}

		return ValidationResult.Success();
	}
}

/// <summary>
/// <c>orchestra doctor</c> — verify prerequisites before the first run: bundled assets, which
/// <c>orchestra.json</c> is in effect and whether it parses, data-path writability, the agent
/// CLI and its credentials, and server reachability.
/// </summary>
/// <remarks>
/// Exists because those failures otherwise surface <em>during</em> a run — after the one-time
/// ~100 MB Copilot CLI download and after a run scope is open — as a raw provider exception.
/// Exit code is 1 when any check fails so CI can gate on it.
/// </remarks>
public sealed class DoctorCommand : AsyncCommand<DoctorSettings>
{
	public override async Task<int> ExecuteAsync(CommandContext context, DoctorSettings settings)
	{
		ArgumentNullException.ThrowIfNull(settings);

		var options = new DoctorOptions(
			Provider: settings.Provider?.Trim().ToLowerInvariant(),
			Fix: settings.Fix,
			Offline: settings.Offline,
			ServerUrl: settings.Server);

		var report = await DoctorDiagnostics.RunAsync(options).ConfigureAwait(false);

		if (string.Equals(settings.Format, "json", StringComparison.OrdinalIgnoreCase))
		{
			WriteJson(report);
		}
		else
		{
			WriteText(report);
		}

		return report.HasFailures ? 1 : 0;
	}

	private static void WriteJson(DoctorReport report)
	{
		var payload = new
		{
			ok = !report.HasFailures,
			hasWarnings = report.HasWarnings,
			checks = report.Checks.Select(c => new
			{
				name = c.Name,
				status = c.Status.ToString().ToLowerInvariant(),
				detail = c.Detail,
				remedy = c.Remedy,
			}),
		};

		Console.WriteLine(JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
	}

	private static void WriteText(DoctorReport report)
	{
		AnsiConsole.WriteLine();

		// Pad the name column so the detail text lines up; long paths still wrap, but the
		// status/name prefix stays scannable.
		var width = report.Checks.Count == 0 ? 0 : report.Checks.Max(c => c.Name.Length);

		foreach (var check in report.Checks)
		{
			var (glyph, colour) = check.Status switch
			{
				DoctorStatus.Ok => ("ok  ", "green"),
				DoctorStatus.Warning => ("warn", "yellow"),
				DoctorStatus.Failed => ("fail", "red"),
				_ => ("skip", "grey"),
			};

			AnsiConsole.MarkupLine(
				$"  [{colour}]{glyph}[/]  [bold]{Markup.Escape(check.Name.PadRight(width))}[/]  {Markup.Escape(check.Detail)}");

			if (check.Remedy is not null && check.Status is not DoctorStatus.Ok)
				AnsiConsole.MarkupLine($"        {new string(' ', width)}[dim]{Markup.Escape(check.Remedy)}[/]");
		}

		AnsiConsole.WriteLine();

		if (report.HasFailures)
			AnsiConsole.MarkupLine("[red]Some checks failed.[/] Runs will not succeed until they are resolved.");
		else if (report.HasWarnings)
			AnsiConsole.MarkupLine("[yellow]Ready, with warnings.[/]");
		else
			AnsiConsole.MarkupLine("[green]Everything checks out.[/]");
	}
}
