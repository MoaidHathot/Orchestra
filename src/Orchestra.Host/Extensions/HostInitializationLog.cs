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

	[LoggerMessage(
		EventId = 3,
		Level = LogLevel.Warning,
		Message = "orchestra.json: ignoring logging.file.level='{Value}'; expected Trace, Debug, Information, Warning, Error or Critical. The file keeps the host's logLevel.")]
	public static partial void InvalidFileLogLevel(ILogger logger, string value);

	[LoggerMessage(
		EventId = 4,
		Level = LogLevel.Warning,
		Message = "orchestra.json: ignoring logging.file.retainedFiles={Value}; it must be at least 1. Keeping the default of 14 daily files.")]
	public static partial void InvalidFileLogRetention(ILogger logger, int value);

	[LoggerMessage(
		EventId = 5,
		Level = LogLevel.Information,
		Message = "Host log file: {Path} (minimum level {MinimumLevel}, {RetainedFiles} daily files kept)")]
	public static partial void FileLoggingEnabled(ILogger logger, string path, LogLevel minimumLevel, int retainedFiles);

	[LoggerMessage(
		EventId = 6,
		Level = LogLevel.Warning,
		Message = "Host log file disabled: the log directory '{Directory}' is not writable. Console logging continues.")]
	public static partial void FileLoggingUnavailable(ILogger logger, string directory, Exception exception);
}
