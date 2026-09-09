using Microsoft.Extensions.Logging;
using Orchestra.Cli.Commands;
using Orchestra.Copilot;
using Orchestra.Host.Hosting;
using Orchestra.OpenCode;

namespace Orchestra.Cli.Doctor;

/// <summary>
/// Runs the <c>orchestra doctor</c> prerequisite checks.
/// </summary>
/// <remarks>
/// Deliberately separated from rendering so the checks can be unit-tested and emitted as JSON.
/// Every check is written to answer a failure Orchestra otherwise only reports mid-run: a
/// missing agent CLI, absent credentials, an unparseable config that silently reverted the host
/// to defaults, or an unwritable data path.
/// </remarks>
public static class DoctorDiagnostics
{
	/// <summary>Cap on the health probe so an unreachable server does not stall the report.</summary>
	private static readonly TimeSpan ServerProbeTimeout = TimeSpan.FromSeconds(2);

	/// <summary>
	/// Executes every applicable check and returns the collected report. Never throws:
	/// an unexpected failure inside one check is reported as that check failing.
	/// </summary>
	public static async Task<DoctorReport> RunAsync(
		DoctorOptions options,
		ILogger? logger = null,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(options);

		var checks = new List<DoctorCheck>
		{
			CheckInstallation(),
		};

		var config = CheckConfiguration(options.StartDirectory, out var configFile);
		checks.Add(config);
		checks.Add(CheckDataPath(configFile, options.StartDirectory));

		var configuredProvider = (configFile?.Provider ?? configFile?.DefaultProvider ?? "copilot").ToLowerInvariant();

		if (WantsProvider(options.Provider, "copilot"))
			checks.AddRange(await CheckCopilotAsync(options, configFile, configuredProvider, logger, cancellationToken).ConfigureAwait(false));

		if (WantsProvider(options.Provider, "opencode"))
			checks.Add(CheckOpenCode(configFile, configuredProvider));

		checks.Add(await CheckServerAsync(options, cancellationToken).ConfigureAwait(false));

		return new DoctorReport(checks);
	}

	private static bool WantsProvider(string? filter, string provider)
		=> filter is null || string.Equals(filter, provider, StringComparison.OrdinalIgnoreCase);

	/// <summary>
	/// Verifies the tool's own bundled assets are present. A partial install shows up as
	/// confusing downstream errors (`orchestra init` with no templates, `$schema` that will not
	/// resolve), so it is worth naming directly.
	/// </summary>
	private static DoctorCheck CheckInstallation()
	{
		var schemas = Path.Combine(AppContext.BaseDirectory, "schemas");
		var templates = Path.Combine(AppContext.BaseDirectory, "templates");

		var missing = new List<string>();
		if (!Directory.Exists(schemas))
			missing.Add("schemas/");
		if (!Directory.Exists(templates))
			missing.Add("templates/");

		return missing.Count == 0
			? new DoctorCheck("installation", DoctorStatus.Ok, $"Bundled assets present at {AppContext.BaseDirectory}")
			: new DoctorCheck(
				"installation",
				DoctorStatus.Failed,
				$"Missing {string.Join(" and ", missing)} under {AppContext.BaseDirectory}",
				"The installation looks incomplete - reinstall the tool (`dotnet tool update --global Orchestra`).");
	}

