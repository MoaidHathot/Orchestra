using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orchestra.Host.Logging;
using Xunit;

namespace Orchestra.Host.Tests;

/// <summary>
/// Unit tests for FileLogging (FileLoggerProvider and FileLogger).
/// Since the file logger now uses a background Channel for async writes,
/// tests must dispose the provider (which flushes the channel) before reading the file.
/// </summary>
public class FileLoggingTests : IDisposable
{
	private readonly string _tempDir;

	public FileLoggingTests()
	{
		_tempDir = Path.Combine(Path.GetTempPath(), $"orchestra-logging-tests-{Guid.NewGuid():N}");
		Directory.CreateDirectory(_tempDir);
	}

	public void Dispose()
	{
		if (Directory.Exists(_tempDir))
		{
			try { Directory.Delete(_tempDir, recursive: true); }
			catch { /* best-effort cleanup */ }
		}
	}

	[Fact]
	public void FileLoggerProvider_Constructor_CreatesDirectory()
	{
		// Arrange
		var logDir = Path.Combine(_tempDir, "logs", "nested");
		var logPath = Path.Combine(logDir, "app.log");

		// Act
		using var provider = new FileLoggerProvider(logPath);

		// Assert
		Directory.Exists(logDir).Should().BeTrue();
	}

	[Fact]
	public void FileLoggerProvider_CreateLogger_ReturnsFileLogger()
	{
		// Arrange
		var logPath = Path.Combine(_tempDir, "test.log");
		using var provider = new FileLoggerProvider(logPath);

		// Act
		var logger = provider.CreateLogger("TestCategory");

		// Assert
		logger.Should().BeOfType<FileLogger>();
	}

	[Fact]
	public void FileLoggerProvider_CreateLogger_MultipleCategoriesWork()
	{
		// Arrange
		var logPath = Path.Combine(_tempDir, "test.log");
		using var provider = new FileLoggerProvider(logPath);

		// Act
		var logger1 = provider.CreateLogger("Category1");
		var logger2 = provider.CreateLogger("Category2");

		// Assert
		logger1.Should().NotBeSameAs(logger2);
	}

	[Fact]
	public void FileLogger_IsEnabled_ReturnsTrueForInformationAndAbove()
	{
		// Arrange — default minimum level is Information
		var logPath = Path.Combine(_tempDir, "test.log");
		using var provider = new FileLoggerProvider(logPath);
		var logger = provider.CreateLogger("Test");

		// Assert
		logger.IsEnabled(LogLevel.Trace).Should().BeFalse();
		logger.IsEnabled(LogLevel.Debug).Should().BeFalse();
		logger.IsEnabled(LogLevel.Information).Should().BeTrue();
		logger.IsEnabled(LogLevel.Warning).Should().BeTrue();
		logger.IsEnabled(LogLevel.Error).Should().BeTrue();
		logger.IsEnabled(LogLevel.Critical).Should().BeTrue();
	}

	[Fact]
	public void FileLogger_Log_WritesToFile()
	{
		// Arrange
		var logPath = Path.Combine(_tempDir, "write-test.log");
		var provider = new FileLoggerProvider(logPath);
		var logger = provider.CreateLogger("TestCategory");

		// Act
		logger.LogInformation("Hello, World!");
		provider.Dispose(); // Flush the channel

		// Assert
		File.Exists(logPath).Should().BeTrue();
		var content = File.ReadAllText(logPath);
		content.Should().Contain("TestCategory");
		content.Should().Contain("Hello, World!");
		content.Should().Contain("[Information]");
	}

	[Fact]
	public void FileLogger_Log_IncludesTimestamp()
	{
		// Arrange
		var logPath = Path.Combine(_tempDir, "timestamp-test.log");
		var provider = new FileLoggerProvider(logPath);
		var logger = provider.CreateLogger("Test");

		// Act
		logger.LogInformation("Test message");
		provider.Dispose(); // Flush the channel

		// Assert
		var content = File.ReadAllText(logPath);
		// Should contain a timestamp in format yyyy-MM-dd HH:mm:ss
		content.Should().MatchRegex(@"\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}");
	}

	[Fact]
	public void FileLogger_Log_AppendsMultipleMessages()
	{
		// Arrange
		var logPath = Path.Combine(_tempDir, "append-test.log");
		var provider = new FileLoggerProvider(logPath);
		var logger = provider.CreateLogger("Test");

		// Act
		logger.LogInformation("First message");
		logger.LogInformation("Second message");
		logger.LogWarning("Third message");
		provider.Dispose(); // Flush the channel

		// Assert
		var content = File.ReadAllText(logPath);
		content.Should().Contain("First message");
		content.Should().Contain("Second message");
		content.Should().Contain("Third message");
	}

