namespace Orchestra.Engine;

/// <summary>One directory rule for agent, command, and script execution.</summary>
internal static class ExecutionWorkingDirectory
{
	public static string? Resolve(string? configuredDirectory, string? sourceDirectory)
	{
		if (string.IsNullOrWhiteSpace(configuredDirectory))
			return string.IsNullOrWhiteSpace(sourceDirectory) ? null : Path.GetFullPath(sourceDirectory);

		return Path.IsPathRooted(configuredDirectory)
			? Path.GetFullPath(configuredDirectory)
			: Path.GetFullPath(configuredDirectory, string.IsNullOrWhiteSpace(sourceDirectory)
				? Environment.CurrentDirectory : Path.GetFullPath(sourceDirectory));
	}
}
