using Microsoft.Extensions.Logging;

namespace Orchestra.Host.Logging;

/// <summary>
/// Simple file-based logging provider for Orchestra hosting applications.
/// </summary>
public class FileLoggerProvider : ILoggerProvider
{
	private readonly string _path;
	private readonly object _lock = new();

	public FileLoggerProvider(string path)
	{
		_path = path;

		// Ensure directory exists
		var dir = Path.GetDirectoryName(path);
		if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
			Directory.CreateDirectory(dir);
	}

	public ILogger CreateLogger(string categoryName) => new FileLogger(_path, categoryName, _lock);

	public void Dispose() { }
}

/// <summary>
/// File-based logger implementation.
/// </summary>
public class FileLogger : ILogger
{
	private readonly string _path;
	private readonly string _category;
	private readonly object _lock;

	public FileLogger(string path, string category, object lockObj)
	{
		_path = path;
		_category = category;
		_lock = lockObj;
	}

	public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

	public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

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

		lock (_lock)
		{
			File.AppendAllText(_path, message + "\n");
		}
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
	public static ILoggingBuilder AddFile(this ILoggingBuilder builder, string path)
	{
		var dir = Path.GetDirectoryName(path);
		if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
			Directory.CreateDirectory(dir);

		builder.AddProvider(new FileLoggerProvider(path));
		return builder;
	}
}
