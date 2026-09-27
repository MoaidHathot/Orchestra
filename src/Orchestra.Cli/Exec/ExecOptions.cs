namespace Orchestra.Exec;

/// <summary>
/// How a one-shot run obtains the Orchestra host it runs against.
/// </summary>
internal enum ExecMode
{
	/// <summary>Use an already-running instance when a server URL is configured and healthy;
	/// otherwise spawn a throwaway isolated instance and terminate it afterward.</summary>
	Auto,

	/// <summary>Always spawn a throwaway isolated instance (never reuse a running one).</summary>
	Isolated,

	/// <summary>Require a configured, healthy running instance; error if none is reachable.</summary>
	Existing,
}

/// <summary>
/// Post-run report format for <c>--report</c>.
/// </summary>
internal enum ReportFormat
{
	/// <summary>No report (default): just the result + records path.</summary>
	None,

	/// <summary>Human-readable plain-text digest.</summary>
	Text,

	/// <summary>Structured Markdown (headings + tables).</summary>
	Markdown,

	/// <summary>The raw run record JSON (the same data the Portal consumes).</summary>
	Json,
}

/// <summary>
/// Parsed options for a one-shot run (<c>orchestra run</c> / <c>orchestra exec</c>). Constructed
/// either by the Spectre command (from typed settings) or by <see cref="Parse"/> (argv / tests).
/// </summary>
internal sealed class ExecOptions
{
	/// <summary>How to obtain the host (connect to a running instance vs spawn a throwaway one).</summary>
	public ExecMode Mode { get; init; } = ExecMode.Auto;

	/// <summary>Explicit Orchestra server URL to connect to. Falls back to <c>ORCHESTRA_URL</c> then orchestra.json.</summary>
	public string? ServerUrl { get; init; }

	/// <summary>Ignore the user's <c>orchestra.json</c> / services / global MCP for the spawned instance.</summary>
	public bool NoConfig { get; init; }

	/// <summary>Extra tags applied (in addition to the defaults) when registering into a running instance.</summary>
	public string[] Tags { get; init; } = [];

	/// <summary>Keep a <c>--run-file</c> orchestration registered in a running instance after the run
	/// (default is to remove what we registered, leaving the shared instance unchanged).</summary>
	public bool KeepRegistered { get; init; }

	/// <summary>Registered orchestration id or declared name to run (mutually exclusive with <see cref="RunFile"/>).</summary>
	public string? RunId { get; init; }

	/// <summary>Path to an orchestration file to register-then-run (mutually exclusive with <see cref="RunId"/>).</summary>
	public string? RunFile { get; init; }

	/// <summary>Runtime parameters (repeated <c>--param key=value</c>).</summary>
	public Dictionary<string, string>? Parameters { get; init; }

	/// <summary>Optional hard wall-clock timeout for the run, in seconds.</summary>
	public int? TimeoutSeconds { get; init; }

	/// <summary>Print every SSE event (firehose).</summary>
	public bool Verbose { get; init; }

	/// <summary>Suppress per-step chatter; show only HITL prompts and the final summary.</summary>
	public bool Quiet { get; init; }

	/// <summary>Don't prompt on HITL pauses; print the pending-input message and exit 2.</summary>
	public bool NoInteractive { get; init; }

	/// <summary>Audit identifier recorded with any HITL responses submitted.</summary>
	public string? RespondedBy { get; init; }

	/// <summary>Write the run's final content to this file instead of printing it to stdout.</summary>
	public string? OutputFile { get; init; }

	/// <summary>Surface a richer set of real-time events (model, MCP/tool calls, sub-agents, retries).</summary>
	public bool Detailed { get; init; }

	/// <summary>Post-run report format (default <see cref="ReportFormat.None"/>).</summary>
	public ReportFormat Report { get; init; } = ReportFormat.None;

	/// <summary>Write the report to this file instead of stdout.</summary>
	public string? ReportOutput { get; init; }

	/// <summary>Root data path for run history / registry (overrides config / env).</summary>
	public string? DataPath { get; init; }

	/// <summary>Workspace directory scanned for orchestrations (so <c>--run name</c> resolves).</summary>
	public string? OrchestrationsPath { get; init; }

	/// <summary>Validate and print the execution plan (layers, steps, parameters) without running anything.</summary>
	public bool DryRun { get; init; }
}
