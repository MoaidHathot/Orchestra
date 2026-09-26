using Microsoft.Extensions.Logging;

namespace Orchestra.Host.Extensions;

/// <summary>
/// Source-generated log messages for host initialization (<c>InitializeOrchestraHostAsync</c>).
/// </summary>
internal static partial class HostInitializationLog
{
	[LoggerMessage(
		EventId = 1,
		Level = LogLevel.Information,
		Message = "Ignoring user-global {FileName} at '{GlobalPath}' because the project configuration '{ProjectConfigPath}' is in effect; sidecars are read only from the directory of the orchestra.json that wins discovery. Copy the file next to that orchestra.json to use it in this workspace.")]
	public static partial void GlobalSidecarShadowed(ILogger logger, string fileName, string globalPath, string projectConfigPath);
}