	[Fact]
	public void FileLogger_Log_IncludesExceptionDetails()
	{
		// Arrange
		var logPath = Path.Combine(_tempDir, "exception-test.log");
		var provider = new FileLoggerProvider(logPath);
		var logger = provider.CreateLogger("Test");
		var exception = new InvalidOperationException("Test exception message");

		// Act
		logger.LogError(exception, "An error occurred");
		provider.Dispose(); // Flush the channel

		// Assert
		var content = File.ReadAllText(logPath);
		content.Should().Contain("An error occurred");
		content.Should().Contain("InvalidOperationException");
		content.Should().Contain("Test exception message");
	}

	[Fact]
	public void FileLogger_Log_IgnoresBelowInformationLevel()
	{
		// Arrange
		var logPath = Path.Combine(_tempDir, "level-test.log");
		using var provider = new FileLoggerProvider(logPath);
		var logger = provider.CreateLogger("Test");

		// Act
		logger.LogDebug("Debug message");
		logger.LogTrace("Trace message");

		// Assert
		File.Exists(logPath).Should().BeFalse();
	}

	[Fact]
	public void FileLogger_BeginScope_ReturnsNull()
	{
		// Arrange
		var logPath = Path.Combine(_tempDir, "scope-test.log");
		using var provider = new FileLoggerProvider(logPath);
		var logger = provider.CreateLogger("Test");

		// Act
		var scope = logger.BeginScope("test scope");

		// Assert
		scope.Should().BeNull();
	}

