using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Orchestra.Watchers.Models;

namespace Orchestra.Watchers.State;

/// <summary>
/// JSON-file state store. Files live directly under <see cref="RootDirectory"/>:
/// <c>&lt;watcher&gt;.state.json</c> and <c>&lt;watcher&gt;--&lt;orchestration-id&gt;.state.json</c>,
/// both segments sanitized with <see cref="WatcherNames.Sanitize"/>. Writes go to a temp file in
/// the same directory and are moved over the target (<c>File.Move(..., overwrite: true)</c> is
/// atomic on the same volume), so a crash mid-write leaves the previous file intact. Writes are
/// serialized per file; reads of a missing file return a fresh state instead of throwing.
/// </summary>
public sealed partial class FileWatcherStateStore : IWatcherStateStore
{
	private static readonly JsonSerializerOptions s_jsonOptions = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		PropertyNameCaseInsensitive = true,
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
		WriteIndented = true,
		Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
	};

	private readonly ConcurrentDictionary<string, SemaphoreSlim> _fileLocks = new(StringComparer.OrdinalIgnoreCase);
	private readonly ILogger<FileWatcherStateStore> _logger;

	public FileWatcherStateStore(string rootDirectory, ILogger<FileWatcherStateStore> logger)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
		ArgumentNullException.ThrowIfNull(logger);

		RootDirectory = Path.GetFullPath(rootDirectory);
		_logger = logger;
	}

	/// <summary>Directory holding every state file.</summary>
	public string RootDirectory { get; }

	/// <summary>
	/// Resolves the state directory for a given Orchestra configuration directory - the folder
	/// that holds <c>orchestra.json</c> and <c>.orchestra.pids.json</c> (the <c>ProcessTracker</c>
	/// root). State lives in <c>&lt;configDir&gt;/.orchestra/watchers</c>; when the configuration
	/// directory is itself a <c>.orchestra</c> folder (project-local layout) the extra level is
	/// skipped to avoid <c>.orchestra/.orchestra</c>.
	/// </summary>
	public static string ResolveStateDirectory(string configDirectory)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(configDirectory);

		var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(configDirectory));
		return string.Equals(Path.GetFileName(full), ".orchestra", StringComparison.OrdinalIgnoreCase)
			? Path.Combine(full, "watchers")
			: Path.Combine(full, ".orchestra", "watchers");
	}

	/// <summary>Path of the shared state file for <paramref name="watcherName"/>.</summary>
	public string GetSharedPath(string watcherName)
		=> Path.Combine(RootDirectory, $"{WatcherNames.Sanitize(watcherName)}.state.json");

	/// <summary>Path of the per-subscriber state file.</summary>
	public string GetSubscriberPath(string watcherName, string orchestrationId)
		=> Path.Combine(RootDirectory, $"{WatcherNames.Sanitize(watcherName)}{WatcherNames.SubscriberSeparator}{WatcherNames.Sanitize(orchestrationId)}.state.json");

	public async Task<SharedWatcherState> ReadSharedAsync(string watcherName, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(watcherName);

		var path = GetSharedPath(watcherName);
		var state = await ReadAsync<SharedWatcherState>(path, cancellationToken);
		if (state is null)
			return new SharedWatcherState { Watcher = watcherName };

		EnsureVersion(state.Version, path);
		state.Watcher = watcherName;
		state.SharedSeenKeys ??= [];
		state.Stats ??= new SharedWatcherStats();
		return state;
	}

	public Task WriteSharedAsync(SharedWatcherState state, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(state);
		ArgumentException.ThrowIfNullOrWhiteSpace(state.Watcher);

		state.Version = WatcherStateVersion.Current;
		return WriteAsync(GetSharedPath(state.Watcher), state, cancellationToken);
	}

	public async Task<SubscriberWatcherState> ReadSubscriberAsync(string watcherName, string orchestrationId, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(watcherName);
		ArgumentException.ThrowIfNullOrWhiteSpace(orchestrationId);

		var path = GetSubscriberPath(watcherName, orchestrationId);
		var state = await ReadAsync<SubscriberWatcherState>(path, cancellationToken);
		if (state is null)
			return new SubscriberWatcherState { Watcher = watcherName, OrchestrationId = orchestrationId, IsNew = true };

		EnsureVersion(state.Version, path);
		state.Watcher = watcherName;
		state.OrchestrationId = orchestrationId;
		state.SeenKeys ??= [];
		state.AbsenceTickCounts ??= new Dictionary<string, int>(StringComparer.Ordinal);
		state.Stats ??= new SubscriberWatcherStats();
		state.IsNew = false;
		return state;
	}

	public Task WriteSubscriberAsync(SubscriberWatcherState state, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(state);
		ArgumentException.ThrowIfNullOrWhiteSpace(state.Watcher);
		ArgumentException.ThrowIfNullOrWhiteSpace(state.OrchestrationId);

		state.Version = WatcherStateVersion.Current;
		state.IsNew = false;
		return WriteAsync(GetSubscriberPath(state.Watcher, state.OrchestrationId), state, cancellationToken);
	}

	private static void EnsureVersion(int version, string path)
	{
		if (version != WatcherStateVersion.Current)
		{
			throw new WatcherValidationException(
				path,
				$"unsupported state file version {version} (this build reads version {WatcherStateVersion.Current}). Delete the file to reset the watcher's state.");
		}
	}

	private async Task<T?> ReadAsync<T>(string path, CancellationToken cancellationToken) where T : class
	{
		if (!File.Exists(path))
			return null;

		var gate = GetLock(path);
		await gate.WaitAsync(cancellationToken);
		try
		{
			await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 4096, useAsync: true);
			return await JsonSerializer.DeserializeAsync<T>(stream, s_jsonOptions, cancellationToken);
		}
		catch (JsonException ex)
		{
			throw new WatcherValidationException(path, $"state file is not valid JSON - {ex.Message}", ex);
		}
		finally
		{
			gate.Release();
		}
	}

	private async Task WriteAsync<T>(string path, T state, CancellationToken cancellationToken)
	{
		var gate = GetLock(path);
		await gate.WaitAsync(cancellationToken);
		try
		{
			Directory.CreateDirectory(RootDirectory);
			var json = JsonSerializer.Serialize(state, s_jsonOptions);

			// Temp file in the same directory so the final move never crosses volumes.
			var tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
			try
			{
				await File.WriteAllTextAsync(tempPath, json, cancellationToken);
				File.Move(tempPath, path, overwrite: true);
			}
			catch
			{
				TryDelete(tempPath);
				throw;
			}

			LogStateWritten(path);
		}
		finally
		{
			gate.Release();
		}
	}

	private SemaphoreSlim GetLock(string path) => _fileLocks.GetOrAdd(path, static _ => new SemaphoreSlim(1, 1));

	private static void TryDelete(string path)
	{
		try
		{
			if (File.Exists(path))
				File.Delete(path);
		}
		catch (IOException)
		{
			// Best effort: a leftover .tmp is harmless and ignored by readers.
		}
		catch (UnauthorizedAccessException)
		{
		}
	}

	[LoggerMessage(EventId = 5150, Level = LogLevel.Trace, Message = "Watcher state written to {Path}")]
	private partial void LogStateWritten(string path);
}
