namespace Orchestra.Engine;

/// <summary>
/// Thrown by <see cref="AgentSwapLoop"/> when a step's CLI-swap recovery budget is
/// exhausted: every permitted swap (resume and/or cold restart) has been attempted and the
/// step still failed. The final underlying failure is wrapped as
/// <see cref="Exception.InnerException"/> so the engine's failure categorisation — which
/// walks the inner-exception chain — still observes the original marker exception
/// (<see cref="IAgentClientUnhealthyException"/> / <see cref="IAgentSessionFailedException"/>)
/// and categorises the step exactly as before.
///
/// The distinct message exists because the inner failure often describes an <em>intended</em>
/// next step — e.g. a resume failure whose message ends with "falling back to cold restart".
/// Once the budget is spent no such restart happens, so the terminal message surfaced to
/// <c>run.json</c> / <c>result.md</c> must make the give-up explicit instead of leaving that
/// misleading "falling back to cold restart" text as the last word.
/// </summary>
public sealed class AgentSwapBudgetExhaustedException : Exception
{
	public AgentSwapBudgetExhaustedException(int swapAttempts, int swapBudget, string reason, Exception inner)
		: base(BuildMessage(swapAttempts, swapBudget, reason, inner), inner)
	{
		SwapAttempts = swapAttempts;
		SwapBudget = swapBudget;
		Reason = reason;
	}

	/// <summary>Number of swaps performed before the budget was exhausted.</summary>
	public int SwapAttempts { get; }

	/// <summary>The configured swap budget for the step.</summary>
	public int SwapBudget { get; }

	/// <summary>The swap-eligible failure reason of the final attempt (e.g. <c>transport_lost</c>).</summary>
	public string Reason { get; }

	private static string BuildMessage(int swapAttempts, int swapBudget, string reason, Exception inner) =>
		$"Agent CLI recovery gave up: the swap budget ({swapBudget}) was exhausted after " +
		$"{swapAttempts} swap(s). The step failed on a '{reason}' failure; no further resume or " +
		$"cold restart was attempted. Underlying error: {inner.Message}";
}
