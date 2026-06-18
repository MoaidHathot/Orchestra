using Orchestra.Cli.Hosting;
using Orchestra.Client;
using Orchestra.Host.Hosting;
using Spectre.Console;

namespace Orchestra.Cli.Commands;

/// <summary>
/// Shared execution shell for the managed Group-A verbs. Resolves a target instance the same way
/// <c>run</c>/<c>exec</c> do (explicit <c>--server</c> → <c>$ORCHESTRA_URL</c> → <c>orchestra.json</c>),
/// connects to a healthy running server or spawns a throwaway inert host, runs the verb's
/// <paramref name="action"/> with a ready client, then tears down anything it spawned. Keeping this
/// in one place means every managed verb behaves identically and reuses the exact connect-or-spawn
/// machinery (<see cref="OrchestraHostSessionFactory"/>) rather than re-implementing it.
/// </summary>
internal static class ManagedSession
{
	/// <summary>Exit code when no instance can be reached/spawned (e.g. <c>--mode existing</c> with no server).</summary>
	public const int LaunchErrorExitCode = 3;

	public static async Task<int> RunAsync(ManagedCommandSettings settings, Func<OrchestraClient, Task> action)
	{
		// The management host never loads the user's orchestra.json into the spawned instance:
		// scanning the workspace and starting orchestra.services.json / orchestra.mcp.json processes
		// can take tens of seconds and is pure overhead for a read-only registry operation. We
		// resolve only the data path from config (so it reads the same files the server uses) and
		// inject it into an otherwise reproducible, side-effect-free host (NoConfig = true).
		var dataPath = settings.DataPath ?? (settings.NoConfig ? null : SafeConfiguredDataPath());

		var request = new HostSessionRequest
		{
			ServerUrl = ClientFactory.ResolveServerUrlOrNull(settings.Server, settings.NoConfig),
			Mode = settings.ResolveMode(),
			NoConfig = true,
			// Load global MCP DEFINITIONS (so MCP-referencing orchestrations parse and list) but never
			// START the services/proxies — that's the slow, side-effecting part the management host skips.
			SkipExternalServices = false,
			DataPath = dataPath,
			ConfigureIsolation = ConfigureManagementHost,
			SpawnOnlyOptionLabels = SpawnOnlyOptionsInEffect(settings),
		};

		var result = await OrchestraHostSessionFactory.ConnectOrSpawnAsync(request);
		if (!result.Ok)
		{
			AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(result.ErrorMessage!)}");
			return LaunchErrorExitCode;
		}

		// Informational notes go to stderr so stdout stays a clean JSON document for piping.
		foreach (var note in result.Notes)
		{
			Console.Error.WriteLine(note);
		}

		await using var session = result.Session!;

		// Let HttpRequestException from the action propagate to the top-level handler (which maps
		// it to exit code 1) — `await using` still tears the spawned host down on the way out.
		await action(session.Client);
		return 0;
	}

	/// <summary>
	/// Inert management host profile: load the registry (and register JSON-declared triggers so
	/// <c>triggers list</c> can show them) but disable the scheduler, auto-resume, and external
	/// infrastructure startup so the throwaway instance never fires, resumes, or boots services/MCP
	/// proxies during its brief lifetime. Global MCP definitions are still loaded (for parsing) via
	/// <c>SkipExternalServices = false</c> on the request.
	/// </summary>
	private static void ConfigureManagementHost(OrchestrationHostOptions o)
	{
		o.EnableScheduler = false;
		o.AutoResumeCheckpointsOnStartup = false;
		o.StartExternalServices = false;
		o.LoadPersistedOrchestrations = true;
		o.RegisterJsonTriggers = true;
	}

	private static IReadOnlyList<string> SpawnOnlyOptionsInEffect(ManagedCommandSettings settings)
	{
		var labels = new List<string>();
		if (settings.NoConfig) labels.Add("--no-config");
		if (settings.DataPath is not null) labels.Add("--data-path");
		return labels;
	}

	/// <summary>Best-effort read of the configured data path; a malformed/absent orchestra.json
	/// must not block a management command (it falls back to the host default).</summary>
	private static string? SafeConfiguredDataPath()
	{
		try { return OrchestraConfigLoader.ResolveConfiguredDataPath(); }
		catch { return null; }
	}
}
