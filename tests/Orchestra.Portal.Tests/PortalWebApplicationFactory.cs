using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Orchestra.Portal.Tests;

/// <summary>
/// Custom WebApplicationFactory for Portal integration tests.
/// Creates an isolated test environment with its own data directory.
/// </summary>
public class PortalWebApplicationFactory : WebApplicationFactory<Program>
{
	private readonly string _testDataPath;

	public PortalWebApplicationFactory()
	{
		_testDataPath = Path.Combine(Path.GetTempPath(), "Orchestra.Portal.Tests", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_testDataPath);
		
		// Set environment variable for test isolation before application starts
		Environment.SetEnvironmentVariable("ORCHESTRA_PORTAL_DATA_PATH", _testDataPath);
	}

	protected override void ConfigureWebHost(IWebHostBuilder builder)
	{
		builder.UseEnvironment("Testing");

		// Note: The OrchestrationRegistry uses a hardcoded path to LocalApplicationData.
		// For full test isolation, we'd need to modify the Program.cs to read the path from
		// configuration. For now, tests will share the same persisted orchestrations file,
		// but each test run creates unique orchestration names to avoid conflicts.
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
		
		Environment.SetEnvironmentVariable("ORCHESTRA_PORTAL_DATA_PATH", null);
	}
}
