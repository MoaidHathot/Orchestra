using FluentAssertions;
using Microsoft.Extensions.Logging;
using Orchestra.Host.Logging;
using Xunit;

namespace Orchestra.Host.Tests;

/// <summary>
/// Unit tests for FileLogging (FileLoggerProvider and FileLogger).
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
		// Arrange
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
		using var provider = new FileLoggerProvider(logPath);
		var logger = provider.CreateLogger("TestCategory");

		// Act
		logger.LogInformation("Hello, World!");

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
		using var provider = new FileLoggerProvider(logPath);
		var logger = provider.CreateLogger("Test");

		// Act
		logger.LogInformation("Test message");

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
		using var provider = new FileLoggerProvider(logPath);
		var logger = provider.CreateLogger("Test");

		// Act
		logger.LogInformation("First message");
		logger.LogInformation("Second message");
		logger.LogWarning("Third message");

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
		using var provider = new FileLoggerProvider(logPath);
		var logger = provider.CreateLogger("Test");
		var exception = new InvalidOperationException("Test exception message");

		// Act
		logger.LogError(exception, "An error occurred");

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
		using var provider = new FileLoggerProvider(logPath);
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
		using var provider = new FileLoggerProvider(logPath);
		var logger = provider.CreateLogger("Test");

		// Act
		logger.LogInformation("Info message");
		logger.LogWarning("Warning message");
		logger.LogError("Error message");
		logger.LogCritical("Critical message");

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
}
