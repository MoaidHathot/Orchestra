using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Orchestra.Host.Logging;

/// <summary>
/// Settings for the host's file log. Sourced from <c>orchestra.json</c> (<c>logging.file</c>)
/// with the directory defaulting to <c>{dataPath}/logs</c>.
/// </summary>
public sealed class FileLoggingOptions
{
	/// <summary>Default file-name prefix: <c>orchestra-2026-09-27.log</c>.</summary>
	public const string DefaultFilePrefix = "orchestra";

	/// <summary>Default number of daily files kept.</summary>
	public const int DefaultRetainedFiles = 14;

	/// <summary>Whether the host writes a log file at all. Default true.</summary>
	public bool Enabled { get; set; } = true;

	/// <summary>
	/// Directory that receives the daily files. Null means <c>{dataPath}/logs</c>; the host
	/// resolves it when it builds the provider.
	/// </summary>
	public string? Directory { get; set; }

	/// <summary>File-name prefix; one file per calendar day: <c>{prefix}-yyyy-MM-dd.log</c>.</summary>
	public string FilePrefix { get; set; } = DefaultFilePrefix;

	/// <summary>Minimum level written to the file (the console keeps its own level).</summary>
	public LogLevel MinimumLevel { get; set; } = LogLevel.Information;

	/// <summary>How many daily files to keep; older ones are deleted when a new day starts.</summary>
	public int RetainedFiles { get; set; } = DefaultRetainedFiles;

	/// <summary>The directory in effect for <paramref name="dataPath"/>.</summary>
	public string ResolveDirectory(string dataPath)
		=> string.IsNullOrWhiteSpace(Directory) ? Path.Combine(dataPath, "logs") : Directory;

	/// <summary>The file that receives entries stamped <paramref name="day"/>.</summary>
	public string FileNameFor(DateOnly day) => $"{FilePrefix}-{day:yyyy-MM-dd}.log";

	/// <summary>Glob matching every file this configuration may have written.</summary>
	public string FilePattern => $"{FilePrefix}-*.log";
}

/// <summary>
/// Buffered file-based logging provider for Orchestra hosting applications.
/// Uses a Channel for lock-free, non-blocking log writes with background flushing.
/// </summary>
/// <remarks>
/// Two modes: a single fixed file (<see cref="FileLoggerProvider(string, LogLevel)"/>), and
/// daily rolling files with retention (<see cref="FileLoggerProvider(FileLoggingOptions, string)"/>),
/// which is what the host uses so a long-running server cannot grow one file without bound.
/// </remarks>
[ProviderAlias("OrchestraFile")]
public sealed class FileLoggerProvider : ILoggerProvider, IAsyncDisposable
{
	private readonly string? _fixedPath;
	private readonly FileLoggingOptions? _rolling;
	private readonly string? _directory;
	private readonly LogLevel _minimumLevel;
	private readonly Channel<LogEntry> _channel;
	private readonly Task _writeTask;
	private readonly CancellationTokenSource _cts = new();

	/// <summary>Writes every entry to one file at <paramref name="path"/>.</summary>
	public FileLoggerProvider(string path, LogLevel minimumLevel = LogLevel.Information)
	{
		_fixedPath = path;
		_minimumLevel = minimumLevel;

		// Ensure directory exists
		var dir = Path.GetDirectoryName(path);
		if (!string.IsNullOrEmpty(dir) && !System.IO.Directory.Exists(dir))
			System.IO.Directory.CreateDirectory(dir);

		_channel = CreateChannel();
		_writeTask = Task.Run(WriteLoopAsync);
	}

	/// <summary>
	/// Writes to one file per calendar day under <see cref="FileLoggingOptions.ResolveDirectory"/>,
	/// deleting files beyond <see cref="FileLoggingOptions.RetainedFiles"/> whenever a new day
	/// starts.
	/// </summary>
	public FileLoggerProvider(FileLoggingOptions options, string dataPath)
	{
		ArgumentNullException.ThrowIfNull(options);
		_rolling = options;
		_directory = options.ResolveDirectory(dataPath);
		_minimumLevel = options.MinimumLevel;
		System.IO.Directory.CreateDirectory(_directory);

		_channel = CreateChannel();
		_writeTask = Task.Run(WriteLoopAsync);
	}

	private static Channel<LogEntry> CreateChannel()
		// Unbounded channel — producers never block.
		=> Channel.CreateUnbounded<LogEntry>(new UnboundedChannelOptions
		{
			SingleReader = true,
			AllowSynchronousContinuations = false,
		});

	/// <summary>The directory receiving daily files, or null in fixed-file mode.</summary>
	public string? Directory => _directory;

	/// <summary>The file that receives entries stamped <paramref name="timestamp"/>.</summary>
	public string PathFor(DateTime timestamp)
		=> _fixedPath ?? Path.Combine(_directory!, _rolling!.FileNameFor(DateOnly.FromDateTime(timestamp)));

	public ILogger CreateLogger(string categoryName) => new FileLogger(categoryName, _channel.Writer, _minimumLevel);

