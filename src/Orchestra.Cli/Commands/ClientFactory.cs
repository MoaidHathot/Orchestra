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
	/// Returns the effective server URL the CLI will hit. Pure: no I/O beyond reading the
	/// environment variable supplied by the caller (the real <c>Environment</c> by default,
	/// or a stub in tests).
	/// </summary>
	public static string ResolveServerUrl(string? explicitFlag, Func<string, string?>? envReader = null)
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

		return DefaultServerUrl;
	}

	public static OrchestraClient Create(GlobalSettings settings)
		=> new(ResolveServerUrl(settings.Server));
}
