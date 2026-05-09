namespace Orchestra.Engine;

/// <summary>
/// Tracks the cumulative time an orchestration spends awaiting human input and re-arms
/// the orchestration timeout CTS so a long human wait does not consume the budget.
/// <para>
/// Algorithm: on <see cref="BeginWait"/> we capture <c>now</c>; on <see cref="EndWait"/>
/// we add the elapsed delta to <c>TotalWaitElapsed</c> and re-issue
/// <c>CancelAfter(originalDeadline + TotalWaitElapsed - elapsedSinceRunStart)</c> on the
/// orchestration timeout CTS. This is safe with the existing CTS lifecycle because
/// <c>CancelAfter</c> is idempotent and replaces the prior timer.
/// </para>
/// <para>
/// Multiple concurrent waits are supported: <c>TotalWaitElapsed</c> only accumulates the
/// time during which AT LEAST ONE wait was outstanding (parallel waits don't double-count).
/// </para>
/// </summary>
internal sealed class ClockPauseTracker
{
	private readonly CancellationTokenSource _orchestrationTimeoutCts;
	private readonly TimeSpan _originalTimeout;
	private readonly DateTimeOffset _runStartedAt;
	private readonly Lock _lock = new();
	private int _activeWaitCount;
	private DateTimeOffset? _waitStart;
	private TimeSpan _totalWaitElapsed = TimeSpan.Zero;

	public ClockPauseTracker(CancellationTokenSource orchestrationTimeoutCts, int timeoutSeconds, DateTimeOffset runStartedAt)
	{
		_orchestrationTimeoutCts = orchestrationTimeoutCts;
		_originalTimeout = TimeSpan.FromSeconds(timeoutSeconds);
		_runStartedAt = runStartedAt;
	}

	/// <summary>
	/// Total wait time accumulated so far. Surfaced for tests and diagnostics.
	/// </summary>
	public TimeSpan TotalWaitElapsed
	{
		get
		{
			lock (_lock)
			{
				return _totalWaitElapsed;
			}
		}
	}

	public void BeginWait()
	{
		lock (_lock)
		{
			_activeWaitCount++;
			if (_activeWaitCount == 1)
			{
				_waitStart = DateTimeOffset.UtcNow;

				// Pause the timeout: disable the CancelAfter timer entirely. We'll re-arm it
				// in EndWait once we know the actual remaining compute budget. Without this
				// pause, a long human wait could trigger the original deadline mid-wait
				// before EndWait gets a chance to extend it.
				try
				{
					_orchestrationTimeoutCts.CancelAfter(Timeout.InfiniteTimeSpan);
				}
				catch (ObjectDisposedException)
				{
					// CTS already disposed (run completed). Safe to ignore.
				}
			}
		}
	}

	public void EndWait()
	{
		lock (_lock)
		{
			if (_activeWaitCount == 0)
				return; // Defensive: out-of-order EndWait

			_activeWaitCount--;
			if (_activeWaitCount > 0 || _waitStart is null)
				return; // Some waits still outstanding; don't accumulate yet.

			var delta = DateTimeOffset.UtcNow - _waitStart.Value;
			_totalWaitElapsed += delta;
			_waitStart = null;

			// Re-arm the orchestration timeout to account for the time spent waiting.
			// Compute the *remaining* compute time:
			//   total = original + totalWaitElapsed
			//   elapsed = now - runStartedAt
			//   remaining = total - elapsed
			// CancelAfter expects a non-negative TimeSpan; clamp to zero (effectively cancel
			// immediately) when the remaining budget is gone.
			var elapsed = DateTimeOffset.UtcNow - _runStartedAt;
			var newDeadline = _originalTimeout + _totalWaitElapsed - elapsed;
			if (newDeadline < TimeSpan.Zero)
				newDeadline = TimeSpan.Zero;

			try
			{
				_orchestrationTimeoutCts.CancelAfter(newDeadline);
			}
			catch (ObjectDisposedException)
			{
				// CTS already disposed (run completed/cancelled). Safe to ignore.
			}
		}
	}
}
