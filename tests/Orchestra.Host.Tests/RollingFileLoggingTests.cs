using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orchestra.Host.Api;
using Orchestra.Host.Hosting;
using Orchestra.Host.Logging;
using Xunit;

namespace Orchestra.Host.Tests;

/// <summary>
/// The host's rolling log file: daily files with retention, the tail reader behind
/// <c>orchestra logs</c>, and the <c>/api/logs</c> endpoint.
/// </summary>
public class RollingFileLoggingTests : IDisposable
{
	private readonly string _tempDir;

	public RollingFileLoggingTests()
	{
		_tempDir = Path.Combine(Path.GetTempPath(), $"orchestra-rolling-log-tests-{Guid.NewGuid():N}");
		Directory.CreateDirectory(_tempDir);
	}

	public void Dispose()
	{
		if (Directory.Exists(_tempDir))
		{
			try { Directory.Delete(_tempDir, recursive: true); }
			catch { /* best-effort cleanup */ }
		}
		GC.SuppressFinalize(this);
	}

	private string LogsDir => Path.Combine(_tempDir, "logs");

	#region Options

	[Fact]
	public void Options_Defaults_AreDataPathLogs_14Days_Information()
	{
		var options = new FileLoggingOptions();

		options.Enabled.Should().BeTrue();
		options.ResolveDirectory(_tempDir).Should().Be(Path.Combine(_tempDir, "logs"));
		options.MinimumLevel.Should().Be(LogLevel.Information);
		options.RetainedFiles.Should().Be(14);
		options.FileNameFor(new DateOnly(2026, 9, 27)).Should().Be("orchestra-2026-09-27.log");
		options.FilePattern.Should().Be("orchestra-*.log");
	}

	[Fact]
	public void Options_ExplicitDirectory_Wins()
	{
		var options = new FileLoggingOptions { Directory = @"D:\elsewhere\logs" };

		options.ResolveDirectory(_tempDir).Should().Be(@"D:\elsewhere\logs");
	}

	#endregion

	#region Provider (rolling mode)

	[Fact]
	public void RollingProvider_WritesToTodaysFile_UnderDataPathLogs()
	{
		var options = new FileLoggingOptions();
		string expectedPath;
		using (var provider = new FileLoggerProvider(options, _tempDir))
		{
			expectedPath = provider.PathFor(DateTime.Now);
			provider.CreateLogger("Test").LogInformation("hello from the rolling provider");
		}

		expectedPath.Should().StartWith(LogsDir);
		File.Exists(expectedPath).Should().BeTrue();
		File.ReadAllText(expectedPath).Should().Contain("[Information] Test: hello from the rolling provider");
	}

	[Fact]
	public void RollingProvider_RespectsMinimumLevel()
	{
		var options = new FileLoggingOptions { MinimumLevel = LogLevel.Warning };
		string path;
		using (var provider = new FileLoggerProvider(options, _tempDir))
		{
			path = provider.PathFor(DateTime.Now);
			var logger = provider.CreateLogger("Test");
			logger.LogInformation("dropped");
			logger.LogWarning("kept");
		}

		var text = File.ReadAllText(path);
		text.Should().Contain("kept").And.NotContain("dropped");
	}

	[Fact]
	public void RollingProvider_PrunesFilesBeyondRetention_WhenItOpensAFile()
	{
		Directory.CreateDirectory(LogsDir);
		foreach (var day in new[] { "2020-01-01", "2020-01-02", "2020-01-03" })
			File.WriteAllText(Path.Combine(LogsDir, $"orchestra-{day}.log"), "old");
		File.WriteAllText(Path.Combine(LogsDir, "unrelated.log"), "keep");

		var options = new FileLoggingOptions { RetainedFiles = 2 };
		using (var provider = new FileLoggerProvider(options, _tempDir))
		{
			provider.CreateLogger("Test").LogInformation("today");
		}

		var remaining = Directory.GetFiles(LogsDir).Select(f => Path.GetFileName(f)).OrderBy(f => f, StringComparer.Ordinal).ToArray();
		// Today's file + the newest old one survive (2 retained); the two oldest go; other files untouched.
		remaining.Should().NotContain("orchestra-2020-01-01.log");
		remaining.Should().NotContain("orchestra-2020-01-02.log");
		remaining.Should().Contain("orchestra-2020-01-03.log");
		remaining.Should().Contain("unrelated.log");
		remaining.Count(f => f.StartsWith("orchestra-", StringComparison.Ordinal)).Should().Be(2);
	}

