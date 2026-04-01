using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Orchestra.Host.Hosting;

/// <summary>
/// Loads Orchestra configuration from a JSON file on disk.
/// Resolution order:
///   1. Explicit path via ORCHESTRA_CONFIG_PATH environment variable
///   2. XDG_CONFIG_HOME/Orchestra/orchestra.json (all platforms, including Windows)
///   3. Platform-specific fallback:
///      - Windows: %APPDATA%/Orchestra/orchestra.json
///      - Linux/macOS: ~/.config/Orchestra/orchestra.json
///   4. If no file is found, returns defaults.
/// </summary>
public static class OrchestraConfigLoader
{
	/// <summary>
	/// The config file name within the Orchestra config directory.
	/// </summary>
	public const string ConfigFileName = "orchestra.json";

	/// <summary>
	/// The directory name under the config root.
	/// </summary>
	public const string ConfigDirectoryName = "Orchestra";

	private static readonly JsonSerializerOptions JsonOptions = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		ReadCommentHandling = JsonCommentHandling.Skip,
		AllowTrailingCommas = true,
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
		WriteIndented = true,
		Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
	};

	/// <summary>
	/// Resolves the configuration file path according to the resolution order.
	/// Returns null if no configuration file exists at any location.
	/// </summary>
	public static string? ResolveConfigPath()
	{
		// 1. Explicit path via environment variable
		var envPath = Environment.GetEnvironmentVariable("ORCHESTRA_CONFIG_PATH");
		if (!string.IsNullOrWhiteSpace(envPath) && File.Exists(envPath))
			return envPath;

		// 2. XDG_CONFIG_HOME (works on all platforms, including Windows)
		var xdgConfigHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
		if (!string.IsNullOrWhiteSpace(xdgConfigHome))
		{
			var xdgPath = Path.Combine(xdgConfigHome, ConfigDirectoryName, ConfigFileName);
			if (File.Exists(xdgPath))
				return xdgPath;
		}

		// 3. Platform-specific fallback
		var fallbackPath = GetPlatformConfigPath();
		if (fallbackPath is not null && File.Exists(fallbackPath))
			return fallbackPath;

		return null;
	}

	/// <summary>
	/// Gets the default config file path for the current platform.
	/// This is where the config file would be created if one doesn't exist.
	/// </summary>
	public static string GetDefaultConfigPath()
	{
		// Prefer XDG_CONFIG_HOME if set
		var xdgConfigHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
		if (!string.IsNullOrWhiteSpace(xdgConfigHome))
			return Path.Combine(xdgConfigHome, ConfigDirectoryName, ConfigFileName);

		return GetPlatformConfigPath()
			?? Path.Combine(
				Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
				ConfigDirectoryName,
				ConfigFileName);
	}

	/// <summary>
	/// Loads configuration from the resolved config file path and applies it to the options.
	/// Values in the config file are applied first, then the programmatic configure action
	/// runs on top (allowing overrides).
	/// </summary>
	public static void LoadAndApply(OrchestrationHostOptions options, ILogger? logger = null)
	{
		logger ??= NullLogger.Instance;

		var configPath = ResolveConfigPath();
		if (configPath is null)
		{
			logger.LogDebug("No Orchestra configuration file found. Using defaults.");
			return;
		}

		logger.LogInformation("Loading Orchestra configuration from {ConfigPath}", configPath);

		try
		{
			var json = File.ReadAllText(configPath);
			var config = JsonSerializer.Deserialize<OrchestraConfigFile>(json, JsonOptions);
			if (config is null)
			{
				logger.LogWarning("Configuration file at {ConfigPath} was empty or invalid. Using defaults.", configPath);
				return;
			}

			ApplyConfig(options, config);
			logger.LogInformation("Orchestra configuration loaded successfully from {ConfigPath}", configPath);
		}
		catch (Exception ex)
		{
			logger.LogWarning(ex, "Failed to load Orchestra configuration from {ConfigPath}. Using defaults.", configPath);
		}
	}

	/// <summary>
	/// Applies a deserialized config file to the options.
	/// Only non-null values in the config file override the defaults.
	/// </summary>
	internal static void ApplyConfig(OrchestrationHostOptions options, OrchestraConfigFile config)
	{
		if (config.DataPath is not null)
			options.DataPath = config.DataPath;

		if (config.HostBaseUrl is not null)
			options.HostBaseUrl = config.HostBaseUrl;

		if (config.OrchestrationsScanPath is not null)
			options.OrchestrationsScanPath = config.OrchestrationsScanPath;

		if (config.ShutdownTimeoutSeconds.HasValue)
			options.ShutdownTimeoutSeconds = config.ShutdownTimeoutSeconds.Value;

		if (config.LogLevel is not null)
			options.LogLevel = config.LogLevel;

		if (config.Retention is not null)
		{
			if (config.Retention.MaxRunsPerOrchestration.HasValue)
				options.Retention.MaxRunsPerOrchestration = config.Retention.MaxRunsPerOrchestration.Value;

			if (config.Retention.MaxRunAgeDays.HasValue)
				options.Retention.MaxRunAgeDays = config.Retention.MaxRunAgeDays.Value;
		}
	}

	private static string? GetPlatformConfigPath()
	{
		if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
		{
			var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
			if (!string.IsNullOrEmpty(appData))
				return Path.Combine(appData, ConfigDirectoryName, ConfigFileName);
		}
		else
		{
			// Linux and macOS: ~/.config/Orchestra/orchestra.json
			var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
			if (!string.IsNullOrEmpty(home))
				return Path.Combine(home, ".config", ConfigDirectoryName, ConfigFileName);
		}

		return null;
	}
}

/// <summary>
/// Represents the on-disk orchestra.json configuration file structure.
/// All fields are nullable — only non-null values override defaults.
/// </summary>
public class OrchestraConfigFile
{
	/// <summary>
	/// Root data path for runs, triggers, registry, etc.
	/// </summary>
	public string? DataPath { get; set; }

	/// <summary>
	/// Base URL for the Orchestra web UI.
	/// </summary>
	public string? HostBaseUrl { get; set; }

	/// <summary>
	/// Path to auto-scan for orchestration files on startup.
	/// </summary>
	public string? OrchestrationsScanPath { get; set; }

	/// <summary>
	/// Retention policy for automatic cleanup of old run records.
	/// </summary>
	public RetentionPolicyConfig? Retention { get; set; }

	/// <summary>
	/// Maximum time in seconds to wait for in-flight tasks during graceful shutdown.
	/// </summary>
	public int? ShutdownTimeoutSeconds { get; set; }

	/// <summary>
	/// Minimum log level for the file logger. Values: Trace, Debug, Information, Warning, Error, Critical.
	/// </summary>
	public string? LogLevel { get; set; }
}

/// <summary>
/// Retention policy section of the config file.
/// </summary>
public class RetentionPolicyConfig
{
	/// <summary>
	/// Maximum number of runs to keep per orchestration.
	/// 0 or null means no limit (keep forever).
	/// </summary>
	public int? MaxRunsPerOrchestration { get; set; }

	/// <summary>
	/// Maximum age of runs in days.
	/// 0 or null means no age limit (keep forever).
	/// </summary>
	public int? MaxRunAgeDays { get; set; }
}
