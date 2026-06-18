using System.ComponentModel;
using Orchestra.Client;
using Orchestra.Exec;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Orchestra.Cli.Commands;

/// <summary>
/// Settings for <see cref="RunCommand"/> / <see cref="ExecCommand"/>: run a single orchestration
/// to completion. Inherits the streaming flags (<c>--no-interactive</c>, <c>-q</c>, <c>-V</c>,
/// <c>--by</c>) and <c>--server</c>, and adds the one-shot host-selection + reporting options.
/// </summary>
public sealed class RunSettings : StreamingSettings
{
	[CommandArgument(0, "[NAME]")]
	[Description("Orchestration name (or ID) to run. Omit when using --run-file.")]
	public string? Name { get; set; }

	[CommandOption("--run-file <PATH>")]
	[Description("Register the given orchestration file, then run it (instead of <NAME>).")]
	public string? RunFile { get; set; }

	[CommandOption("--mode <MODE>")]
	[Description("Host selection: auto (default) | isolated | existing.")]
	public string? Mode { get; set; }

	[CommandOption("--param <KEY=VALUE>")]
	[Description("Repeated runtime parameter. Example: --param topic=AI --param length=short")]
	public string[] Params { get; set; } = [];

	[CommandOption("--run-timeout <SECONDS>")]
	[Description("Hard wall-clock timeout for the run, in seconds.")]
	public int? RunTimeout { get; set; }

	[CommandOption("--output <FILE>")]
	[Description("Write the run's final output to a file instead of stdout.")]
	public string? Output { get; set; }

	[CommandOption("--detailed")]
	[Description("Show richer real-time progress (model, MCP/tool calls, sub-agents, retries).")]
	public bool Detailed { get; set; }

	[CommandOption("--report <FORMAT>")]
	[Description("After the run, print a report: text | markdown | json.")]
	public string? Report { get; set; }

	[CommandOption("--report-output <FILE>")]
	[Description("Write the --report to a file instead of stdout.")]
	public string? ReportOutput { get; set; }

	[CommandOption("--orchestrations-path <DIR>")]
	[Description("Workspace dir scanned for orchestrations (spawned instance).")]
	public string? OrchestrationsPath { get; set; }

	[CommandOption("--data-path <DIR>")]
	[Description("Root data path for run history / registry (spawned instance).")]
	public string? DataPath { get; set; }

	[CommandOption("--no-config")]
	[Description("Ignore orchestra.json / services / global MCP (spawned instance).")]
	public bool NoConfig { get; set; }

	[CommandOption("--tag <NAME>")]
	[Description("Extra tag for a --run-file registered into a running instance (repeatable).")]
	public string[] Tags { get; set; } = [];

	[CommandOption("--keep-registered")]
	[Description("Leave a --run-file orchestration registered in a running instance after the run.")]
	public bool KeepRegistered { get; set; }

	public override ValidationResult Validate()
	{
		var hasName = !string.IsNullOrWhiteSpace(Name);
		var hasFile = !string.IsNullOrWhiteSpace(RunFile);
		if (hasName && hasFile)
		{
			return ValidationResult.Error("Specify either <NAME> or --run-file, not both.");
		}

		if (!hasName && !hasFile)
		{
			return ValidationResult.Error("Specify the orchestration to run: a <NAME> or --run-file <path>.");
		}

		if (Mode is not null && RunCommand.ParseMode(Mode) is null)
		{
			return ValidationResult.Error($"Invalid --mode '{Mode}' (expected auto, isolated, or existing).");
		}

		if (Report is not null && RunCommand.ParseReport(Report) is null)
		{
			return ValidationResult.Error($"Invalid --report '{Report}' (expected text, markdown, or json).");
		}

		return ValidationResult.Success();
	}
}

/// <summary>
/// <c>orchestra run</c> — run a single orchestration to completion. Connects to a running
/// Orchestra instance when one is configured (via <c>--server</c>, <c>ORCHESTRA_URL</c>, or the
/// discovered <c>orchestra.json</c> <c>hostBaseUrl</c>/<c>urls</c>) and healthy; otherwise spawns
/// a throwaway isolated host. <see cref="DefaultMode"/> lets <see cref="ExecCommand"/> pin the mode.
/// </summary>
public class RunCommand : AsyncCommand<RunSettings>
{
	/// <summary>When true, the default host-selection mode is isolated (used by <see cref="ExecCommand"/>).</summary>
	protected virtual bool DefaultsToIsolated => false;

	public override Task<int> ExecuteAsync(CommandContext context, RunSettings settings)
		=> ExecProgram.RunCoreAsync(ToExecOptions(settings, DefaultsToIsolated ? ExecMode.Isolated : ExecMode.Auto));

	internal static ExecOptions ToExecOptions(RunSettings s, ExecMode defaultMode)
	{
		var mode = s.Mode is null ? defaultMode : (ParseMode(s.Mode) ?? defaultMode);
		var report = s.Report is null ? ReportFormat.None : (ParseReport(s.Report) ?? ReportFormat.None);

		return new ExecOptions
		{
			RunId = string.IsNullOrWhiteSpace(s.Name) ? null : s.Name,
			RunFile = string.IsNullOrWhiteSpace(s.RunFile) ? null : s.RunFile,
			Mode = mode,
			ServerUrl = string.IsNullOrWhiteSpace(s.Server) ? null : s.Server,
			Parameters = ParameterParser.Parse(s.Params),
			TimeoutSeconds = s.RunTimeout,
			Verbose = s.Verbose,
			Quiet = s.Quiet,
			NoInteractive = s.NoInteractive,
			RespondedBy = s.RespondedBy,
			OutputFile = s.Output,
			Detailed = s.Detailed,
			Report = report,
			ReportOutput = s.ReportOutput,
			OrchestrationsPath = s.OrchestrationsPath,
			DataPath = s.DataPath,
			NoConfig = s.NoConfig,
			Tags = s.Tags ?? [],
			KeepRegistered = s.KeepRegistered,
		};
	}

	internal static ExecMode? ParseMode(string value) => value.Trim().ToLowerInvariant() switch
	{
		"auto" => ExecMode.Auto,
		"isolated" => ExecMode.Isolated,
		"existing" => ExecMode.Existing,
		_ => null,
	};

	internal static ReportFormat? ParseReport(string value) => value.Trim().ToLowerInvariant() switch
	{
		"text" => ReportFormat.Text,
		"markdown" or "md" => ReportFormat.Markdown,
		"json" => ReportFormat.Json,
		_ => null,
	};
}

/// <summary>
/// <c>orchestra exec</c> — alias of <c>run --mode isolated</c>: always run in a self-contained,
/// throwaway in-process host, ignoring any running server.
/// </summary>
public sealed class ExecCommand : RunCommand
{
	protected override bool DefaultsToIsolated => true;
}
