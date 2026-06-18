using System.ComponentModel;
using Orchestra.Playground.Copilot.Portal;
using Spectre.Console.Cli;

namespace Orchestra.Cli.Commands;

/// <summary>
/// Settings for <see cref="PortalCommand"/>. Maps the common host knobs onto the args
/// <see cref="PortalApp"/> consumes (<c>--urls</c> for Kestrel; <c>--data-path</c> /
/// <c>--orchestrations-path</c> as configuration keys).
/// </summary>
public sealed class PortalSettings : CommandSettings
{
	[CommandOption("--urls <URLS>")]
	[Description("URL(s) the portal binds to (semicolon-separated). Default from orchestra.json or http://localhost:5000.")]
	public string? Urls { get; set; }

	[CommandOption("--data-path <DIR>")]
	[Description("Root data path for run history / registry.")]
	public string? DataPath { get; set; }

	[CommandOption("--orchestrations-path <DIR>")]
	[Description("Workspace directory scanned for orchestrations.")]
	public string? OrchestrationsPath { get; set; }
}

/// <summary>
/// <c>orchestra portal</c> — launch the long-running Orchestra host + Portal web UI (REST API,
/// MCP endpoints, and the dashboard SPA). Blocks until the process is stopped.
/// </summary>
public sealed class PortalCommand : AsyncCommand<PortalSettings>
{
	public override async Task<int> ExecuteAsync(CommandContext context, PortalSettings settings)
	{
		// PortalApp reads --urls (Kestrel) and the data-path / orchestrations-path configuration
		// keys from the args array (ASP.NET's command-line config provider maps `--key value`).
		var args = new List<string>();
		if (!string.IsNullOrWhiteSpace(settings.Urls))
		{
			args.Add("--urls");
			args.Add(settings.Urls);
		}

		if (!string.IsNullOrWhiteSpace(settings.DataPath))
		{
			args.Add("--data-path");
			args.Add(settings.DataPath);
		}

		if (!string.IsNullOrWhiteSpace(settings.OrchestrationsPath))
		{
			args.Add("--orchestrations-path");
			args.Add(settings.OrchestrationsPath);
		}

		await PortalApp.RunAsync(args.ToArray(), typeof(Program), useAppBaseContentRoot: true);
		return 0;
	}
}

/// <summary>
/// Settings for <see cref="SchemasCliCommand"/>.
/// </summary>
public sealed class SchemasSettings : CommandSettings
{
	[CommandOption("-o|--output <DIR>")]
	[Description("Target directory for the copied schemas (default: ./.orchestra/schemas).")]
	public string? Output { get; set; }

	[CommandOption("-f|--force")]
	[Description("Overwrite existing schema files.")]
	public bool Force { get; set; }
}

/// <summary>
/// <c>orchestra schemas</c> — copy the bundled JSON schemas (orchestration, mcp, services) into a
/// local directory so editors can resolve <c>$schema</c> references offline.
/// </summary>
public sealed class SchemasCliCommand : Command<SchemasSettings>
{
	public override int Execute(CommandContext context, SchemasSettings settings)
	{
		var args = new List<string>();
		if (settings.Force)
		{
			args.Add("--force");
		}

		if (!string.IsNullOrWhiteSpace(settings.Output))
		{
			args.Add("--output");
			args.Add(settings.Output);
		}

		var schemasSource = Path.Combine(AppContext.BaseDirectory, "schemas");
		return global::Orchestra.Tool.SchemasCommand.Execute(
			args.ToArray(),
			Console.Out,
			Console.Error,
			schemasSource,
			Directory.GetCurrentDirectory());
	}
}
