using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Orchestra.ProcessHost;

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
		Converters =
		{
			new JsonStringEnumConverter(JsonNamingPolicy.CamelCase),
			new ServiceEntryJsonConverter(),
		}
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
	/// The global MCP configuration file name, co-located with orchestra.json.
	/// </summary>
	public const string McpConfigFileName = "orchestra.mcp.json";

	/// <summary>
	/// The global service configuration file name, co-located with orchestra.json.
	/// </summary>
	public const string ServiceConfigFileName = "orchestra.services.json";

	/// <summary>
	/// Resolves the path to the global orchestra.mcp.json file.
	/// It lives in the same directory as orchestra.json.
	/// Returns null if no orchestra.mcp.json exists.
	/// </summary>
	public static string? ResolveGlobalMcpPath()
	{
		return ResolveColocatedConfigPath(McpConfigFileName);
	}

	/// <summary>
	/// Resolves the path to the global orchestra.services.json file.
	/// It lives in the same directory as orchestra.json.
	/// Returns null if no orchestra.services.json exists.
	/// </summary>
	public static string? ResolveServiceConfigPath()
	{
		return ResolveColocatedConfigPath(ServiceConfigFileName);
	}

	/// <summary>
	/// Resolves the path to a config file co-located with orchestra.json.
	/// Returns null if the file does not exist.
	/// </summary>
	private static string? ResolveColocatedConfigPath(string fileName)
	{
		// First try to find the config directory from the resolved config path
		var configPath = ResolveConfigPath();
		if (configPath is not null)
		{
			var dir = Path.GetDirectoryName(configPath)!;
			var filePath = Path.Combine(dir, fileName);
			if (File.Exists(filePath))
				return filePath;
		}

		// Fall back to the default config directory
		var defaultConfigPath = GetDefaultConfigPath();
		var defaultDir = Path.GetDirectoryName(defaultConfigPath)!;
		var defaultFilePath = Path.Combine(defaultDir, fileName);
		return File.Exists(defaultFilePath) ? defaultFilePath : null;
	}

	/// <summary>
	/// Loads and deserializes the orchestra.services.json file into an array of <see cref="ServiceEntry"/>.
	/// Returns null if the file cannot be parsed.
	/// </summary>
	public static ServiceEntry[]? LoadServiceConfig(string path, ILogger? logger = null)
	{
		logger ??= NullLogger.Instance;

		try
		{
			var json = File.ReadAllText(path);
			var config = JsonSerializer.Deserialize<ServiceConfigFile>(json, JsonOptions);
			return config?.Services;
		}
		catch (Exception ex)
		{
			logger.LogWarning(ex, "Failed to load service configuration from {ConfigPath}.", path);
			return null;
		}
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
	/// Loads the configuration file and returns the deserialized config, or null if no file exists
	/// or it cannot be parsed. Useful for consumers that need to read config values (such as LogLevel)
	/// before calling <see cref="LoadAndApply"/>.
	/// </summary>
	public static OrchestraConfigFile? Load(ILogger? logger = null)
	{
		logger ??= NullLogger.Instance;

		var configPath = ResolveConfigPath();
		if (configPath is null)
		{
			logger.LogDebug("No Orchestra configuration file found.");
			return null;
		}

		try
		{
			var json = File.ReadAllText(configPath);
			return JsonSerializer.Deserialize<OrchestraConfigFile>(json, JsonOptions);
		}
		catch (Exception ex)
		{
			logger.LogWarning(ex, "Failed to load Orchestra configuration from {ConfigPath}.", configPath);
			return null;
		}
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

			var configDirectory = Path.GetDirectoryName(Path.GetFullPath(configPath));
			ApplyConfig(options, config, configDirectory);
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
	/// Relative paths for <c>dataPath</c> and <c>orchestrationsScan.directory</c>
	/// are resolved against the config file's directory.
	/// </summary>
	internal static void ApplyConfig(OrchestrationHostOptions options, OrchestraConfigFile config, string? configDirectory = null)
	{
		if (config.DataPath is not null)
			options.DataPath = ResolvePath(config.DataPath, configDirectory);

		if (config.HostBaseUrl is not null)
			options.HostBaseUrl = config.HostBaseUrl;

		if (config.Scan is not null && config.Scan.Directory is not null)
		{
			var resolvedDirectory = ResolvePath(config.Scan.Directory, configDirectory);
			options.Scan ??= new ScanConfig { Directory = resolvedDirectory };

			options.Scan.Directory = resolvedDirectory;

			if (config.Scan.Watch.HasValue)
				options.Scan.Watch = config.Scan.Watch.Value;

			if (config.Scan.Recursive.HasValue)
				options.Scan.Recursive = config.Scan.Recursive.Value;
		}

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

		if (config.Polling is not null)
		{
			if (config.Polling.ActiveExecutionsMs.HasValue)
				options.Polling.ActiveExecutionsMs = config.Polling.ActiveExecutionsMs.Value;

			if (config.Polling.OrchestrationsMs.HasValue)
				options.Polling.OrchestrationsMs = config.Polling.OrchestrationsMs.Value;

			if (config.Polling.HistoryMs.HasValue)
				options.Polling.HistoryMs = config.Polling.HistoryMs.Value;

			if (config.Polling.ServerStatusMs.HasValue)
				options.Polling.ServerStatusMs = config.Polling.ServerStatusMs.Value;
		}

		if (config.DefaultModel is not null)
			options.DefaultModel = config.DefaultModel;
	}

	/// <summary>
	/// Resolves a path from the config file. If the path is relative and a config directory
	/// is known, it is resolved against the config file's directory. Otherwise, it is returned as-is
	/// (which means it will resolve against the process working directory at point of use).
	/// </summary>
	private static string ResolvePath(string path, string? configDirectory)
	{
		if (configDirectory is not null && !Path.IsPathRooted(path))
			return Path.GetFullPath(Path.Combine(configDirectory, path));

		return path;
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
	/// URL binding configuration for the ASP.NET host.
	/// Example: "http://127.0.0.1:5200" or multiple URLs separated by semicolons.
	/// </summary>
	public string? Urls { get; set; }

	/// <summary>
	/// Root data path for runs, triggers, registry, etc.
	/// </summary>
	public string? DataPath { get; set; }

	/// <summary>
	/// Base URL for the Orchestra web UI.
	/// </summary>
	public string? HostBaseUrl { get; set; }

	/// <summary>
	/// Configuration for automatic directory scanning and watching.
	/// </summary>
	public ScanConfigFile? Scan { get; set; }

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

	/// <summary>
	/// Polling intervals for the web UI, in milliseconds.
	/// </summary>
	public PollingConfig? Polling { get; set; }

	/// <summary>
	/// Default AI model to use for internal LLM calls (e.g., trigger input handlers).
	/// </summary>
	public string? DefaultModel { get; set; }

	/// <summary>
	/// MCP server endpoint configuration.
	/// </summary>
	public McpServerConfig? McpServer { get; set; }
}

/// <summary>
/// Polling interval configuration section of the config file.
/// All values are in milliseconds. Null means use the default.
/// </summary>
public class PollingConfig
{
	/// <summary>
	/// How often to poll for active execution updates. Default: 1000ms.
	/// </summary>
	public int? ActiveExecutionsMs { get; set; }

	/// <summary>
	/// How often to poll the orchestrations list. Default: 5000ms.
	/// </summary>
	public int? OrchestrationsMs { get; set; }

	/// <summary>
	/// How often to poll execution history. Default: 5000ms.
	/// </summary>
	public int? HistoryMs { get; set; }

	/// <summary>
	/// How often to poll server status. Default: 5000ms.
	/// </summary>
	public int? ServerStatusMs { get; set; }
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

/// <summary>
/// MCP server configuration section of the config file.
/// </summary>
public class McpServerConfig
{
	/// <summary>
	/// Whether the data-plane MCP server is enabled.
	/// Default: true.
	/// </summary>
	public bool? DataPlaneEnabled { get; set; }

	/// <summary>
	/// Route path for the data-plane MCP endpoint.
	/// Default: "/mcp/data".
	/// </summary>
	public string? DataPlaneRoute { get; set; }

	/// <summary>
	/// Whether the control-plane MCP server is enabled.
	/// Default: false.
	/// </summary>
	public bool? ControlPlaneEnabled { get; set; }

	/// <summary>
	/// Route path for the control-plane MCP endpoint.
	/// Default: "/mcp/control".
	/// </summary>
	public string? ControlPlaneRoute { get; set; }

	/// <summary>
	/// Maximum nesting depth for orchestration-to-orchestration invocations.
	/// 0 = top-level only (no nesting). Default: 5.
	/// </summary>
	public int? MaxNestingDepth { get; set; }
}

/// <summary>
/// Scan configuration section of the config file.
/// All fields are nullable — only non-null values override defaults.
/// </summary>
public class ScanConfigFile
{
	/// <summary>
	/// Root directory path to scan. Expected to contain <c>orchestrations/</c> and/or <c>profiles/</c> subdirectories.
	/// </summary>
	public string? Directory { get; set; }

	/// <summary>
	/// If true, watch the directory for file changes at runtime and
	/// automatically register, update, or remove orchestrations and profiles.
	/// </summary>
	public bool? Watch { get; set; }

	/// <summary>
	/// If true, scan subdirectories recursively within <c>orchestrations/</c> and <c>profiles/</c>.
	/// </summary>
	public bool? Recursive { get; set; }
}

/// <summary>
/// Represents the on-disk orchestra.services.json configuration file structure.
/// Uses a polymorphic <c>type</c> discriminator to deserialize into
/// <see cref="ProcessService"/> or <see cref="CommandHook"/> subtypes.
/// </summary>
public class ServiceConfigFile
{
	/// <summary>
	/// The list of service entries to manage.
	/// </summary>
	public ServiceEntry[]? Services { get; set; }
}

/// <summary>
/// Custom JSON converter that deserializes <see cref="ServiceEntry"/> objects based on
/// the <c>type</c> discriminator property: <c>"process"</c> maps to <see cref="ProcessService"/>
/// and <c>"command"</c> maps to <see cref="CommandHook"/>.
/// </summary>
public class ServiceEntryJsonConverter : JsonConverter<ServiceEntry>
{
	public override ServiceEntry? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
	{
		using var doc = JsonDocument.ParseValue(ref reader);
		var root = doc.RootElement;

		if (!root.TryGetProperty("type", out var typeProp))
			throw new JsonException("Service entry missing required 'type' property.");

		var type = typeProp.GetString();
		var json = root.GetRawText();

		// Create options without this converter to avoid infinite recursion
		var innerOptions = new JsonSerializerOptions(options);
		// Remove all ServiceEntryJsonConverter instances
		for (int i = innerOptions.Converters.Count - 1; i >= 0; i--)
		{
			if (innerOptions.Converters[i] is ServiceEntryJsonConverter)
				innerOptions.Converters.RemoveAt(i);
		}

		return type switch
		{
			"process" => JsonSerializer.Deserialize<ProcessService>(json, innerOptions),
			"command" => JsonSerializer.Deserialize<CommandHook>(json, innerOptions),
			_ => throw new JsonException($"Unknown service entry type '{type}'. Expected 'process' or 'command'."),
		};
	}

	public override void Write(Utf8JsonWriter writer, ServiceEntry value, JsonSerializerOptions options)
	{
		JsonSerializer.Serialize(writer, value, value.GetType(), options);
	}
}