	private async Task WriteLoopAsync()
	{
		// Open the file lazily on the first actual message so that a logger which never writes
		// anything (e.g. everything is below the minimum level) does not create an empty log file.
		StreamWriter? writer = null;
		string? openPath = null;
		try
		{
			var reader = _channel.Reader;

			// WaitToReadAsync returns false when the channel is completed and drained
			while (await reader.WaitToReadAsync(_cts.Token))
			{
				var wrote = false;
				while (reader.TryRead(out var entry))
				{
					(writer, openPath) = await EnsureWriterAsync(writer, openPath, entry.Timestamp);
					await writer.WriteLineAsync(entry.Text);
					wrote = true;
				}
				if (wrote)
					await writer!.FlushAsync();
			}

			// Drain any remaining items after channel completion signal
			while (reader.TryRead(out var remaining))
			{
				(writer, openPath) = await EnsureWriterAsync(writer, openPath, remaining.Timestamp);
				await writer.WriteLineAsync(remaining.Text);
			}
			if (writer is not null)
				await writer.FlushAsync();
		}
		catch (OperationCanceledException) { }
		catch (ChannelClosedException) { }
		finally
		{
			if (writer is not null)
				await writer.DisposeAsync();
		}
	}

	/// <summary>
	/// Returns a writer for the file that <paramref name="timestamp"/> belongs to, reusing
	/// <paramref name="current"/> when it is already open on that file (fixed mode, or same day).
	/// </summary>
	private async ValueTask<(StreamWriter Writer, string Path)> EnsureWriterAsync(StreamWriter? current, string? openPath, DateTime timestamp)
	{
		var target = PathFor(timestamp);
		if (current is not null && string.Equals(openPath, target, StringComparison.Ordinal))
			return (current, target);

		if (current is not null)
			await current.DisposeAsync();

		var writer = new StreamWriter(target, append: true) { AutoFlush = false };

		if (_rolling is not null)
			PruneOldFiles();

		return (writer, target);
	}

	/// <summary>Deletes the oldest daily files beyond <see cref="FileLoggingOptions.RetainedFiles"/>.</summary>
	private void PruneOldFiles()
	{
		if (_rolling is null || _directory is null || _rolling.RetainedFiles <= 0)
			return;

		try
		{
			// Names embed yyyy-MM-dd, so ordinal order is chronological order.
			var files = System.IO.Directory.GetFiles(_directory, _rolling.FilePattern)
				.OrderBy(f => f, StringComparer.Ordinal)
				.ToList();
			for (var i = 0; i < files.Count - _rolling.RetainedFiles; i++)
			{
				try { File.Delete(files[i]); }
				catch (IOException) { /* another process may hold it; retry on the next roll */ }
				catch (UnauthorizedAccessException) { }
			}
		}
		catch (IOException) { }
		catch (UnauthorizedAccessException) { }
	}

	public void Dispose()
	{
		// Complete the channel — no new writes accepted
		_channel.Writer.TryComplete();

		// Wait for the write loop to drain all pending items
		try { _writeTask.Wait(TimeSpan.FromSeconds(5)); } catch { }

		// Cancel and dispose as final cleanup
		_cts.Cancel();
		_cts.Dispose();
	}

	public async ValueTask DisposeAsync()
	{
		// Complete the channel — no new writes accepted
		_channel.Writer.TryComplete();

		// Wait for the write loop to drain all pending items
		try
		{
			await _writeTask.WaitAsync(TimeSpan.FromSeconds(5));
		}
		catch (TimeoutException) { }
		catch (OperationCanceledException) { }

		// Cancel and dispose as final cleanup
		_cts.Cancel();
		_cts.Dispose();
	}

	/// <summary>A formatted line plus the local timestamp that decides which daily file it lands in.</summary>
	internal readonly record struct LogEntry(DateTime Timestamp, string Text);
}

/// <summary>
/// File-based logger that writes to a Channel for lock-free, non-blocking operation.
/// </summary>
public sealed class FileLogger : ILogger
{
	private readonly string _category;
	private readonly ChannelWriter<FileLoggerProvider.LogEntry> _writer;
	private readonly LogLevel _minimumLevel;

	internal FileLogger(string category, ChannelWriter<FileLoggerProvider.LogEntry> writer, LogLevel minimumLevel = LogLevel.Information)
	{
		_category = category;
		_writer = writer;
		_minimumLevel = minimumLevel;
	}

	public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

	public bool IsEnabled(LogLevel logLevel) => logLevel >= _minimumLevel;

	public void Log<TState>(
		LogLevel logLevel,
		EventId eventId,
		TState state,
		Exception? exception,
		Func<TState, Exception?, string> formatter)
	{
		if (!IsEnabled(logLevel)) return;

		var now = DateTime.Now;
		var message = $"{now:yyyy-MM-dd HH:mm:ss} [{logLevel}] {_category}: {formatter(state, exception)}";
		if (exception != null)
			message += $"\n{exception}";

		// Non-blocking write — TryWrite returns false only if the channel is completed
		_writer.TryWrite(new FileLoggerProvider.LogEntry(now, message));
	}
}

/// <summary>
/// Extension methods for adding file logging.
/// </summary>
public static class FileLoggingExtensions
{
	/// <summary>
	/// Adds file-based logging to the logging builder.
	/// </summary>
	public static ILoggingBuilder AddFile(this ILoggingBuilder builder, string path, LogLevel minimumLevel = LogLevel.Information)
	{
		var dir = Path.GetDirectoryName(path);
		if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
			Directory.CreateDirectory(dir);

		builder.AddProvider(new FileLoggerProvider(path, minimumLevel));
		return builder;
	}
}
