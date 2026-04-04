using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Orchestra.Portal.Tests;

/// <summary>
/// Custom WebApplicationFactory for Portal integration tests.
/// Creates an isolated test environment with its own data directory.
/// Each instance injects its unique data path via IConfiguration, avoiding
/// process-global environment variables that cause race conditions in parallel test runs.
/// </summary>
public class PortalWebApplicationFactory : WebApplicationFactory<Program>
{
	private readonly string _testDataPath;

	public PortalWebApplicationFactory()
	{
		_testDataPath = Path.Combine(Path.GetTempPath(), "Orchestra.Portal.Tests", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_testDataPath);
	}

	protected override void ConfigureWebHost(IWebHostBuilder builder)
	{
		builder.UseEnvironment("Testing");

		// Inject the unique test data path via configuration instead of
		// a process-global environment variable. Program.cs reads this via
		// builder.Configuration["data-path"].
		builder.ConfigureAppConfiguration((_, config) =>
		{
			config.AddInMemoryCollection(new Dictionary<string, string?>
			{
				["data-path"] = _testDataPath,
			});
		});
	}

	public string TestDataPath => _testDataPath;

	protected override void Dispose(bool disposing)
	{
		base.Dispose(disposing);

		// Clean up test data directory
		if (Directory.Exists(_testDataPath))
		{
			try
			{
				Directory.Delete(_testDataPath, recursive: true);
			}
			catch
			{
				// Ignore cleanup errors in tests
			}
		}
	}
}
