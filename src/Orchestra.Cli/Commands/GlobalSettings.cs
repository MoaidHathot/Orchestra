using System.ComponentModel;
using Spectre.Console.Cli;

namespace Orchestra.Cli.Commands;

/// <summary>
/// Base settings shared by every Orchestra CLI command: the URL of the Orchestra server
/// (Portal/Host) to talk to. Resolution order, applied by <see cref="ClientFactory"/>:
/// <list type="number">
///   <item>The explicit <c>--server / -s</c> flag, when provided.</item>
///   <item>The <c>ORCHESTRA_URL</c> environment variable.</item>
///   <item>The fallback <c>http://localhost:5000</c>.</item>
/// </list>
/// Promoted onto the base so every subcommand inherits the flag and shows it uniformly
/// in <c>--help</c> output.
/// </summary>
public class GlobalSettings : CommandSettings
{
	[CommandOption("-s|--server <URL>")]
	[Description("Orchestra server URL (default: $ORCHESTRA_URL or http://localhost:5000)")]
	public string? Server { get; set; }
}

/// <summary>
/// Adds the <c>--format</c> switch used by every command whose output is a JSON document.
/// Streaming commands (<c>run</c>, <c>attach</c>) intentionally do NOT inherit this — they
/// render live event frames, not buffered JSON, so a "table" view does not apply.
/// </summary>
public class JsonOutputSettings : GlobalSettings
{
	[CommandOption("--format <FORMAT>")]
	[Description("Output format: 'json' (default) or 'table'")]
	[DefaultValue("json")]
	public string Format { get; set; } = "json";
}
