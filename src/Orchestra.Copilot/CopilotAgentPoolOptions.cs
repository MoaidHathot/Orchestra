namespace Orchestra.Copilot;

/// <summary>
/// Default Copilot provider pool settings used when an orchestration does not
/// request explicit agentPool values.
/// </summary>
public sealed class CopilotAgentPoolOptions
{
	public int DefaultMinInstances { get; set; } = 1;

	/// <summary>
	/// Default cap on CLI processes per orchestration run. Bumped from 4 to 8 so the
	/// CLI-swap recovery path (<see cref="CliSwapBudgetPerStep"/>) has headroom even when
	/// several other prompt steps are running concurrently. Each worker is still a
	/// real Copilot CLI process; tune this if memory is a concern on the host.
	/// </summary>
	public int DefaultMaxInstancesPerRun { get; set; } = 8;

	public int DefaultMaxSessionsPerInstance { get; set; } = 1;
	public int DefaultIdleTimeoutSeconds { get; set; } = 120;

	/// <summary>
	/// Upper bound on how long a single <c>CreateSessionAsync</c>/<c>ResumeSessionAsync</c> call
	/// may take. This call spawns the session's inline MCP stdio servers and performs their
	/// <c>initialize</c> handshake inside the Copilot SDK; a misconfigured or unresponsive MCP
	/// server (e.g. a command that never starts, or one that never answers <c>initialize</c>)
	/// would otherwise leave the step "running" indefinitely with no output until manually
	/// cancelled. When the deadline elapses the attempt fails with a clear diagnostic and the
	/// swap loop can retry on a fresh worker. Default 120s — generous enough to absorb a
	/// first-run package restore (e.g. <c>dnx</c>/NuGet acquiring a tool) while still bounding a
	/// true hang. Set to <see cref="TimeSpan.Zero"/> to disable the guard.
	/// </summary>
	public TimeSpan McpStartupTimeout { get; set; } = TimeSpan.FromSeconds(120);

	// ── Authentication (host-level default; per-step githubToken still overrides) ──

	/// <summary>
	/// Optional GitHub token applied to every Copilot CLI client in the run, making auth
	/// deterministic for servers/CI instead of relying solely on the CLI's stored
	/// credentials. Sourced from <c>orchestra.json</c> <c>copilot.gitHubToken</c>
	/// (which supports <c>${ENV}</c> expansion). Null = use the CLI's own auth.
	/// </summary>
	public string? GitHubToken { get; set; }

	/// <summary>
	/// When set, controls the SDK's <c>UseLoggedInUser</c> flag — whether the runtime
	/// attempts to use stored OAuth / gh-CLI auth. Sourced from <c>orchestra.json</c>
	/// <c>copilot.useLoggedInUser</c>. Null = SDK default.
	/// </summary>
	public bool? UseLoggedInUser { get; set; }

	// ── CLI-swap / session-resume recovery (Phase 1–3) ──

	/// <summary>
	/// Number of CLI swaps a single prompt step may attempt before giving up. Each swap
	/// abandons the failed CLI worker and picks a fresh one from the pool (or spawns a
	/// new one if under <see cref="DefaultMaxInstancesPerRun"/>). Swap is consumed only
	/// for transport-level / CLI-internal failures — pure model errors still flow through
	/// the orchestration-level retry policy.
	/// </summary>
	public int CliSwapBudgetPerStep { get; set; } = 3;

	/// <summary>
	/// When true, the swap path will call <c>ResumeSessionAsync</c> on the new CLI with
	/// the prior session id (preserving conversation history). When false, the swap path
	/// always cold-restarts: brand-new session, original prompt re-sent. Default true.
	/// </summary>
	public bool ResumeOnSwapEnabled { get; set; } = true;

	/// <summary>
	/// Maximum total time the swap path will wait for the SDK to report whether the
	/// resumed session is <c>AlreadyInUse</c> by the dying CLI before falling back to
	/// a cold restart. Default 5 seconds — long enough to absorb a graceful CLI shutdown
	/// flush, short enough not to add a noticeable delay to recovery.
	/// </summary>
	public TimeSpan ResumeAlreadyInUseWait { get; set; } = TimeSpan.FromSeconds(5);

	/// <summary>
	/// Interval between resume-attempt retries inside the <see cref="ResumeAlreadyInUseWait"/>
	/// window. Default 500 ms.
	/// </summary>
	public TimeSpan ResumeAlreadyInUsePollInterval { get; set; } = TimeSpan.FromMilliseconds(500);
}
