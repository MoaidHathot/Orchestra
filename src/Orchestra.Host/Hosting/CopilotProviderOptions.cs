namespace Orchestra.Host.Hosting;

/// <summary>
/// Copilot-provider-specific runtime options exposed in <c>orchestra.json</c>. The
/// host project is provider-neutral and does not directly depend on
/// <c>Orchestra.Copilot</c>; <c>Orchestra.Server.Program.cs</c> maps these values
/// onto <c>CopilotAgentPoolOptions</c> at <c>CopilotAgentBuilder</c> construction
/// time. Defaults mirror those of <c>CopilotAgentPoolOptions</c> so leaving the
/// section empty produces the same behaviour as code-only construction.
/// </summary>
public sealed class CopilotProviderOptions
{
	/// <summary>
	/// Settings for the CLI-swap-and-resume recovery loop in <c>CopilotAgent</c>.
	/// </summary>
	public CopilotSwapOptions Swap { get; set; } = new();
}

/// <summary>
/// CLI swap-and-resume recovery policy. A "swap" abandons the current Copilot CLI
/// worker mid-step (after a transport-level failure or after the CLI exhausts its
/// own model-API retries) and tries again on a fresh worker. When session resume is
/// enabled the new worker reattaches to the prior session id, preserving conversation
/// history; otherwise the original prompt is re-sent on a brand-new session.
/// </summary>
public sealed class CopilotSwapOptions
{
	/// <summary>
	/// Maximum number of CLI swaps a single prompt step may attempt before failing.
	/// Each swap is consumed only by transport-class failures (broker latched the
	/// worker unhealthy, CLI emitted an exhaustion error, abnormal CLI shutdown);
	/// pure model errors still flow through the orchestration-level retry policy.
	/// Default: 3. Set to 0 to disable swap recovery entirely.
	/// </summary>
	public int BudgetPerStep { get; set; } = 3;

	/// <summary>
	/// When <c>true</c>, the swap path calls <c>ResumeSessionAsync</c> on the new
	/// CLI with the prior session id, preserving conversation history (and avoiding
	/// re-execution of any tool side effects the prior turn may already have run).
	/// When <c>false</c>, the swap always cold-restarts: brand-new session, original
	/// prompt re-sent. Default: <c>true</c>.
	/// </summary>
	public bool ResumeOnSwap { get; set; } = true;

	/// <summary>
	/// Maximum total time the swap path waits for the SDK to report whether the
	/// resumed session is <c>AlreadyInUse</c> by the dying CLI before falling back
	/// to a cold restart. Default: 5 seconds.
	/// </summary>
	public double ResumeAlreadyInUseWaitSeconds { get; set; } = 5;

	/// <summary>
	/// Interval between resume-attempt polls inside the
	/// <see cref="ResumeAlreadyInUseWaitSeconds"/> window. Default: 500 ms.
	/// </summary>
	public double ResumeAlreadyInUsePollIntervalMs { get; set; } = 500;
}