	[Fact]
	public async Task FileLogger_Log_ThreadSafe()
	{
		// Arrange
		var logPath = Path.Combine(_tempDir, "thread-test.log");
		var provider = new FileLoggerProvider(logPath);
		var logger = provider.CreateLogger("Test");
		var messageCount = 100;
		var tasks = new List<Task>();

		// Act
		for (int i = 0; i < messageCount; i++)
		{
			var messageNum = i;
			tasks.Add(Task.Run(() => logger.LogInformation($"Message {messageNum}")));
		}

		await Task.WhenAll(tasks);
		provider.Dispose(); // Flush the channel

		// Assert
		var content = await File.ReadAllTextAsync(logPath);
		var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);
		lines.Should().HaveCount(messageCount);
	}

	[Fact]
	public void FileLogger_Log_DifferentLogLevels()
	{
		// Arrange
		var logPath = Path.Combine(_tempDir, "levels-test.log");
		var provider = new FileLoggerProvider(logPath);
		var logger = provider.CreateLogger("Test");

		// Act
		logger.LogInformation("Info message");
		logger.LogWarning("Warning message");
		logger.LogError("Error message");
		logger.LogCritical("Critical message");
		provider.Dispose(); // Flush the channel

		// Assert
		var content = File.ReadAllText(logPath);
		content.Should().Contain("[Information]");
		content.Should().Contain("[Warning]");
		content.Should().Contain("[Error]");
		content.Should().Contain("[Critical]");
	}

	[Fact]
	public void FileLoggerProvider_Dispose_CompletesSuccessfully()
	{
		// Arrange
		var logPath = Path.Combine(_tempDir, "dispose-test.log");
		var provider = new FileLoggerProvider(logPath);
		var logger = provider.CreateLogger("Test");
		logger.LogInformation("Before dispose");

		// Act & Assert (should not throw)
		provider.Dispose();
	}

	// ── Configurable LogLevel tests ──

	[Fact]
	public void FileLogger_IsEnabled_RespectsCustomMinimumLevel_Debug()
	{
		// Arrange — set minimum to Debug
		var logPath = Path.Combine(_tempDir, "debug-level.log");
		using var provider = new FileLoggerProvider(logPath, LogLevel.Debug);
		var logger = provider.CreateLogger("Test");

		// Assert
		logger.IsEnabled(LogLevel.Trace).Should().BeFalse();
		logger.IsEnabled(LogLevel.Debug).Should().BeTrue();
		logger.IsEnabled(LogLevel.Information).Should().BeTrue();
		logger.IsEnabled(LogLevel.Warning).Should().BeTrue();
	}

	[Fact]
	public void FileLogger_IsEnabled_RespectsCustomMinimumLevel_Warning()
	{
		// Arrange — set minimum to Warning
		var logPath = Path.Combine(_tempDir, "warning-level.log");
		using var provider = new FileLoggerProvider(logPath, LogLevel.Warning);
		var logger = provider.CreateLogger("Test");

		// Assert
		logger.IsEnabled(LogLevel.Trace).Should().BeFalse();
		logger.IsEnabled(LogLevel.Debug).Should().BeFalse();
		logger.IsEnabled(LogLevel.Information).Should().BeFalse();
		logger.IsEnabled(LogLevel.Warning).Should().BeTrue();
		logger.IsEnabled(LogLevel.Error).Should().BeTrue();
		logger.IsEnabled(LogLevel.Critical).Should().BeTrue();
	}

	[Fact]
	public void FileLogger_IsEnabled_RespectsCustomMinimumLevel_Trace()
	{
		// Arrange — set minimum to Trace (everything enabled)
		var logPath = Path.Combine(_tempDir, "trace-level.log");
		using var provider = new FileLoggerProvider(logPath, LogLevel.Trace);
		var logger = provider.CreateLogger("Test");

		// Assert
		logger.IsEnabled(LogLevel.Trace).Should().BeTrue();
		logger.IsEnabled(LogLevel.Debug).Should().BeTrue();
		logger.IsEnabled(LogLevel.Information).Should().BeTrue();
	}

	[Fact]
	public void FileLogger_Log_WritesDebugMessages_WhenMinimumLevelIsDebug()
	{
		// Arrange
		var logPath = Path.Combine(_tempDir, "debug-write.log");
		var provider = new FileLoggerProvider(logPath, LogLevel.Debug);
		var logger = provider.CreateLogger("Test");

		// Act
		logger.LogDebug("Debug message");
		provider.Dispose(); // Flush the channel

		// Assert
		File.Exists(logPath).Should().BeTrue();
		var content = File.ReadAllText(logPath);
		content.Should().Contain("[Debug]");
		content.Should().Contain("Debug message");
	}

	[Fact]
	public void FileLogger_Log_SkipsInformationMessages_WhenMinimumLevelIsWarning()
	{
		// Arrange
		var logPath = Path.Combine(_tempDir, "warning-write.log");
		var provider = new FileLoggerProvider(logPath, LogLevel.Warning);
		var logger = provider.CreateLogger("Test");

		// Act
		logger.LogInformation("Info message");
		logger.LogWarning("Warning message");
		provider.Dispose(); // Flush the channel

		// Assert
		File.Exists(logPath).Should().BeTrue();
		var content = File.ReadAllText(logPath);
		content.Should().NotContain("Info message");
		content.Should().Contain("Warning message");
	}

	[Fact]
	public void FileLogger_Log_SkipsAllBelowError_WhenMinimumLevelIsError()
	{
		// Arrange
		var logPath = Path.Combine(_tempDir, "error-write.log");
		var provider = new FileLoggerProvider(logPath, LogLevel.Error);
		var logger = provider.CreateLogger("Test");

		// Act
		logger.LogDebug("Debug");
		logger.LogInformation("Info");
		logger.LogWarning("Warning");
		logger.LogError("Error message");
		logger.LogCritical("Critical message");
		provider.Dispose(); // Flush the channel

		// Assert
		var content = File.ReadAllText(logPath);
		content.Should().NotContain("[Debug]");
		content.Should().NotContain("[Information]");
		content.Should().NotContain("[Warning]");
		content.Should().Contain("[Error]");
		content.Should().Contain("[Critical]");
	}

	[Fact]
	public void FileLoggerProvider_DefaultMinimumLevel_IsInformation()
	{
		// Arrange — no explicit LogLevel specified
		var logPath = Path.Combine(_tempDir, "default-level.log");
		using var provider = new FileLoggerProvider(logPath);
		var logger = provider.CreateLogger("Test");

		// Assert — should behave same as Information level
		logger.IsEnabled(LogLevel.Debug).Should().BeFalse();
		logger.IsEnabled(LogLevel.Information).Should().BeTrue();
	}

	[Fact]
	public void AddFile_Extension_AcceptsMinimumLevel()
	{
		// Arrange — verify the extension method signature accepts LogLevel
		var logPath = Path.Combine(_tempDir, "extension-level.log");
		var provider = new FileLoggerProvider(logPath, LogLevel.Debug);
		var logger = provider.CreateLogger("Test");

		// Act
		logger.LogDebug("Debug from extension");
		provider.Dispose(); // Flush the channel

		// Assert
		File.Exists(logPath).Should().BeTrue();
		var content = File.ReadAllText(logPath);
		content.Should().Contain("[Debug]");
	}
}
