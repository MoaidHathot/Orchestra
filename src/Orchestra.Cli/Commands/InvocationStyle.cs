namespace Orchestra.Cli.Commands;

/// <summary>
/// Detects how the tool was launched so "next step" hints print a command the user can
/// actually paste back.
/// </summary>
/// <remarks>
/// <c>dnx</c> runs the tool straight out of the NuGet package cache without installing it, so
/// telling that user to type <c>orchestra …</c> hands them a command that does not exist on
/// their PATH. A global install resolves under the dotnet tools store instead. When neither
/// layout matches we assume a global install, which is the form the docs lead with.
/// </remarks>
internal static class InvocationStyle
{
	/// <summary>
	/// Command prefix for user-facing hints: either <c>orchestra</c> or
	/// <c>dnx Orchestra --yes --</c>.
	/// </summary>
	internal static string CommandPrefix { get; } = Detect(AppContext.BaseDirectory);

	/// <summary>Formats <paramref name="commandLine"/> with the detected launcher prefix.</summary>
	internal static string Format(string commandLine) => $"{CommandPrefix} {commandLine}";

	/// <summary>
	/// Exposed for testing: classifies a base directory as a dnx (package cache) or global
	/// tool (tools store) layout.
	/// </summary>
	internal static string Detect(string baseDirectory)
	{
		if (string.IsNullOrWhiteSpace(baseDirectory))
			return "orchestra";

		var normalized = baseDirectory.Replace('\\', '/');

		// A global install always lands under the tool store, even when the user has
		// relocated DOTNET_TOOLS_PATH — check it first so it wins over any cache-shaped path.
		if (normalized.Contains("/.store/", StringComparison.OrdinalIgnoreCase))
			return "orchestra";

		if (normalized.Contains("/.nuget/packages/", StringComparison.OrdinalIgnoreCase)
			|| normalized.Contains("/packages/orchestra/", StringComparison.OrdinalIgnoreCase))
		{
			return "dnx Orchestra --yes --";
		}

		return "orchestra";
	}
}
