namespace Orchestra.Copilot;

/// <summary>
/// Immutable snapshot of the swap-and-resume configuration captured from
/// <see cref="CopilotAgentPoolOptions"/> at <see cref="CopilotAgent"/> construction time.
/// Decouples the agent from the live options bag so concurrent runs see a stable view of
/// the policy values for the duration of a single <c>SendAsync</c> call.
/// </summary>
internal sealed record CopilotAgentSwapOptions(
	int CliSwapBudgetPerStep,
	bool ResumeOnSwapEnabled,
	TimeSpan ResumeAlreadyInUseWait,
	TimeSpan ResumeAlreadyInUsePollInterval,
	TimeSpan McpStartupTimeout,
	TimeSpan SwapBackoffBase = default)
{
	public static CopilotAgentSwapOptions FromPoolOptions(CopilotAgentPoolOptions options) => new(
		CliSwapBudgetPerStep: Math.Max(0, options.CliSwapBudgetPerStep),
		ResumeOnSwapEnabled: options.ResumeOnSwapEnabled,
		ResumeAlreadyInUseWait: options.ResumeAlreadyInUseWait < TimeSpan.Zero
			? TimeSpan.Zero
			: options.ResumeAlreadyInUseWait,
		ResumeAlreadyInUsePollInterval: options.ResumeAlreadyInUsePollInterval <= TimeSpan.Zero
			? TimeSpan.FromMilliseconds(250)
			: options.ResumeAlreadyInUsePollInterval,
		McpStartupTimeout: options.McpStartupTimeout < TimeSpan.Zero
			? TimeSpan.Zero
			: options.McpStartupTimeout,
		// Base delay for the exponential backoff the swap loop applies before retrying an
		// UPSTREAM-transient failure (transient_upstream / cli_exhausted_retries) so we don't
		// immediately re-hit a brief provider/network outage. 1s → 1s/2s/4s across swaps.
		SwapBackoffBase: TimeSpan.FromSeconds(1));

	/// <summary>
	/// Default options for test / fallback paths where the builder isn't involved.
	/// Mirrors the production defaults in <see cref="CopilotAgentPoolOptions"/>.
	/// </summary>
	public static CopilotAgentSwapOptions Defaults { get; } = FromPoolOptions(new CopilotAgentPoolOptions());
}
