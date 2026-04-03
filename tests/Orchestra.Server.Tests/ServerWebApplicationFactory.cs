using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Orchestra.Server.Tests;

/// <summary>
/// Custom WebApplicationFactory for Orchestra.Server integration tests.
/// Creates an isolated test environment with its own data directory
/// so tests don't interfere with each other or with real data.
/// </summary>
public class ServerWebApplicationFactory : WebApplicationFactory<Program>
{
	private readonly string _testDataPath;

	public ServerWebApplicationFactory()
	{
		_testDataPath = Path.Combine(Path.GetTempPath(), "Orchestra.Server.Tests", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_testDataPath);

		// Set environment variable for test isolation before application starts
		Environment.SetEnvironmentVariable("ORCHESTRA_DATA_PATH", _testDataPath);
	}

	protected override void ConfigureWebHost(IWebHostBuilder builder)
	{
		builder.UseEnvironment("Testing");
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

		Environment.SetEnvironmentVariable("ORCHESTRA_DATA_PATH", null);
	}
}
