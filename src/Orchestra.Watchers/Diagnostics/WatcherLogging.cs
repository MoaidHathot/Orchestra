using Microsoft.Extensions.Logging;
using Orchestra.Engine;

namespace Orchestra.Watchers.Diagnostics;

/// <summary>
/// Source-generated log messages for the watcher runtime. Event ids 5100-5199 are reserved for
/// watchers (5150+ are used by the state store).
/// </summary>
public static partial class WatcherLogging
{
	[LoggerMessage(EventId = 5101, Level = LogLevel.Debug,
		Message = "Watcher {WatcherName} tick: fetched {ItemCount} items in {ElapsedMs}ms")]
	public static partial void LogTickFetched(this ILogger logger, string watcherName, int itemCount, long elapsedMs);

	[LoggerMessage(EventId = 5102, Level = LogLevel.Information,
		Message = "Watcher {WatcherName} subscriber {OrchestrationId}: dispatched {DispatchCount} new items")]
	public static partial void LogDispatched(this ILogger logger, string watcherName, string orchestrationId, int dispatchCount);

	[LoggerMessage(EventId = 5103, Level = LogLevel.Warning,
		Message = "Watcher {WatcherName} fetch failed (exit {ExitCode}): {Reason}")]
	public static partial void LogFetchFailed(this ILogger logger, string watcherName, int exitCode, string reason);

	[LoggerMessage(EventId = 5104, Level = LogLevel.Warning,
		Message = "Watcher {WatcherName} subscriber {OrchestrationId}: FIFO trim rate {EvictionsPerHour}/hr at maxEntries={MaxEntries}. Possible duplicate dispatches; consider mode=presence, a larger window, or a cursor-aware fetcher.")]
	public static partial void LogHighTrimRate(this ILogger logger, string watcherName, string orchestrationId, double evictionsPerHour, int maxEntries);

	[LoggerMessage(EventId = 5105, Level = LogLevel.Error,
		Message = "Watcher {WatcherName} fetcher output failed schema validation: {Pointer} - {Message}")]
	public static partial void LogValidationFailed(this ILogger logger, string watcherName, string pointer, string message);

	[LoggerMessage(EventId = 5106, Level = LogLevel.Information,
		Message = "Watcher {WatcherName} subscription added: {OrchestrationId} (catchup={Catchup})")]
	public static partial void LogSubscriptionAdded(this ILogger logger, string watcherName, string orchestrationId, WatcherCatchupMode catchup);

	[LoggerMessage(EventId = 5107, Level = LogLevel.Information,
		Message = "Watcher {WatcherName} subscription removed: {OrchestrationId}")]
	public static partial void LogSubscriptionRemoved(this ILogger logger, string watcherName, string orchestrationId);

	[LoggerMessage(EventId = 5108, Level = LogLevel.Information,
		Message = "Watcher {WatcherName} started (first subscriber)")]
	public static partial void LogWatcherStarted(this ILogger logger, string watcherName);

	[LoggerMessage(EventId = 5109, Level = LogLevel.Information,
		Message = "Watcher {WatcherName} stopped (no subscribers)")]
	public static partial void LogWatcherStopped(this ILogger logger, string watcherName);

	[LoggerMessage(EventId = 5110, Level = LogLevel.Error,
		Message = "Watcher {WatcherName} configuration error: {Reason}")]
	public static partial void LogConfigError(this ILogger logger, string watcherName, string reason);

	[LoggerMessage(EventId = 5111, Level = LogLevel.Debug,
		Message = "Watcher {WatcherName} fetch failed (exit {ExitCode}), ignored by policy: {Reason}")]
	public static partial void LogFetchFailedIgnored(this ILogger logger, string watcherName, int exitCode, string reason);

	[LoggerMessage(EventId = 5112, Level = LogLevel.Error,
		Message = "Watcher {WatcherName} fetch failed (exit {ExitCode}) and failurePolicy=fail: stopping this watcher's loop. {Reason}")]
	public static partial void LogFetchFailedStopping(this ILogger logger, string watcherName, int exitCode, string reason);

	[LoggerMessage(EventId = 5113, Level = LogLevel.Warning,
		Message = "Watcher {WatcherName} fetcher stderr: {StdErr}")]
	public static partial void LogFetcherStdErr(this ILogger logger, string watcherName, string stdErr);

	[LoggerMessage(EventId = 5114, Level = LogLevel.Information,
		Message = "Watcher {WatcherName} fetcher diagnostics: {Message}")]
	public static partial void LogFetcherDiagnostics(this ILogger logger, string watcherName, string message);

	[LoggerMessage(EventId = 5115, Level = LogLevel.Error,
		Message = "Watcher {WatcherName} subscriber {OrchestrationId}: dispatch failed for item {DedupKey}")]
	public static partial void LogDispatchFailed(this ILogger logger, Exception exception, string watcherName, string orchestrationId, string dedupKey);

	[LoggerMessage(EventId = 5116, Level = LogLevel.Error,
		Message = "Watcher {WatcherName} loop crashed; the watcher is stopped until its subscriptions change or the host restarts")]
	public static partial void LogLoopCrashed(this ILogger logger, Exception exception, string watcherName);

	[LoggerMessage(EventId = 5117, Level = LogLevel.Information,
		Message = "Loaded {Count} watcher definitions from {Path}")]
	public static partial void LogLibraryLoaded(this ILogger logger, int count, string path);

	[LoggerMessage(EventId = 5118, Level = LogLevel.Debug,
		Message = "Watcher {WatcherName} next tick at {NextTick:O} (backoff after {ConsecutiveFailures} failures)")]
	public static partial void LogNextTick(this ILogger logger, string watcherName, DateTimeOffset nextTick, int consecutiveFailures);

	[LoggerMessage(EventId = 5119, Level = LogLevel.Warning,
		Message = "Watcher {WatcherName} state could not be read; starting from empty state: {Reason}")]
	public static partial void LogStateReadFailed(this ILogger logger, string watcherName, string reason);

	[LoggerMessage(EventId = 5120, Level = LogLevel.Error,
		Message = "Watcher {WatcherName} state could not be written")]
	public static partial void LogStateWriteFailed(this ILogger logger, Exception exception, string watcherName);
}
