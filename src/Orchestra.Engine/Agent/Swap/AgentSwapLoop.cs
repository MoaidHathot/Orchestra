using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Orchestra.Engine;

/// <summary>
/// Recovery mode chosen by the swap loop after a transport-class failure.
/// </summary>
public enum SwapMode
{
	/// <summary>Reattach to the prior session id on a fresh worker (preserve conversation history).</summary>
	Resume,

	/// <summary>Create a new session from scratch and re-send the original prompt.</summary>
	ColdRestart,
}

/// <summary>
/// Provider-neutral swap/resume policy: how many in-provider worker swaps a single step may
/// attempt, and whether a swap may resume the prior session (vs always cold-restart).
/// </summary>
public sealed record SwapPolicy(int BudgetPerStep, bool ResumeEnabled)
{
	/// <summary>A swap policy that retries on a fresh worker but never resumes (cold restart only).</summary>
	public static SwapPolicy ColdRestartOnly(int budgetPerStep) => new(Math.Max(0, budgetPerStep), ResumeEnabled: false);

	/// <summary>A swap policy that performs no in-provider swaps.</summary>
	public static SwapPolicy Disabled { get; } = new(0, ResumeEnabled: false);
}

/// <summary>
/// Mutable wrapper that threads the current attempt's session id back to the swap loop
/// without throwing it through the exception. A sibling session may have latched the fault
/// first, so the exception's session id may not be ours — the attempt writes its own id here.
/// </summary>
public sealed class SwapSessionIdBox
{
	public string? Value;
}

/// <summary>Context handed to each swap attempt.</summary>
/// <param name="SwapAttempt">Zero on the first attempt; incremented per swap.</param>
/// <param name="PriorSessionId">The session id to resume, or null for a cold restart.</param>
/// <param name="SessionIdBox">The attempt writes its issued/resumed session id here.</param>
public sealed record SwapAttemptContext(int SwapAttempt, string? PriorSessionId, SwapSessionIdBox SessionIdBox);

/// <summary>
/// Classifies an exception as a swap-eligible (transport-class) failure, yielding a stable
/// reason string. Returns false for non-recoverable failures so they propagate untouched.
/// </summary>
public delegate bool SwapFailureClassifier(Exception exception, out string reason);

/// <summary>Sink for provider-specific swap metrics (e.g. a worker pool's swap counter).</summary>
public interface ISwapMetricsSink
{
	void RecordSwapTriggered();
}

/// <summary>No-op metrics sink for providers/tests that don't track swap counters.</summary>
public sealed class NullSwapMetricsSink : ISwapMetricsSink
{
	public static readonly NullSwapMetricsSink Instance = new();

	public void RecordSwapTriggered()
	{
	}
}

/// <summary>
/// Provider-neutral CLI/worker swap-and-resume loop shared by every agent provider.
///
/// Owns the budget loop, failure classification, swap-event emission, reporter + metrics
/// signalling, and resume-vs-cold-restart mode selection. A provider supplies a single
/// "run one attempt" callback (acquire worker → create/resume session → send → await
/// completion) plus an optional provider-specific classifier for failure shapes the neutral
/// classifier cannot see (e.g. an SDK abnormal-shutdown kind).
///
/// On a swap-eligible failure the loop, budget permitting, emits a
/// <see cref="AgentEventType.CliInstanceSwapped"/> event, records the swap, reports it, and
/// re-invokes the callback on a fresh worker — resuming the prior session when the policy
/// allows and a session id is available, otherwise cold-restarting. When the budget is
/// exhausted (or the failure is not swap-eligible) the original exception propagates.
/// </summary>
public sealed partial class AgentSwapLoop
{
	private readonly SwapPolicy _policy;
	private readonly IOrchestrationReporter _reporter;
	private readonly ISwapMetricsSink _metrics;
	private readonly string _stepName;
	private readonly ILogger _logger;
	private readonly Func<string, int, TimeSpan> _swapBackoff;

	public AgentSwapLoop(
		SwapPolicy policy,
		IOrchestrationReporter reporter,
		string stepName,
		ILogger logger,
		ISwapMetricsSink? metrics = null,
		Func<string, int, TimeSpan>? swapBackoff = null)
	{
		_policy = policy;
		_reporter = reporter;
		_stepName = stepName;
		_logger = logger;
		_metrics = metrics ?? NullSwapMetricsSink.Instance;
		// Default: no backoff (immediate retry) — preserves historical behavior for providers
		// that don't opt in. Copilot passes ExponentialUpstreamBackoff so upstream-transient
		// swaps wait briefly before re-attempting.
		_swapBackoff = swapBackoff ?? (static (_, _) => TimeSpan.Zero);
	}

