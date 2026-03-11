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
	/// Optional path to auto-scan for orchestration files on startup.
	/// </summary>
	public string? OrchestrationsScanPath { get; set; }

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
}