	[Fact]
	public void FixedPathProvider_StillWorks_AndDoesNotPrune()
	{
		Directory.CreateDirectory(LogsDir);
		File.WriteAllText(Path.Combine(LogsDir, "orchestra-2020-01-01.log"), "old");
		var path = Path.Combine(LogsDir, "app.log");

		using (var provider = new FileLoggerProvider(path))
		{
			provider.CreateLogger("Test").LogInformation("fixed");
		}

		File.ReadAllText(path).Should().Contain("fixed");
		File.Exists(Path.Combine(LogsDir, "orchestra-2020-01-01.log")).Should().BeTrue("fixed-file mode never touches other files");
	}

	#endregion

	#region LogFileReader

	private void WriteLog(string day, params string[] lines)
	{
		Directory.CreateDirectory(LogsDir);
		File.WriteAllText(Path.Combine(LogsDir, $"orchestra-{day}.log"), string.Join('\n', lines) + "\n");
	}

	[Fact]
	public async Task Reader_NoFiles_ReturnsNull()
	{
		var tail = await LogFileReader.ReadAsync(LogsDir, "orchestra-*.log", 10, null, null, CancellationToken.None);

		tail.Should().BeNull();
	}

	[Fact]
	public async Task Reader_Tail_ReturnsLastNLinesOfTheNewestFile()
	{
		WriteLog("2026-09-26", "yesterday-1", "yesterday-2");
		WriteLog("2026-09-27", "l1", "l2", "l3", "l4", "l5");

		var tail = await LogFileReader.ReadAsync(LogsDir, "orchestra-*.log", 3, null, null, CancellationToken.None);

		tail.Should().NotBeNull();
		tail!.FileName.Should().Be("orchestra-2026-09-27.log");
		tail.Lines.Should().Equal("l3", "l4", "l5");
		tail.Truncated.Should().BeTrue();
		tail.Offset.Should().Be(new FileInfo(tail.Path).Length);
	}

	[Fact]
	public async Task Reader_Tail_LargerThanFile_ReturnsEverything_NotTruncated()
	{
		WriteLog("2026-09-27", "a", "b");

		var tail = await LogFileReader.ReadAsync(LogsDir, "orchestra-*.log", 100, null, null, CancellationToken.None);

		tail!.Lines.Should().Equal("a", "b");
		tail.Truncated.Should().BeFalse();
	}

	[Fact]
	public async Task Reader_Tail_HandlesCrLf_AndMultiChunkFiles()
	{
		Directory.CreateDirectory(LogsDir);
		var lines = Enumerable.Range(1, 5000).Select(i => $"line-{i:00000}-{new string('x', 40)}");
		File.WriteAllText(Path.Combine(LogsDir, "orchestra-2026-09-27.log"), string.Join("\r\n", lines) + "\r\n");

		var tail = await LogFileReader.ReadAsync(LogsDir, "orchestra-*.log", 2, null, null, CancellationToken.None);

		tail!.Lines.Should().Equal("line-04999-" + new string('x', 40), "line-05000-" + new string('x', 40));
	}

	[Fact]
	public async Task Reader_Follow_ReturnsOnlyWhatWasAppended()
	{
		WriteLog("2026-09-27", "first");
		var initial = await LogFileReader.ReadAsync(LogsDir, "orchestra-*.log", 10, null, null, CancellationToken.None);

		File.AppendAllText(Path.Combine(LogsDir, "orchestra-2026-09-27.log"), "second\nthird\n");
		var next = await LogFileReader.ReadAsync(LogsDir, "orchestra-*.log", 0, initial!.Offset, initial.FileName, CancellationToken.None);

		next!.Lines.Should().Equal("second", "third");
		next.Offset.Should().BeGreaterThan(initial.Offset);

		var nothingNew = await LogFileReader.ReadAsync(LogsDir, "orchestra-*.log", 0, next.Offset, next.FileName, CancellationToken.None);
		nothingNew!.Lines.Should().BeEmpty();
	}

