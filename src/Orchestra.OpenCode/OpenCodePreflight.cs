namespace Orchestra.OpenCode;

/// <summary>
/// Result of looking for the OpenCode CLI on this machine.
/// </summary>
/// <param name="Available">True when a launchable binary was found.</param>
/// <param name="Path">Resolved executable path, or the bare command when resolution failed.</param>
/// <param name="Source">How the command was chosen: config, environment variable, or PATH.</param>
public sealed record OpenCodeCliProbe(bool Available, string Path, string Source);

/// <summary>
/// Read-only prerequisite check for the OpenCode provider, surfaced by <c>orchestra doctor</c>.
/// </summary>
/// <remarks>
/// Unlike Copilot, OpenCode is never downloaded — it must already be installed. Today a missing
/// binary only surfaces when the first Prompt step tries to spawn <c>opencode serve</c>, so this
/// moves the answer to before the run.
/// </remarks>
public static class OpenCodePreflight
{
	/// <summary>
	/// Resolves the OpenCode executable using the same precedence as the runtime
	/// (<c>orchestra.json</c> <c>opencode.cliPath</c> → <c>ORCHESTRA_OPENCODE_PATH</c> → PATH),
	/// without launching anything.
	/// </summary>
	/// <param name="configuredCliPath">The <c>opencode.cliPath</c> value from configuration, if any.</param>
	public static OpenCodeCliProbe Inspect(string? configuredCliPath = null)
	{
		var envPath = Environment.GetEnvironmentVariable(OpenCodeServerBootstrap.ExplicitCliPathEnvVar);

		var (command, source) = !string.IsNullOrWhiteSpace(configuredCliPath)
			? (configuredCliPath, "orchestra.json opencode.cliPath")
			: !string.IsNullOrWhiteSpace(envPath)
				? (envPath, OpenCodeServerBootstrap.ExplicitCliPathEnvVar)
				: ("opencode", "PATH");

		// ResolveExecutable hand-rolls PATH+PATHEXT lookup and returns the bare command
		// unchanged when nothing matches, so a returned path equal to the input means
		// "not found" for anything that wasn't already an absolute path.
		var resolved = OpenCodeServerBootstrap.ResolveExecutable(command);
		var available = File.Exists(resolved);

		return new OpenCodeCliProbe(available, resolved, source);
	}
}
