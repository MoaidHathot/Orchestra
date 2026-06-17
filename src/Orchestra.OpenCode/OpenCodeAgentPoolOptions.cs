namespace Orchestra.OpenCode;

/// <summary>
/// Default OpenCode provider settings used when an orchestration does not request explicit
/// agentPool values, plus the OpenCode-specific knobs for locating / launching the
/// <c>opencode serve</c> process and addressing models. Mirrors
/// <c>Orchestra.Copilot.CopilotAgentPoolOptions</c>.
/// </summary>
public sealed class OpenCodeAgentPoolOptions
{
	public int DefaultMinInstances { get; set; } = 1;

	/// <summary>
	/// Default cap on <c>opencode serve</c> processes per orchestration run.
	/// </summary>
	public int DefaultMaxInstancesPerRun { get; set; } = 4;

	/// <summary>
	/// Sessions per OpenCode instance. Defaults to <c>1</c> so each prompt step gets its own
	/// server process — this keeps the per-step engine-tool MCP bridge correlation unambiguous
	/// (one in-flight <c>EngineToolContext</c> per instance) and isolates step failures.
	/// </summary>
	public int DefaultMaxSessionsPerInstance { get; set; } = 1;

	public int DefaultIdleTimeoutSeconds { get; set; } = 120;

	// ── Server discovery / lifecycle ──

	/// <summary>
	/// Internal test seam only: when set, the adapter connects to this base URL instead of
	/// spawning <c>opencode serve</c>. NOT exposed via host config — the OpenCode provider is
	/// spawn-only in production. Used by tests to drive a fake server without launching a process.
	/// </summary>
	public string? ServerUrl { get; set; }

	/// <summary>
	/// Explicit path to the <c>opencode</c> binary. When null, the adapter resolves
	/// <c>ORCHESTRA_OPENCODE_PATH</c> then <c>opencode</c> on PATH.
	/// </summary>
	public string? CliPath { get; set; }

	/// <summary>Hostname the spawned server binds to. Default <c>127.0.0.1</c>.</summary>
	public string Hostname { get; set; } = "127.0.0.1";

	/// <summary>
	/// Optional HTTP basic-auth password the server requires (OpenCode <c>OPENCODE_SERVER_PASSWORD</c>).
	/// Applied to spawned servers and sent on requests to connected servers. Null = no auth.
	/// </summary>
	public string? ServerPassword { get; set; }

	/// <summary>Basic-auth username paired with <see cref="ServerPassword"/>. Default <c>opencode</c>.</summary>
	public string ServerUsername { get; set; } = "opencode";

	/// <summary>
	/// Provider applied to bare model ids (no <c>provider/</c> prefix) when routing a step to
	/// OpenCode. Default <c>github-copilot</c> so Copilot-style model ids (e.g.
	/// <c>claude-opus-4.8</c>) resolve to <c>github-copilot/claude-opus-4.8</c>.
	/// </summary>
	public string FallbackProvider { get; set; } = "github-copilot";

	/// <summary>Maximum time to wait for a spawned server's <c>/global/health</c> to report ready.</summary>
	public TimeSpan StartupTimeout { get; set; } = TimeSpan.FromSeconds(60);

	/// <summary>
	/// When true, the per-step engine tools (orchestra_set_status / complete / save_file /
	/// read_file / request_user_input) are exposed to OpenCode via a loopback HTTP MCP bridge.
	/// Default true. Set false to run OpenCode steps without Orchestra engine tools.
	/// </summary>
	public bool EngineToolBridgeEnabled { get; set; } = true;

	/// <summary>
	/// Maximum in-provider worker swaps a single step may attempt after a transport-class
	/// failure (event-stream loss or a transient upstream session error). OpenCode has no
	/// session-resume primitive, so every swap is a cold restart on a fresh server: the failed
	/// attempt's session is deleted and the original prompt is re-sent. Default <c>1</c> — one
	/// fresh-worker retry clears most transient transport faults; <c>0</c> disables in-provider
	/// swapping (the executor-level fallback still applies).
	/// </summary>
	public int SwapBudgetPerStep { get; set; } = 1;

	/// <summary>
	/// When true (default), a swap resumes the prior OpenCode session (which persists in OpenCode's
	/// data dir, shared across server processes) by re-prompting its id, preserving any tool-call
	/// progress from the failed attempt. When false, every swap cold-restarts on a brand-new session.
	/// If the prior session can't be reached, the attempt falls back to a fresh session automatically.
	/// </summary>
	public bool ResumeOnSwapEnabled { get; set; } = true;
}