	[Fact]
	public async Task Reader_Follow_RestartsOnTheNewFile_WhenTheDayRolls()
	{
		WriteLog("2026-09-27", "old-day");
		var initial = await LogFileReader.ReadAsync(LogsDir, "orchestra-*.log", 10, null, null, CancellationToken.None);

		WriteLog("2026-09-28", "new-day-1", "new-day-2");
		var next = await LogFileReader.ReadAsync(LogsDir, "orchestra-*.log", 0, initial!.Offset, initial.FileName, CancellationToken.None);

		next!.FileName.Should().Be("orchestra-2026-09-28.log");
		next.Lines.Should().Equal(["new-day-1", "new-day-2"], "a roll restarts from the top of the new file");
	}

	[Fact]
	public async Task Reader_Follow_OffsetBeyondFile_StartsOver()
	{
		WriteLog("2026-09-27", "short");

		var tail = await LogFileReader.ReadAsync(LogsDir, "orchestra-*.log", 0, 10_000, "orchestra-2026-09-27.log", CancellationToken.None);

		tail!.Lines.Should().Equal("short");
	}

	[Fact]
	public async Task Reader_ClampsLinesToMax()
	{
		WriteLog("2026-09-27", Enumerable.Range(1, 6000).Select(i => i.ToString()).ToArray());

		var tail = await LogFileReader.ReadAsync(LogsDir, "orchestra-*.log", 100_000, null, null, CancellationToken.None);

		tail!.Lines.Should().HaveCount(LogsApi.MaxLines);
		tail.Lines[^1].Should().Be("6000");
	}

	#endregion

	#region /api/logs

	private IHost CreateHost(bool fileLoggingEnabled = true)
	{
		var options = new OrchestrationHostOptions { DataPath = _tempDir };
		options.FileLogging.Enabled = fileLoggingEnabled;

		var host = new HostBuilder()
			.ConfigureWebHost(webHost =>
			{
				webHost.UseTestServer();
				webHost.ConfigureServices(services =>
				{
					services.AddRouting();
					services.AddSingleton(options);
				});
				webHost.Configure(app =>
				{
					app.UseRouting();
					app.UseEndpoints(endpoints => endpoints.MapLogsApi());
				});
			})
			.Build();

		host.Start();
		return host;
	}

	[Fact]
	public async Task Api_NoLogFileYet_Is404WithAHelpfulDetail()
	{
		using var host = CreateHost();
		var client = host.GetTestClient();

		var response = await client.GetAsync("/api/logs");

		response.StatusCode.Should().Be(HttpStatusCode.NotFound);
		var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
		problem.GetProperty("detail").GetString().Should().Contain("No log file has been written yet");
	}

	[Fact]
	public async Task Api_Tail_ReturnsLinesAndOffsetForFollowing()
	{
		WriteLog("2026-09-27", "one", "two", "three");
		using var host = CreateHost();
		var client = host.GetTestClient();

		var body = await client.GetFromJsonAsync<JsonElement>("/api/logs?lines=2");

		body.GetProperty("file").GetString().Should().Be("orchestra-2026-09-27.log");
		body.GetProperty("directory").GetString().Should().Be(LogsDir);
		body.GetProperty("enabled").GetBoolean().Should().BeTrue();
		body.GetProperty("lines").EnumerateArray().Select(l => l.GetString()).Should().Equal("two", "three");
		body.GetProperty("offset").GetInt64().Should().BeGreaterThan(0);

		File.AppendAllText(Path.Combine(LogsDir, "orchestra-2026-09-27.log"), "four\n");
		var follow = await client.GetFromJsonAsync<JsonElement>($"/api/logs?offset={body.GetProperty("offset").GetInt64()}&file=orchestra-2026-09-27.log");
		follow.GetProperty("lines").EnumerateArray().Select(l => l.GetString()).Should().Equal("four");
	}

	[Fact]
	public async Task Api_FileLoggingDisabled_StillServesExistingFiles_AndSaysSo()
	{
		WriteLog("2026-09-27", "from a previous host");
		using var host = CreateHost(fileLoggingEnabled: false);
		var client = host.GetTestClient();

		var body = await client.GetFromJsonAsync<JsonElement>("/api/logs");

		body.GetProperty("enabled").GetBoolean().Should().BeFalse();
		body.GetProperty("lines").EnumerateArray().Select(l => l.GetString()).Should().Equal("from a previous host");
	}

	[Theory]
	[InlineData("lines=-1")]
	[InlineData("offset=-5")]
	public async Task Api_RejectsNegativeParameters(string query)
	{
		using var host = CreateHost();
		var client = host.GetTestClient();

		var response = await client.GetAsync($"/api/logs?{query}");

		response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
	}

	#endregion
}
