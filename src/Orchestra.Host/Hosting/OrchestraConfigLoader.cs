using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Orchestra.Engine;
using Orchestra.Engine.Serialization;
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
			new HookEventTypeJsonConverter(),
			new HookStepSelectionJsonConverter(),
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
	/// <remarks>
	/// <c>${VAR}</c> and <c>"env:VAR"</c> references inside the JSON are expanded
	/// against the process environment before deserialization. Missing variables
	/// surface as load-time errors rather than silently leaking literal
	/// <c>${VAR}</c> strings into the spawned process command lines.
	/// </remarks>
	public static ServiceEntry[]? LoadServiceConfig(string path, ILogger? logger = null)
	{
		logger ??= NullLogger.Instance;

		try
		{
			var json = File.ReadAllText(path);
			json = EnvironmentVariableExpander.Expand(json, path);
			var config = JsonSerializer.Deserialize<ServiceConfigFile>(json, JsonOptions);

			// Apply the file-level default readiness timeout to any process service that does
			// not set its own. This keeps the readiness timeout configurable (orchestra.services.json)
			// rather than hardcoded, while still letting a service override it.
			if (config?.DefaultReadinessTimeoutSeconds is { } defaultReadinessTimeout)
			{
				foreach (var entry in config.Services ?? [])
				{
					if (entry is ProcessService { Readiness: { TimeoutSeconds: null } readiness })
					{
						readiness.TimeoutSeconds = defaultReadinessTimeout;
					}
				}
			}

			return config?.Services;
		}
		catch (EnvironmentVariableExpansionException ex)
		{
			// A missing env var is a configuration error, not a transient I/O
			// problem — log at Error so it shows up in CI/operator triage and
			// rethrow so the caller can decide whether to fail fast.
			logger.LogError(ex, "Service configuration {ConfigPath} references unset environment variable '{VariableName}'.", path, ex.VariableName);
			throw;
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
	/// <remarks>
	/// <c>${VAR}</c> and <c>"env:VAR"</c> references inside the JSON are expanded
	/// against the process environment before deserialization. Missing variables
	/// throw <see cref="EnvironmentVariableExpansionException"/>.
	/// </remarks>
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
			json = EnvironmentVariableExpander.Expand(json, configPath);
			return JsonSerializer.Deserialize<OrchestraConfigFile>(json, JsonOptions);
		}
		catch (EnvironmentVariableExpansionException ex)
		{
			logger.LogError(ex, "Orchestra configuration {ConfigPath} references unset environment variable '{VariableName}'.", configPath, ex.VariableName);
			throw;
		}
		catch (Exception ex)
		{
			logger.LogWarning(ex, "Failed to load Orchestra configuration from {ConfigPath}.", configPath);
			return null;
		}
	}

	/// <summary>
	/// Resolves the effective <c>dataPath</c> configured in the discovered <c>orchestra.json</c>,
	/// applying the same relative-to-config-directory resolution as <see cref="ApplyConfig"/>.
	/// Returns null when no config file is found or it sets no <c>dataPath</c> (so the caller should
	/// fall back to the host default). Useful for lightweight tools (e.g. the CLI's management verbs)
	/// that need to read/write the same data path as the host <em>without</em> loading the rest of
	/// orchestra.json (scan/services/MCP), which can be expensive and has side effects.
	/// </summary>
	public static string? ResolveConfiguredDataPath(ILogger? logger = null)
	{
		var configPath = ResolveConfigPath();
		if (configPath is null)
			return null;

		var config = Load(logger);
		if (config?.DataPath is not { } dataPath)
			return null;

		var configDirectory = Path.GetDirectoryName(Path.GetFullPath(configPath));
		return ResolvePath(dataPath, configDirectory);
	}

	/// <summary>
	/// Loads configuration from the resolved config file path and applies it to the options.
	/// Values in the config file are applied first, then the programmatic configure action
	/// runs on top (allowing overrides).
	/// </summary>
	/// <remarks>
	/// <c>${VAR}</c> and <c>"env:VAR"</c> references inside the JSON are expanded
	/// against the process environment before deserialization. Missing variables
	/// throw <see cref="EnvironmentVariableExpansionException"/> so the host fails
	/// fast at startup rather than continuing with partial configuration.
	/// </remarks>
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
			json = EnvironmentVariableExpander.Expand(json, configPath);
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
		catch (EnvironmentVariableExpansionException ex)
		{
			logger.LogError(ex, "Orchestra configuration {ConfigPath} references unset environment variable '{VariableName}'.", configPath, ex.VariableName);
			throw;
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

		if (config.AutoResumeCheckpointsOnStartup.HasValue)
			options.AutoResumeCheckpointsOnStartup = config.AutoResumeCheckpointsOnStartup.Value;

		if (config.EnableScheduler.HasValue)
			options.EnableScheduler = config.EnableScheduler.Value;

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

		if (config.AgentPool is not null)
		{
			if (config.AgentPool.MinInstances.HasValue)
				options.AgentPool.MinInstances = config.AgentPool.MinInstances.Value;

			if (config.AgentPool.MaxInstances.HasValue)
				options.AgentPool.MaxInstances = config.AgentPool.MaxInstances.Value;

			if (config.AgentPool.MaxSessionsPerInstance.HasValue)
				options.AgentPool.MaxSessionsPerInstance = config.AgentPool.MaxSessionsPerInstance.Value;

			if (config.AgentPool.IdleTimeoutSeconds.HasValue)
				options.AgentPool.IdleTimeoutSeconds = config.AgentPool.IdleTimeoutSeconds.Value;
		}

		if (config.Copilot?.Swap is { } swapConfig)
		{
			if (swapConfig.BudgetPerStep.HasValue)
				options.Copilot.Swap.BudgetPerStep = swapConfig.BudgetPerStep.Value;

			if (swapConfig.ResumeOnSwap.HasValue)
				options.Copilot.Swap.ResumeOnSwap = swapConfig.ResumeOnSwap.Value;

			if (swapConfig.ResumeAlreadyInUseWaitSeconds.HasValue)
				options.Copilot.Swap.ResumeAlreadyInUseWaitSeconds = swapConfig.ResumeAlreadyInUseWaitSeconds.Value;

			if (swapConfig.ResumeAlreadyInUsePollIntervalMs.HasValue)
				options.Copilot.Swap.ResumeAlreadyInUsePollIntervalMs = swapConfig.ResumeAlreadyInUsePollIntervalMs.Value;
		}

		if (config.Copilot is { } copilotConfig)
		{
			if (!string.IsNullOrWhiteSpace(copilotConfig.GitHubToken))
				options.Copilot.GitHubToken = copilotConfig.GitHubToken;

			if (copilotConfig.UseLoggedInUser.HasValue)
				options.Copilot.UseLoggedInUser = copilotConfig.UseLoggedInUser.Value;

			if (copilotConfig.McpStartupTimeoutSeconds.HasValue)
				options.Copilot.McpStartupTimeoutSeconds = copilotConfig.McpStartupTimeoutSeconds.Value;
		}

		// Default agent provider (top-level "provider" or "defaultProvider").
		var providerDefault = config.Provider ?? config.DefaultProvider;
		if (!string.IsNullOrWhiteSpace(providerDefault))
			options.Provider = providerDefault.Trim();

		if (config.OpenCode is { } openCodeConfig)
		{
			if (!string.IsNullOrWhiteSpace(openCodeConfig.CliPath))
				options.OpenCode.CliPath = openCodeConfig.CliPath;
			if (!string.IsNullOrWhiteSpace(openCodeConfig.Hostname))
				options.OpenCode.Hostname = openCodeConfig.Hostname;
			if (!string.IsNullOrWhiteSpace(openCodeConfig.ServerPassword))
				options.OpenCode.ServerPassword = openCodeConfig.ServerPassword;
			if (!string.IsNullOrWhiteSpace(openCodeConfig.ServerUsername))
				options.OpenCode.ServerUsername = openCodeConfig.ServerUsername;
			if (!string.IsNullOrWhiteSpace(openCodeConfig.FallbackProvider))
				options.OpenCode.FallbackProvider = openCodeConfig.FallbackProvider;
			if (openCodeConfig.StartupTimeoutSeconds.HasValue)
				options.OpenCode.StartupTimeoutSeconds = openCodeConfig.StartupTimeoutSeconds.Value;
			if (openCodeConfig.EngineToolBridgeEnabled.HasValue)
				options.OpenCode.EngineToolBridgeEnabled = openCodeConfig.EngineToolBridgeEnabled.Value;
			if (openCodeConfig.SwapBudgetPerStep.HasValue)
				options.OpenCode.SwapBudgetPerStep = openCodeConfig.SwapBudgetPerStep.Value;
			if (openCodeConfig.ResumeOnSwapEnabled.HasValue)
				options.OpenCode.ResumeOnSwapEnabled = openCodeConfig.ResumeOnSwapEnabled.Value;
		}

		if (config.Hooks is { Length: > 0 })
		{
			HookDefinitionResolver.ApplyBaseDirectory(config.Hooks, configDirectory);
			options.Hooks = config.Hooks;
		}

		if (config.Sse is not null)
		{
			if (config.Sse.MaxAccumulatedEvents.HasValue)
				options.Sse.MaxAccumulatedEvents = config.Sse.MaxAccumulatedEvents.Value;

			if (config.Sse.MaxChannelCapacity.HasValue)
				options.Sse.MaxChannelCapacity = config.Sse.MaxChannelCapacity.Value;

			if (config.Sse.MaxSubscribers.HasValue)
				options.Sse.MaxSubscribers = config.Sse.MaxSubscribers.Value;

			if (config.Sse.HeartbeatIntervalSeconds.HasValue && config.Sse.HeartbeatIntervalSeconds.Value > 0)
				options.Sse.HeartbeatInterval = TimeSpan.FromSeconds(config.Sse.HeartbeatIntervalSeconds.Value);
		}
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
	/// Whether to automatically resume persisted orchestration checkpoints on startup.
	/// </summary>
	public bool? AutoResumeCheckpointsOnStartup { get; set; }

	/// <summary>
	/// Whether the background scheduling loops run (trigger scheduler + profile schedules).
	/// Set to <c>false</c> for an API-only server that never auto-fires anything.
	/// </summary>
	public bool? EnableScheduler { get; set; }

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
	/// Default agent worker pool settings for orchestration runs.
	/// </summary>
	public AgentPoolConfig? AgentPool { get; set; }

	/// <summary>
	/// Copilot-provider-specific runtime settings (swap budget, session-resume policy).
	/// See <see cref="CopilotProviderOptions"/> for the schema. Leaving this null uses
	/// the built-in defaults defined in <c>CopilotAgentPoolOptions</c>.
	/// </summary>
	public CopilotProviderConfig? Copilot { get; set; }

	/// <summary>
	/// OpenCode-provider-specific runtime settings. See <see cref="OpenCodeProviderOptions"/>
	/// for the schema. Leaving this null uses the built-in defaults.
	/// </summary>
	public OpenCodeProviderConfig? OpenCode { get; set; }

	/// <summary>
	/// Default agent provider name (e.g. <c>"copilot"</c> or <c>"opencode"</c>) applied to
	/// orchestrations/steps that do not declare their own provider.
	/// </summary>
	public string? Provider { get; set; }

	/// <summary>Alias for <see cref="Provider"/>.</summary>
	public string? DefaultProvider { get; set; }

	/// <summary>
	/// MCP server endpoint configuration.
	/// </summary>
	public McpServerConfig? McpServer { get; set; }

	/// <summary>
	/// Global hooks applied to all orchestrations executed by the host.
	/// </summary>
	public HookDefinition[]? Hooks { get; set; }

	/// <summary>
	/// SSE event-streaming pipeline configuration (replay buffer / channel / subscriber caps).
	/// </summary>
	public SseConfig? Sse { get; set; }
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
/// SSE pipeline configuration section of the config file. All fields are nullable —
/// only non-null values override defaults in <see cref="SseOptions"/>.
/// </summary>
public class SseConfig
{
	/// <summary>
	/// Maximum events kept in the per-execution circular replay buffer.
	/// </summary>
	public int? MaxAccumulatedEvents { get; set; }

	/// <summary>
	/// Maximum events buffered per attached subscriber's outbound channel.
	/// </summary>
	public int? MaxChannelCapacity { get; set; }

	/// <summary>
	/// Maximum number of concurrent SSE subscribers per execution.
	/// </summary>
	public int? MaxSubscribers { get; set; }

	/// <summary>
	/// Heartbeat interval (seconds) for keepalive frames on active SSE streams.
	/// </summary>
	public int? HeartbeatIntervalSeconds { get; set; }
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

	/// <summary>
	/// Default timeout (seconds) applied to MCP tool calls that target Orchestra's
	/// own data-plane MCP endpoint when the orchestration's <c>mcps[]</c> entry does
	/// not specify a <c>timeoutSeconds</c>. Use this to raise the cap above the
	/// Copilot SDK's ~3-minute default for long-running <c>invoke_orchestration</c>
	/// calls in sync mode. Default: 1800 (30 minutes). Set to 0 or negative to disable.
	/// </summary>
	public int? DefaultOrchestraInvokeTimeoutSeconds { get; set; }

	/// <summary>
	/// Catch-all default transport timeout (seconds) applied to MCP tool calls for
	/// servers that are NOT the Orchestra data plane, when the orchestration's
	/// <c>mcps[]</c> entry does not specify a <c>timeoutSeconds</c>. <c>null</c> /
	/// missing leaves the Copilot SDK's built-in ~3-minute default in place;
	/// <c>0</c> applies an effectively-infinite transport timeout; a positive number
	/// applies that many seconds. Per-<c>mcps[]</c> overrides always win.
	/// </summary>
	public int? DefaultMcpToolCallTimeoutSeconds { get; set; }

	/// <summary>
	/// Default value for the <c>timeoutSeconds</c> argument of the
	/// <c>invoke_orchestration</c> MCP tool when the LLM caller doesn't supply one,
	/// in sync mode. Default: 300 seconds (5 minutes).
	/// </summary>
	public int? DefaultInvokeOrchestrationSyncTimeoutSeconds { get; set; }

	/// <summary>
	/// Per-server timeout (seconds) applied by <c>McpManager.GetGlobalMcpToolCountsAsync</c>
	/// when probing a required MCP's <c>tools/list</c> at step start. Raise this above the
	/// default for backends that use deferred-connection / interactive-OAuth: the first
	/// <c>tools/list</c> blocks while the backend lazily connects and authenticates, which
	/// can exceed the short default and surface as a spurious "returned 0 tools at pre-flight"
	/// failure. Maps to <see cref="McpServerOptions.ToolDiscoveryProbeTimeoutSeconds"/>.
	/// Default: 5. Values &lt; 1 are clamped to 1.
	/// </summary>
	public int? ToolDiscoveryProbeTimeoutSeconds { get; set; }

	/// <summary>
	/// Per-MCP timeout (seconds) applied by <c>McpManager.ProbeEndpointReachabilityAsync</c>
	/// when TCP-probing a remote MCP endpoint on the pre-flight error path (to distinguish
	/// "backend offline" from "backend reachable but returned 0 tools"). Maps to
	/// <see cref="McpServerOptions.EndpointReachabilityProbeTimeoutSeconds"/>. Default: 2.
	/// Values &lt; 1 are clamped to 1.
	/// </summary>
	public int? EndpointReachabilityProbeTimeoutSeconds { get; set; }
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
/// Copilot-provider-specific configuration section of the orchestra.json file.
/// All fields are nullable — only non-null values override the built-in defaults
/// captured in <see cref="CopilotProviderOptions"/>.
/// </summary>
public class CopilotProviderConfig
{
	/// <summary>
	/// Settings for the CLI-swap-and-resume recovery loop in <c>CopilotAgent</c>.
	/// </summary>
	public CopilotSwapConfig? Swap { get; set; }

	/// <summary>
	/// Optional GitHub token for Copilot authentication. <c>${VAR}</c> / <c>env:VAR</c>
	/// references are expanded before deserialization, so <c>"${GITHUB_TOKEN}"</c> works.
	/// </summary>
	public string? GitHubToken { get; set; }

	/// <summary>
	/// Optional override for the SDK's <c>UseLoggedInUser</c> flag.
	/// </summary>
	public bool? UseLoggedInUser { get; set; }

	/// <summary>
	/// Optional bound (seconds) on a Copilot session create/resume, covering inline MCP stdio
	/// server startup + initialize handshake. Null = built-in default (120s); 0 disables.
	/// See <see cref="CopilotProviderOptions.McpStartupTimeoutSeconds"/>.
	/// </summary>
	public int? McpStartupTimeoutSeconds { get; set; }
}

/// <summary>
/// OpenCode provider section of the config file. All fields are nullable — only non-null
/// values override the built-in <c>OpenCodeAgentPoolOptions</c> defaults. See
/// <see cref="OpenCodeProviderOptions"/> for full semantics.
/// </summary>
public class OpenCodeProviderConfig
{
	/// <summary>Path to the <c>opencode</c> binary (else PATH). The provider always spawns its own server.</summary>
	public string? CliPath { get; set; }

	/// <summary>Hostname spawned servers bind to (default <c>127.0.0.1</c>).</summary>
	public string? Hostname { get; set; }

	/// <summary>HTTP basic-auth password (OpenCode <c>OPENCODE_SERVER_PASSWORD</c>). Supports <c>${VAR}</c>.</summary>
	public string? ServerPassword { get; set; }

	/// <summary>HTTP basic-auth username (default <c>opencode</c>).</summary>
	public string? ServerUsername { get; set; }

	/// <summary>Provider applied to bare model ids (default <c>github-copilot</c>).</summary>
	public string? FallbackProvider { get; set; }

	/// <summary>Seconds to wait for a spawned server to become healthy (default 60).</summary>
	public int? StartupTimeoutSeconds { get; set; }

	/// <summary>Whether the engine-tool MCP bridge is enabled (default true).</summary>
	public bool? EngineToolBridgeEnabled { get; set; }

	/// <summary>Max in-provider cold-restart swaps per step after a transport failure (default 1).</summary>
	public int? SwapBudgetPerStep { get; set; }

	/// <summary>Whether a swap resumes the prior session vs cold-restarting (default true).</summary>
	public bool? ResumeOnSwapEnabled { get; set; }
}

/// <summary>
/// CLI swap policy section of the config file. See <see cref="CopilotSwapOptions"/>
/// for full semantics.
/// </summary>
public class CopilotSwapConfig
{
	/// <summary>
	/// Maximum number of CLI swaps a single prompt step may attempt before failing.
	/// Default: 3. Set to 0 to disable swap recovery entirely.
	/// </summary>
	public int? BudgetPerStep { get; set; }

	/// <summary>
	/// When true, the swap path calls <c>ResumeSessionAsync</c> on the new CLI with
	/// the prior session id, preserving conversation history. Default: true.
	/// </summary>
	public bool? ResumeOnSwap { get; set; }

	/// <summary>
	/// Maximum total time (seconds) the swap path waits for the SDK to report
	/// whether the resumed session is <c>AlreadyInUse</c> by the dying CLI before
	/// falling back to a cold restart. Default: 5.
	/// </summary>
	public double? ResumeAlreadyInUseWaitSeconds { get; set; }

	/// <summary>
	/// Interval (milliseconds) between resume-attempt polls inside the
	/// <see cref="ResumeAlreadyInUseWaitSeconds"/> window. Default: 500.
	/// </summary>
	public double? ResumeAlreadyInUsePollIntervalMs { get; set; }
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

	/// <summary>
	/// Default readiness timeout (in seconds) applied to every process service whose own
	/// <c>readiness.timeoutSeconds</c> is not set. When this is also unset, the built-in
	/// default (<see cref="ReadinessCheck.DefaultTimeoutSeconds"/>) applies.
	/// </summary>
	public int? DefaultReadinessTimeoutSeconds { get; set; }
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
