using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Orchestra.Portal.Tests;

/// <summary>
/// Custom WebApplicationFactory for Portal integration tests.
/// Creates an isolated test environment with its own data directory and a per-instance
/// <c>orchestra.json</c>. The unique data path is injected via IConfiguration; the
/// <c>orchestra.json</c> location is pointed at via <c>ORCHESTRA_CONFIG_PATH</c>, which is
/// process-global, so host construction is serialized across instances to keep parallel
/// Portal test classes from reading each other's config.
/// </summary>
public class PortalWebApplicationFactory : WebApplicationFactory<Program>
{
	// OrchestraConfigLoader resolves orchestra.json from the process-global ORCHESTRA_CONFIG_PATH
	// (and honors XDG_CONFIG_HOME). Hold this lock across the whole host build so concurrent
	// factory instances never observe each other's env values mid-construction.
	private static readonly object ConfigEnvLock = new();

	private readonly string _testDataPath;
	private readonly string _configPath;

	public PortalWebApplicationFactory() : this(logLevel: null)
	{
	}

	protected PortalWebApplicationFactory(string? logLevel)
	{
		_testDataPath = Path.Combine(Path.GetTempPath(), "Orchestra.Portal.Tests", Guid.NewGuid().ToString("N"));
		var configDirectory = Path.Combine(_testDataPath, "config-root");
		Directory.CreateDirectory(_testDataPath);
		Directory.CreateDirectory(configDirectory);

		_configPath = Path.Combine(configDirectory, "orchestra.json");
		File.WriteAllText(_configPath, BuildConfigJson(logLevel));
	}

	private static string BuildConfigJson(string? logLevel)
	{
		var config = new Dictionary<string, string>
		{
			["urls"] = "http://127.0.0.1:5999",
		};

		if (logLevel is not null)
			config["logLevel"] = logLevel;

		return JsonSerializer.Serialize(config);
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

	protected override IHost CreateHost(IHostBuilder builder)
	{
		// Point OrchestraConfigLoader at this instance's orchestra.json for the duration of the
		// host build. Held under a process-wide lock because these env vars are global.
		lock (ConfigEnvLock)
		{
			var savedConfigPath = Environment.GetEnvironmentVariable("ORCHESTRA_CONFIG_PATH");
			var savedAspNetUrls = Environment.GetEnvironmentVariable("ASPNETCORE_URLS");
			var savedDotnetUrls = Environment.GetEnvironmentVariable("DOTNET_URLS");

			Environment.SetEnvironmentVariable("ORCHESTRA_CONFIG_PATH", _configPath);
			Environment.SetEnvironmentVariable("ASPNETCORE_URLS", null);
			Environment.SetEnvironmentVariable("DOTNET_URLS", null);
			try
			{
				return base.CreateHost(builder);
			}
			finally
			{
				Environment.SetEnvironmentVariable("ORCHESTRA_CONFIG_PATH", savedConfigPath);
				Environment.SetEnvironmentVariable("ASPNETCORE_URLS", savedAspNetUrls);
				Environment.SetEnvironmentVariable("DOTNET_URLS", savedDotnetUrls);
			}
		}
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

/// <summary>
/// Portal factory variant that plants an <c>orchestra.json</c> with <c>"logLevel": "Warning"</c>,
/// used to assert the host honors the configured minimum log level.
/// </summary>
public sealed class PortalWarningLogLevelWebApplicationFactory : PortalWebApplicationFactory
{
	public PortalWarningLogLevelWebApplicationFactory() : base(logLevel: "Warning")
	{
	}
}
