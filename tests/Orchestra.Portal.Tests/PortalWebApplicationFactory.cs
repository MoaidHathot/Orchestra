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
	private readonly string _configDirectory;
	private readonly Dictionary<string, string?> _savedEnvVars = new();

	public PortalWebApplicationFactory()
	{
		_testDataPath = Path.Combine(Path.GetTempPath(), "Orchestra.Portal.Tests", Guid.NewGuid().ToString("N"));
		_configDirectory = Path.Combine(_testDataPath, "config-root");
		Directory.CreateDirectory(_testDataPath);
		Directory.CreateDirectory(_configDirectory);

		SaveAndSetEnvironmentVariable("ORCHESTRA_CONFIG_PATH", Path.Combine(_configDirectory, "orchestra.json"));
		SaveAndSetEnvironmentVariable("ASPNETCORE_URLS", null);
		SaveAndSetEnvironmentVariable("DOTNET_URLS", null);

		File.WriteAllText(Path.Combine(_configDirectory, "orchestra.json"),
			"""
			{
			  "urls": "http://127.0.0.1:5999"
			}
			""");
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
				["Urls"] = "http://127.0.0.1:5117",
			});
		});
	}

	public string TestDataPath => _testDataPath;

	private void SaveAndSetEnvironmentVariable(string name, string? value)
	{
		_savedEnvVars[name] = Environment.GetEnvironmentVariable(name);
		Environment.SetEnvironmentVariable(name, value);
	}

	protected override void Dispose(bool disposing)
	{
		base.Dispose(disposing);

		foreach (var pair in _savedEnvVars)
			Environment.SetEnvironmentVariable(pair.Key, pair.Value);

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
