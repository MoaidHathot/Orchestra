namespace Orchestra.Host.Hosting;

/// <summary>
/// Configuration options for Orchestra hosting.
/// </summary>
public class OrchestrationHostOptions
{
	/// <summary>
	/// The root data path for all Orchestra data (runs, triggers, registry, etc.)
	/// Default: %LOCALAPPDATA%/OrchestraHost
	/// </summary>
	public string DataPath { get; set; } = Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
		"OrchestraHost");

	/// <summary>
	/// Base URL for the Orchestra web UI (used to generate links to run details).
	/// Example: "http://localhost:5000" will generate URLs like "http://localhost:5000/#/history/{orchestration}/{runId}"
	/// If null, no URLs will be displayed.
	/// </summary>
	public string? HostBaseUrl { get; set; }

	/// <summary>
	/// Configuration for automatic orchestration directory scanning and watching.
	/// When set, Orchestra scans the directory on startup (registering new orchestrations,
	/// updating changed ones, and removing deleted ones). If <see cref="OrchestrationsScanConfig.Watch"/>
	/// is enabled, a file watcher monitors the directory for live changes at runtime.
	/// </summary>
	public OrchestrationsScanConfig? OrchestrationsScan { get; set; }

	/// <summary>
	/// Whether to automatically load persisted orchestrations on startup.
	/// Default: true
	/// </summary>
	public bool LoadPersistedOrchestrations { get; set; } = true;

	/// <summary>
	/// Whether to automatically load persisted triggers on startup.
	/// Default: true
	/// </summary>
	public bool LoadPersistedTriggers { get; set; } = true;

	/// <summary>
	/// Whether to register JSON-defined triggers from loaded orchestrations.
	/// Default: true
	/// </summary>
	public bool RegisterJsonTriggers { get; set; } = true;

	/// <summary>
	/// Retention policy for automatic cleanup of old run records.
	/// Default: no limits (runs are kept forever).
	/// </summary>
	public RetentionPolicy Retention { get; set; } = new();

	/// <summary>
	/// Maximum time in seconds to wait for in-flight tasks during graceful shutdown.
	/// Default: 30
	/// </summary>
	public int ShutdownTimeoutSeconds { get; set; } = 30;

	/// <summary>
	/// Minimum log level for the file logger.
	/// Default: "Information"
	/// </summary>
	public string LogLevel { get; set; } = "Information";

	/// <summary>
	/// Polling intervals for the web UI, in milliseconds.
	/// These control how frequently the portal refreshes data from the server.
	/// </summary>
	public PollingOptions Polling { get; set; } = new();

	/// <summary>
	/// Default AI model to use for internal LLM calls (e.g., trigger input handlers)
	/// when no model is explicitly specified.
	/// If null, defaults to "claude-opus-4.6".
	/// </summary>
	public string? DefaultModel { get; set; }
}

/// <summary>
/// Polling interval configuration for the web UI.
/// All values are in milliseconds.
/// </summary>
public class PollingOptions
{
	/// <summary>
	/// How often to poll for active execution updates (running/pending).
	/// Default: 1000ms (1 second).
	/// </summary>
	public int ActiveExecutionsMs { get; set; } = 1000;

	/// <summary>
	/// How often to poll the orchestrations list for external changes.
	/// Default: 5000ms (5 seconds).
	/// </summary>
	public int OrchestrationsMs { get; set; } = 5000;

	/// <summary>
	/// How often to poll execution history.
	/// Default: 5000ms (5 seconds).
	/// </summary>
	public int HistoryMs { get; set; } = 5000;

	/// <summary>
	/// How often to poll server status (connections, counts, etc.).
	/// Default: 5000ms (5 seconds).
	/// </summary>
	public int ServerStatusMs { get; set; } = 5000;
}

/// <summary>
/// Configuration for automatic orchestration directory scanning and watching.
/// </summary>
public class OrchestrationsScanConfig
{
	/// <summary>
	/// Directory path to scan for orchestration files (.json, .yaml, .yml).
	/// </summary>
	public required string Directory { get; set; }

	/// <summary>
	/// If true, watch the directory for file changes at runtime and
	/// automatically register, update, or remove orchestrations.
	/// Default: false.
	/// </summary>
	public bool Watch { get; set; }

	/// <summary>
	/// If true, scan subdirectories recursively.
	/// Default: false.
	/// </summary>
	public bool Recursive { get; set; }
}
