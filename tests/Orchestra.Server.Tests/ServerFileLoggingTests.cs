using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Orchestra.Server.Tests;

/// <summary>
/// The Server host writes a rolling log file under <c>{dataPath}/logs</c> by default and serves
/// its tail through <c>/api/logs</c>.
/// </summary>
public class ServerFileLoggingTests
{
	[Fact]
	public async Task Host_WritesItsLogFile_AndServesTheTail()
	{
		using var factory = new ServerDefaultLogLevelWebApplicationFactory();
		var client = factory.CreateClient();

		// A probe entry through the host's own logger factory lands in the file too - the
		// provider is attached at initialization and retrofitted onto existing loggers.
		var marker = $"probe-{Guid.NewGuid():N}";
		factory.Services.GetRequiredService<ILoggerFactory>().CreateLogger("ServerFileLoggingTests").LogInformation("{Marker}", marker);

		var logsDir = Path.Combine(factory.TestDataPath, "logs");
		var files = await WaitForAsync(() => Directory.Exists(logsDir) ? Directory.GetFiles(logsDir, "orchestra-*.log") : [], f => f.Length > 0);
		files.Should().ContainSingle("one file per day, named orchestra-yyyy-MM-dd.log");
		Path.GetFileName(files[0]).Should().MatchRegex(@"^orchestra-\d{4}-\d{2}-\d{2}\.log$");

		// The writer flushes asynchronously; poll the tail endpoint until the marker shows up.
		var body = await WaitForAsync(
			async () => await client.GetFromJsonAsync<JsonElement>("/api/logs?lines=500"),
			b => b.ValueKind == JsonValueKind.Object && b.GetProperty("lines").EnumerateArray().Any(l => l.GetString()!.Contains(marker)));

		body.GetProperty("enabled").GetBoolean().Should().BeTrue();
		body.GetProperty("directory").GetString().Should().Be(logsDir);
		var lines = body.GetProperty("lines").EnumerateArray().Select(l => l.GetString()!).ToList();
		lines.Should().Contain(l => l.Contains("Host log file:"), "the first entry announces where the file is");
		lines.Should().Contain(l => l.Contains(marker));
	}

	private static async Task<T> WaitForAsync<T>(Func<T> probe, Func<T, bool> ready)
		=> await WaitForAsync(() => Task.FromResult(probe()), ready);

	private static async Task<T> WaitForAsync<T>(Func<Task<T>> probe, Func<T, bool> ready)
	{
		var deadline = DateTime.UtcNow.AddSeconds(15);
		T last = default!;
		while (DateTime.UtcNow < deadline)
		{
			try
			{
				last = await probe();
				if (ready(last))
					return last;
			}
			catch (HttpRequestException)
			{
				// 404 until the first flush; keep polling.
			}
			await Task.Delay(100);
		}
		return last;
	}
}
