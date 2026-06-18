using Orchestra.Client;
using Orchestra.Host.Hosting;

namespace Orchestra.Cli.Commands;

/// <summary>
/// Resolves the Orchestra server URL using the precedence documented on
/// <see cref="GlobalSettings.Server"/>, then constructs a one-shot <see cref="OrchestraClient"/>.
/// Kept separate from the settings class so the resolution rule can be unit-tested without
/// instantiating an HttpClient.
/// </summary>
public static class ClientFactory
{
	public const string DefaultServerUrl = "http://localhost:5000";
	public const string ServerUrlEnvVar = "ORCHESTRA_URL";

	/// <summary>
	/// Returns the effective server URL the CLI will hit. Precedence: explicit <c>--server</c>
	/// flag → <c>$ORCHESTRA_URL</c> → the configured <c>hostBaseUrl</c> (or first <c>urls</c>
	/// entry) from the discovered <c>orchestra.json</c> → the built-in default. The config step
	/// mirrors <c>orchestra run</c>/<c>exec</c> so every verb targets the same instance the
	/// operator configured instead of blindly assuming <see cref="DefaultServerUrl"/>.
	/// </summary>
	/// <remarks>
	/// The <paramref name="envReader"/> and <paramref name="configuredUrlReader"/> seams keep the
	/// rule unit-testable: tests can stub the environment and config lookup without touching the
	/// real process environment or a developer's on-disk <c>orchestra.json</c>.
	/// </remarks>
	public static string ResolveServerUrl(
		string? explicitFlag,
		Func<string, string?>? envReader = null,
		Func<string?>? configuredUrlReader = null)
	{
		if (!string.IsNullOrWhiteSpace(explicitFlag))
		{
			return explicitFlag.Trim();
		}

		var reader = envReader ?? Environment.GetEnvironmentVariable;
		var fromEnv = reader(ServerUrlEnvVar);
		if (!string.IsNullOrWhiteSpace(fromEnv))
		{
			return fromEnv.Trim();
		}

		var fromConfig = (configuredUrlReader ?? ReadConfiguredServerUrl)();
		if (!string.IsNullOrWhiteSpace(fromConfig))
		{
			return fromConfig.Trim();
		}

		return DefaultServerUrl;
	}

	/// <summary>
	/// Like <see cref="ResolveServerUrl"/> but returns <c>null</c> instead of falling back to
	/// <see cref="DefaultServerUrl"/> when nothing is configured. Used by the connect-or-spawn
	/// callers (<c>run</c>/<c>exec</c> and the managed Group-A verbs) where "nothing configured"
	/// means "spawn an isolated host" rather than blindly probing <c>localhost:5000</c>.
	/// Precedence: explicit flag → <c>$ORCHESTRA_URL</c> → <c>orchestra.json</c> → null.
	/// <paramref name="noConfig"/> skips the orchestra.json step (mirrors <c>--no-config</c>).
	/// </summary>
	public static string? ResolveServerUrlOrNull(
		string? explicitFlag,
		bool noConfig = false,
		Func<string, string?>? envReader = null,
		Func<string?>? configuredUrlReader = null)
	{
		var reader = envReader ?? Environment.GetEnvironmentVariable;
		var explicitUrl = (explicitFlag ?? reader(ServerUrlEnvVar))?.Trim();
		if (!string.IsNullOrWhiteSpace(explicitUrl))
		{
			return explicitUrl;
		}

		if (noConfig)
		{
			return null;
		}

		var fromConfig = (configuredUrlReader ?? ReadConfiguredServerUrl)();
		return string.IsNullOrWhiteSpace(fromConfig) ? null : fromConfig.Trim();
	}

	/// <summary>
	/// Best-effort read of the server URL configured in the discovered <c>orchestra.json</c>
	/// (<c>hostBaseUrl</c>, else the first <c>urls</c> entry). Honors the same discovery order as
	/// the host — <c>ORCHESTRA_CONFIG_PATH</c> → <c>XDG_CONFIG_HOME</c> → <c>%APPDATA%</c>/<c>~/.config</c>.
	/// Returns null when no config file is found, nothing relevant is set, or the file can't be
	/// parsed: config discovery must never throw out of a simple client command. Shared with
	/// <c>orchestra run</c>/<c>exec</c> so both resolve the target instance identically.
	/// </summary>
	internal static string? ReadConfiguredServerUrl()
	{
		try
		{
			var config = OrchestraConfigLoader.Load();
			var configured = config?.HostBaseUrl ?? FirstUrl(config?.Urls);
			return string.IsNullOrWhiteSpace(configured) ? null : configured.Trim();
		}
		catch
		{
			// A missing/malformed orchestra.json (or an unset ${VAR} reference inside it) must not
			// block a plain `orchestra list`; fall back to the env/default precedence above.
			return null;
		}
	}

	/// <summary>Returns the first semicolon/comma-separated entry of a <c>urls</c> binding string, or null.</summary>
	internal static string? FirstUrl(string? urls)
	{
		if (string.IsNullOrWhiteSpace(urls))
		{
			return null;
		}

		foreach (var part in urls.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
		{
			return part;
		}

		return null;
	}

	public static OrchestraClient Create(GlobalSettings settings)
		=> new(ResolveServerUrl(settings.Server));
}