	/// <summary>
	/// Reports which <c>orchestra.json</c> won and whether it parses.
	/// </summary>
	/// <remarks>
	/// A malformed config is a <em>failure</em> here even though the host tolerates it, because
	/// the host's tolerance is the problem being surfaced: it logs a warning and silently runs
	/// on defaults, so none of the operator's settings apply.
	/// </remarks>
	private static DoctorCheck CheckConfiguration(string? startDirectory, out OrchestraConfigFile? configFile)
	{
		configFile = null;

		var diagnostic = OrchestraConfigLoader.Describe(startDirectory);

		if (diagnostic.Path is null)
		{
			return new DoctorCheck(
				"configuration",
				DoctorStatus.Ok,
				"No orchestra.json found - using built-in defaults.",
				$"Run `{InvocationStyle.Format("init")}` to scaffold a project-local configuration.");
		}

		if (!diagnostic.Parsed)
		{
			return new DoctorCheck(
				"configuration",
				DoctorStatus.Failed,
				$"{diagnostic.Path} ({Describe(diagnostic.Source)}) could not be parsed: {diagnostic.Error}",
				"Orchestra silently falls back to built-in defaults when this happens, so none of these settings apply. Fix the file or delete it.");
		}

		try
		{
			configFile = OrchestraConfigLoader.Load();
		}
		catch
		{
			// Describe() already validated the file; a divergence here can only come from the
			// working-directory difference, which the detail line below makes visible anyway.
		}

		return new DoctorCheck(
			"configuration",
			DoctorStatus.Ok,
			$"{diagnostic.Path} ({Describe(diagnostic.Source)})");
	}

	private static string Describe(OrchestraConfigSource source) => source switch
	{
		OrchestraConfigSource.EnvironmentVariable => "from ORCHESTRA_CONFIG_PATH",
		OrchestraConfigSource.Project => "project-local",
		OrchestraConfigSource.XdgConfigHome => "from XDG_CONFIG_HOME",
		OrchestraConfigSource.PlatformDefault => "user-global",
		_ => "defaults",
	};

	/// <summary>
	/// Confirms the run-history/registry directory can actually be created and written.
	/// </summary>
	private static DoctorCheck CheckDataPath(OrchestraConfigFile? configFile, string? startDirectory)
	{
		string dataPath;
		try
		{
			dataPath = OrchestraConfigLoader.ResolveConfiguredDataPath() ?? new OrchestrationHostOptions().DataPath;
		}
		catch (Exception ex)
		{
			return new DoctorCheck("data path", DoctorStatus.Failed, $"Could not resolve a data path: {ex.Message}");
		}

		try
		{
			Directory.CreateDirectory(dataPath);

			var probe = Path.Combine(dataPath, $".orchestra-doctor-{Guid.NewGuid():N}");
			File.WriteAllText(probe, string.Empty);
			File.Delete(probe);

			return new DoctorCheck("data path", DoctorStatus.Ok, $"{dataPath} (writable)");
		}
		catch (Exception ex)
		{
			return new DoctorCheck(
				"data path",
				DoctorStatus.Failed,
				$"{dataPath} is not writable: {ex.Message}",
				"Point `dataPath` in orchestra.json at a writable directory, or pass --data-path.");
		}
	}

