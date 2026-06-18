using System.ComponentModel;
using Orchestra.Exec;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Orchestra.Cli.Commands;

/// <summary>
/// Base settings for the "managed" Group-A verbs — the ones that operate on persisted Orchestra
/// state (registry, run history, triggers, profiles, tags). Beyond the inherited
/// <c>--server</c>/<c>--format</c>, these verbs accept a connect-or-spawn <c>--mode</c> (default
/// <c>auto</c>), mirroring <c>orchestra run</c>/<c>exec</c>, so they work whether or not a server
/// is running: <c>auto</c> uses a healthy configured server, else spawns a throwaway inert host
/// bound to the data path, performs the action, then tears it down. Live-runtime verbs
/// (<c>active</c>/<c>cancel</c>/<c>attach</c>/…) intentionally do NOT inherit this — they require a
/// running server.
/// </summary>
public class ManagedCommandSettings : JsonOutputSettings
{
	[CommandOption("--mode <MODE>")]
	[Description("How to reach Orchestra: auto (use a configured+healthy server, else spawn a temporary one), existing (require a running server), or isolated (always spawn). Default: auto.")]
	[DefaultValue("auto")]
	public string Mode { get; set; } = "auto";

	[CommandOption("--data-path <PATH>")]
	[Description("Data path for a spawned instance (registry/runs/triggers). Ignored when using a running server. Default: orchestra.json dataPath or the host default.")]
	public string? DataPath { get; set; }

	[CommandOption("--no-config")]
	[Description("Ignore orchestra.json when spawning a temporary instance. Ignored when using a running server.")]
	public bool NoConfig { get; set; }

	public override ValidationResult Validate()
		=> TryResolveMode(Mode, out _)
			? ValidationResult.Success()
			: ValidationResult.Error($"Invalid --mode '{Mode}'. Expected auto, existing, or isolated.");

	/// <summary>Maps the validated <c>--mode</c> string to the shared <see cref="ExecMode"/>.</summary>
	internal ExecMode ResolveMode() => TryResolveMode(Mode, out var mode) ? mode : ExecMode.Auto;

	private static bool TryResolveMode(string? value, out ExecMode mode)
	{
		switch (value?.Trim().ToLowerInvariant())
		{
			case null or "" or "auto":
				mode = ExecMode.Auto;
				return true;
			case "existing":
				mode = ExecMode.Existing;
				return true;
			case "isolated":
				mode = ExecMode.Isolated;
				return true;
			default:
				mode = ExecMode.Auto;
				return false;
		}
	}
}