	/// <summary>
	/// Builds a reason-gated exponential backoff: it delays only before retrying an
	/// UPSTREAM-transient failure (<c>transient_upstream</c> / <c>cli_exhausted_retries</c>),
	/// where a brief provider/network outage is likely and an immediate retry would just
	/// re-hit it. Local transport failures (<c>transport_lost</c> / <c>resume_*</c>) retry
	/// immediately — there is nothing upstream to wait on. Delay grows as
	/// <paramref name="baseDelay"/> × 2^(swap-1), capped at 4×. A non-positive
	/// <paramref name="baseDelay"/> disables backoff entirely (used by tests).
	/// </summary>
	public static Func<string, int, TimeSpan> ExponentialUpstreamBackoff(TimeSpan baseDelay)
	{
		if (baseDelay <= TimeSpan.Zero)
			return static (_, _) => TimeSpan.Zero;

		var baseMs = baseDelay.TotalMilliseconds;
		var capMs = baseMs * 4;
		return (reason, swapAttempt) =>
			reason is "transient_upstream" or "cli_exhausted_retries"
				? TimeSpan.FromMilliseconds(Math.Min(capMs, baseMs * Math.Pow(2, Math.Max(0, swapAttempt - 1))))
				: TimeSpan.Zero;
	}

	/// <summary>
	/// Runs <paramref name="runAttempt"/> under the swap budget, recovering from swap-eligible
	/// failures on fresh workers until the attempt succeeds, the budget is exhausted, or a
	/// non-recoverable failure propagates. Completes <paramref name="writer"/> on exit.
	/// </summary>
	public async Task<AgentResult> RunAsync(
		Func<SwapAttemptContext, CancellationToken, Task<AgentResult>> runAttempt,
		ChannelWriter<AgentEvent> writer,
		SwapFailureClassifier? providerClassifier = null,
		CancellationToken cancellationToken = default)
	{
		try
		{
			string? priorSessionId = null;
			var swapAttempt = 0;

			while (true)
			{
				// Box the inner attempt populates as soon as it has issued (or resumed) a
				// session id, so we know which id is OURS — distinct from any sibling session
				// that may have latched the fault broker first.
				var sessionIdBox = new SwapSessionIdBox();

				try
				{
					return await runAttempt(
						new SwapAttemptContext(swapAttempt, priorSessionId, sessionIdBox),
						cancellationToken).ConfigureAwait(false);
				}
				catch (OperationCanceledException)
				{
					throw;
				}
				catch (Exception ex) when (Classify(ex, providerClassifier, out var reason))
				{
					var attemptedSessionId = sessionIdBox.Value ?? priorSessionId;
					if (swapAttempt >= _policy.BudgetPerStep)
					{
						LogSwapBudgetExhausted(attemptedSessionId ?? "(none)", swapAttempt, _policy.BudgetPerStep, reason);

						// When at least one swap actually ran, the inner failure's message often
						// describes an intended-but-now-abandoned recovery (e.g. a resume failure
						// ending with "...falling back to cold restart"). Wrap it so the terminal
						// message makes the give-up explicit while keeping the original as
						// InnerException — the engine categorises the step by walking the inner
						// chain, so ClientUnhealthy / session-error details are preserved. When no
						// swap ran (budget 0 / swaps disabled) there is no such narrative to
						// correct, so the original first failure propagates unchanged.
						if (swapAttempt > 0)
						{
							throw new AgentSwapBudgetExhaustedException(swapAttempt, _policy.BudgetPerStep, reason, ex);
						}

						throw;
					}

					swapAttempt++;

					// resume_locked / resume_session_missing both mean the prior session can't
					// be replayed (lock contention or the worker no longer has the id). Force a
					// cold restart so we don't loop on a dead id; everything else honours policy.
					var nextMode = reason is "resume_locked" or "resume_session_missing"
						? SwapMode.ColdRestart
						: ResolveSwapMode(attemptedSessionId);

					LogSwapTriggered(attemptedSessionId ?? "(none)", swapAttempt, _policy.BudgetPerStep, reason, nextMode.ToString());

					EmitSwapEvent(writer, attemptedSessionId, swapAttempt, _policy.BudgetPerStep, reason, nextMode);

					_metrics.RecordSwapTriggered();
					_reporter.ReportCliSwapTriggered(
						_stepName,
						priorSessionId: attemptedSessionId,
						swapAttempt: swapAttempt,
						swapBudget: _policy.BudgetPerStep,
						reason: reason,
						mode: nextMode == SwapMode.Resume ? "resume" : "cold_restart");

					priorSessionId = nextMode == SwapMode.Resume ? attemptedSessionId : null;

					// Brief, reason-gated backoff before retrying so an upstream-transient
					// failure isn't immediately re-hit (no-op for local transport failures).
					var backoff = _swapBackoff(reason, swapAttempt);
					if (backoff > TimeSpan.Zero)
					{
						LogSwapBackoff(reason, swapAttempt, backoff.TotalMilliseconds);
						await Task.Delay(backoff, cancellationToken).ConfigureAwait(false);
					}
					// Loop back for another attempt on a fresh worker.
				}
			}
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		finally
		{
			writer.TryComplete();
		}
	}

	private SwapMode ResolveSwapMode(string? attemptedSessionId)
		=> _policy.ResumeEnabled && !string.IsNullOrEmpty(attemptedSessionId)
			? SwapMode.Resume
			: SwapMode.ColdRestart;

	private static bool Classify(Exception ex, SwapFailureClassifier? providerClassifier, out string reason)
	{
		if (providerClassifier is not null && providerClassifier(ex, out reason))
		{
			return true;
		}

		return TryClassifyNeutral(ex, out reason);
	}

	/// <summary>
	/// Neutral classifier recognising the cross-provider marker-interface failure shapes:
	/// <see cref="IAgentClientUnhealthyException"/> (transport lost / resume locked / resume
	/// session missing) and <see cref="IAgentSessionFailedException"/> whose details carry the
	/// <c>ExhaustedCliRetries</c> or <c>TransientUpstreamFailure</c> flag. Returns false for
	/// everything else (validation, cancellation, plain model errors).
	/// </summary>
	public static bool TryClassifyNeutral(Exception ex, out string reason)
	{
		reason = string.Empty;

		switch (ex)
		{
			case IAgentClientUnhealthyException unhealthy:
				reason = unhealthy.TriggeringFailureReason switch
				{
					"resume_locked" => "resume_locked",
					"resume_session_missing" => "resume_session_missing",
					_ => "transport_lost",
				};
				return true;

			case IAgentSessionFailedException sessionFailed:
				if (sessionFailed.Details?.ExhaustedCliRetries == true)
				{
					reason = "cli_exhausted_retries";
					return true;
				}

				if (sessionFailed.Details?.TransientUpstreamFailure == true)
				{
					reason = "transient_upstream";
					return true;
				}

				return false;

			default:
				return false;
		}
	}

	private static void EmitSwapEvent(
		ChannelWriter<AgentEvent> writer,
		string? priorSessionId,
		int swapAttempt,
		int swapBudget,
		string reason,
		SwapMode mode)
	{
		writer.TryWrite(new AgentEvent
		{
			Type = AgentEventType.CliInstanceSwapped,
			PriorSessionId = priorSessionId,
			SwapAttempt = swapAttempt,
			SwapBudget = swapBudget,
			SwapReason = reason,
			SwapMode = mode == SwapMode.Resume ? "resume" : "cold_restart",
		});
	}

	[LoggerMessage(
		EventId = 1,
		Level = LogLevel.Warning,
		Message = "Swap budget exhausted for session '{SessionId}' after {SwapAttempt}/{SwapBudget} attempts (reason: {Reason}); failing the step.")]
	private partial void LogSwapBudgetExhausted(string sessionId, int swapAttempt, int swapBudget, string reason);

	[LoggerMessage(
		EventId = 2,
		Level = LogLevel.Information,
		Message = "Swap #{SwapAttempt}/{SwapBudget} triggered for session '{SessionId}' (reason: {Reason}, mode: {Mode}).")]
	private partial void LogSwapTriggered(string sessionId, int swapAttempt, int swapBudget, string reason, string mode);

	[LoggerMessage(
		EventId = 3,
		Level = LogLevel.Information,
		Message = "Backing off {BackoffMs}ms before swap #{SwapAttempt} (reason: {Reason}) to let the upstream transient clear.")]
	private partial void LogSwapBackoff(string reason, int swapAttempt, double backoffMs);
}
