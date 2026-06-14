using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Orchestra.Server.Tests;

/// <summary>
/// Server factory that plants a per-instance <c>orchestra.json</c> (optionally with a
/// <c>logLevel</c>) and points <c>ORCHESTRA_CONFIG_PATH</c> at it for the duration of host
/// construction. Used to assert the Server honors orchestra.json's <c>logLevel</c>. Kept separate
/// from <see cref="ServerWebApplicationFactory"/> so the broader server integration suite — which
/// does not plant config — is unaffected.
/// </summary>
public abstract class ConfiguredServerWebApplicationFactory : WebApplicationFactory<Program>
{
	// ORCHESTRA_CONFIG_PATH is process-global; serialize host construction so it is only ever
	// set for the duration of one factory's build.
	private static readonly object ConfigEnvLock = new();

	private readonly string _testDataPath;
	private readonly string _configPath;

	protected ConfiguredServerWebApplicationFactory(string? logLevel)
	{
		_testDataPath = Path.Combine(Path.GetTempPath(), "Orchestra.Server.Tests", Guid.NewGuid().ToString("N"));
		var configDirectory = Path.Combine(_testDataPath, "config-root");
		Directory.CreateDirectory(_testDataPath);
		Directory.CreateDirectory(configDirectory);

		_configPath = Path.Combine(configDirectory, "orchestra.json");
		File.WriteAllText(_configPath, BuildConfigJson(logLevel));
	}

	private static string BuildConfigJson(string? logLevel)
		=> logLevel is null
			? "{}"
			: JsonSerializer.Serialize(new Dictionary<string, string> { ["logLevel"] = logLevel });

	protected override void ConfigureWebHost(IWebHostBuilder builder)
	{
		builder.UseEnvironment("Testing");

		builder.ConfigureAppConfiguration((_, config) =>
		{
			config.AddInMemoryCollection(new Dictionary<string, string?>
			{
				["data-path"] = _testDataPath,
			});
		});
	}

	protected override IHost CreateHost(IHostBuilder builder)
	{
		lock (ConfigEnvLock)
		{
			var savedConfigPath = Environment.GetEnvironmentVariable("ORCHESTRA_CONFIG_PATH");
			Environment.SetEnvironmentVariable("ORCHESTRA_CONFIG_PATH", _configPath);
			try
			{
				return base.CreateHost(builder);
			}
			finally
			{
				Environment.SetEnvironmentVariable("ORCHESTRA_CONFIG_PATH", savedConfigPath);
			}
		}
	}

	public string TestDataPath => _testDataPath;

	protected override void Dispose(bool disposing)
	{
		base.Dispose(disposing);

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

/// <summary>Server factory with <c>orchestra.json</c> = <c>{ "logLevel": "Warning" }</c>.</summary>
public sealed class ServerWarningLogLevelWebApplicationFactory : ConfiguredServerWebApplicationFactory
{
	public ServerWarningLogLevelWebApplicationFactory() : base(logLevel: "Warning")
	{
	}
}

/// <summary>Server factory with an empty <c>orchestra.json</c> (no logLevel override).</summary>
public sealed class ServerDefaultLogLevelWebApplicationFactory : ConfiguredServerWebApplicationFactory
{
	public ServerDefaultLogLevelWebApplicationFactory() : base(logLevel: null)
	{
	}
}
