using Microsoft.Extensions.Logging;

namespace Orchestra.Host.Extensions;

/// <summary>
/// Source-generated log messages for host initialization (<c>InitializeOrchestraHostAsync</c>
/// and <c>OrchestraConfigLoader</c>).
/// </summary>
internal static partial class HostInitializationLog
{
	[LoggerMessage(
		EventId = 1,
		Level = LogLevel.Information,
		Message = "Ignoring user-global {FileName} at '{GlobalPath}' because the project configuration '{ProjectConfigPath}' is in effect; sidecars are read only from the directory of the orchestra.json that wins discovery. Copy the file next to that orchestra.json to use it in this workspace.")]
	public static partial void GlobalSidecarShadowed(ILogger logger, string fileName, string globalPath, string projectConfigPath);

	[LoggerMessage(
		EventId = 2,
		Level = LogLevel.Warning,
		Message = "orchestra.json: ignoring defaultMaxConcurrency={Value}; it must be a positive integer. Step concurrency stays unbounded for orchestrations that set no maxConcurrency.")]
	public static partial void InvalidDefaultMaxConcurrency(ILogger logger, int value);
}
