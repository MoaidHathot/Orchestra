using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Orchestra.Host.Logging;

/// <summary>
/// Buffered file-based logging provider for Orchestra hosting applications.
/// Uses a Channel for lock-free, non-blocking log writes with background flushing.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider, IAsyncDisposable
{
	private readonly string _path;
	private readonly LogLevel _minimumLevel;
	private readonly Channel<string> _channel;
	private readonly Task _writeTask;
	private readonly CancellationTokenSource _cts = new();

	public FileLoggerProvider(string path, LogLevel minimumLevel = LogLevel.Information)
	{
		_path = path;
		_minimumLevel = minimumLevel;

		// Ensure directory exists
		var dir = Path.GetDirectoryName(path);
		if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
			Directory.CreateDirectory(dir);

		// Unbounded channel — producers never block.
		// BoundedChannel with DropOldest could be used to cap memory in extreme scenarios.
		_channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
		{
			SingleReader = true,
			AllowSynchronousContinuations = false,
		});

		_writeTask = Task.Run(WriteLoopAsync);
	}

	public ILogger CreateLogger(string categoryName) => new FileLogger(_path, categoryName, _channel.Writer, _minimumLevel);

	private async Task WriteLoopAsync()
	{
		try
		{
			await using var writer = new StreamWriter(_path, append: true) { AutoFlush = false };
			var reader = _channel.Reader;

			// WaitToReadAsync returns false when the channel is completed and drained
			while (await reader.WaitToReadAsync(_cts.Token))
			{
				while (reader.TryRead(out var message))
				{
					await writer.WriteLineAsync(message);
				}
				await writer.FlushAsync();
			}

			// Drain any remaining items after channel completion signal
			while (reader.TryRead(out var remaining))
			{
				await writer.WriteLineAsync(remaining);
			}
			await writer.FlushAsync();
		}
		catch (OperationCanceledException) { }
		catch (ChannelClosedException) { }
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
}

/// <summary>
/// File-based logger that writes to a Channel for lock-free, non-blocking operation.
/// </summary>
public sealed class FileLogger : ILogger
{
	private readonly string _path;
	private readonly string _category;
	private readonly ChannelWriter<string> _writer;
	private readonly LogLevel _minimumLevel;

	public FileLogger(string path, string category, ChannelWriter<string> writer, LogLevel minimumLevel = LogLevel.Information)
	{
		_path = path;
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

		var message = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{logLevel}] {_category}: {formatter(state, exception)}";
		if (exception != null)
			message += $"\n{exception}";

		// Non-blocking write — TryWrite returns false only if the channel is completed
		_writer.TryWrite(message);
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
