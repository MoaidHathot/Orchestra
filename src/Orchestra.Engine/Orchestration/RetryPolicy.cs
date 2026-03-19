namespace Orchestra.Engine;

/// <summary>
/// Configuration for retry behavior on failed step executions.
/// Can be applied per-step or as a global default on the orchestration.
/// </summary>
public class RetryPolicy
{
	/// <summary>
	/// Maximum number of retry attempts after the initial execution fails.
	/// For example, a value of 3 means up to 4 total attempts (1 initial + 3 retries).
	/// Default is 3.
	/// </summary>
	public int MaxRetries { get; init; } = 3;

	/// <summary>
	/// Initial backoff delay in seconds before the first retry.
	/// Subsequent retries multiply this by <see cref="BackoffMultiplier"/> each time.
	/// Default is 1 second.
	/// </summary>
	public double BackoffSeconds { get; init; } = 1.0;

	/// <summary>
	/// Multiplier applied to the backoff delay after each retry attempt.
	/// For example, with BackoffSeconds=1 and BackoffMultiplier=2:
	/// retry 1 waits 1s, retry 2 waits 2s, retry 3 waits 4s.
	/// Default is 2.0 (exponential backoff).
	/// </summary>
	public double BackoffMultiplier { get; init; } = 2.0;

	/// <summary>
	/// Whether to retry when the step fails due to a timeout (OperationCanceledException
	/// from a per-step timeout, not from orchestration-level cancellation).
	/// Default is true.
	/// </summary>
	public bool RetryOnTimeout { get; init; } = true;

	/// <summary>
	/// Calculates the delay before the given retry attempt (1-based).
	/// </summary>
	public TimeSpan GetDelay(int attempt)
	{
		if (attempt <= 1)
			return TimeSpan.FromSeconds(BackoffSeconds);

		var delay = BackoffSeconds * Math.Pow(BackoffMultiplier, attempt - 1);
		return TimeSpan.FromSeconds(delay);
	}
}