	private static async Task<IReadOnlyList<DoctorCheck>> CheckCopilotAsync(
		DoctorOptions options,
		OrchestraConfigFile? configFile,
		string configuredProvider,
		ILogger? logger,
		CancellationToken cancellationToken)
	{
		var isDefault = configuredProvider is "copilot";
		var missingStatus = isDefault ? DoctorStatus.Failed : DoctorStatus.Warning;

		var probe = CopilotPreflight.Inspect();

		if (probe.Error is not null)
		{
			return
			[
				new DoctorCheck("copilot cli", missingStatus, probe.Error),
				new DoctorCheck("copilot auth", DoctorStatus.Skipped, "Skipped - no usable Copilot CLI."),
			];
		}

		if (!probe.Available && options.Fix && !options.Offline)
		{
			try
			{
				var path = await CopilotPreflight.EnsureAsync(logger, cancellationToken).ConfigureAwait(false);
				probe = probe with { Available = true, Path = path, Source = CopilotCliSource.Cache };
			}
			catch (Exception ex)
			{
				return
				[
					new DoctorCheck("copilot cli", missingStatus, $"Download failed: {ex.Message}"),
					new DoctorCheck("copilot auth", DoctorStatus.Skipped, "Skipped - no usable Copilot CLI."),
				];
			}
		}

		if (!probe.Available)
		{
			// Not a failure on its own: the runtime downloads it automatically. The point of
			// flagging it is that the download is ~100 MB and happens *inside* the first run,
			// where it looks like a hang.
			return
			[
				new DoctorCheck(
					"copilot cli",
					DoctorStatus.Warning,
					$"Not installed - ~100 MB will download on the first run (CLI {probe.Version}, {probe.Rid}).",
					$"Run `{InvocationStyle.Format("doctor --fix")}` to download it now."),
				new DoctorCheck(
					"copilot auth",
					DoctorStatus.Skipped,
					"Skipped - the Copilot CLI is not installed yet, and checking would trigger the download."),
			];
		}

		var cliCheck = new DoctorCheck(
			"copilot cli",
			DoctorStatus.Ok,
			$"{probe.Path} ({DescribeSource(probe.Source)}, CLI {probe.Version})");

		if (options.Offline)
			return [cliCheck, new DoctorCheck("copilot auth", DoctorStatus.Skipped, "Skipped - --offline.")];

		var auth = await CopilotPreflight
			.CheckAuthAsync(configFile?.Copilot?.GitHubToken, configFile?.Copilot?.UseLoggedInUser, cancellationToken: cancellationToken)
			.ConfigureAwait(false);

		var authCheck = auth.Ok
			? new DoctorCheck("copilot auth", DoctorStatus.Ok, $"Authenticated - {auth.ModelCount} models available.")
			: new DoctorCheck(
				"copilot auth",
				missingStatus,
				auth.Error ?? "Unknown authentication failure.",
				"Sign in with the Copilot CLI, or set `copilot.gitHubToken` in orchestra.json. A GitHub Copilot subscription is required.");

		return [cliCheck, authCheck];
	}

	private static string DescribeSource(CopilotCliSource source) => source switch
	{
		CopilotCliSource.ExplicitPath => "from ORCHESTRA_COPILOT_CLI_PATH",
		CopilotCliSource.Bundled => "bundled",
		CopilotCliSource.Cache => "cached",
		_ => "not installed",
	};

	private static DoctorCheck CheckOpenCode(OrchestraConfigFile? configFile, string configuredProvider)
	{
		var isDefault = configuredProvider is "opencode";
		var probe = OpenCodePreflight.Inspect(configFile?.OpenCode?.CliPath);

		return probe.Available
			? new DoctorCheck("opencode cli", DoctorStatus.Ok, $"{probe.Path} (via {probe.Source})")
			: new DoctorCheck(
				"opencode cli",
				isDefault ? DoctorStatus.Failed : DoctorStatus.Warning,
				$"Not found (looked up via {probe.Source}).",
				"Install OpenCode and put it on PATH, or set `opencode.cliPath` in orchestra.json / $ORCHESTRA_OPENCODE_PATH.");
	}

	/// <summary>
	/// Probes the configured server. A missing server is never a failure: the CLI spawns a
	/// throwaway host on demand, so this is purely informational.
	/// </summary>
	private static async Task<DoctorCheck> CheckServerAsync(DoctorOptions options, CancellationToken cancellationToken)
	{
		var url = ClientFactory.ResolveServerUrlOrNull(options.ServerUrl);
		if (url is null)
			return new DoctorCheck("server", DoctorStatus.Ok, "None configured - commands spawn a temporary host as needed.");

		if (options.Offline)
			return new DoctorCheck("server", DoctorStatus.Skipped, $"Skipped - --offline ({url}).");

		try
		{
			using var http = new HttpClient { Timeout = ServerProbeTimeout };
			using var response = await http.GetAsync(new Uri($"{url.TrimEnd('/')}/api/health"), cancellationToken).ConfigureAwait(false);

			return response.IsSuccessStatusCode
				? new DoctorCheck("server", DoctorStatus.Ok, $"Healthy at {url}")
				: new DoctorCheck("server", DoctorStatus.Warning, $"{url} responded {(int)response.StatusCode}.");
		}
		catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
		{
			return new DoctorCheck(
				"server",
				DoctorStatus.Warning,
				$"Not reachable at {url}.",
				$"Start one with `{InvocationStyle.Format("portal")}`, or ignore this - run/exec spawn a temporary host automatically.");
		}
	}
}
