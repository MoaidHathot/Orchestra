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
	/// Upper bound, in seconds, on the computed backoff delay (applied before jitter). Null
	/// (the default) leaves exponential growth unbounded, which with the defaults reaches
	/// 1s, 2s, 4s, 8s, 16s... Set this so a long retry budget does not turn into multi-minute waits.
	/// </summary>
	public double? MaxDelaySeconds { get; init; }

	/// <summary>
	/// Maximum random jitter, in seconds, added to every delay: the actual wait is the computed
	/// backoff plus a uniformly random value in <c>[0, JitterSeconds]</c>. Spreads out the retries
	/// of steps that failed together (a rate-limited provider, a restarted service) so they do not
	/// all hit the dependency again at the same instant. Default 0 (deterministic delays).
	/// </summary>
	public double JitterSeconds { get; init; }

	/// <summary>
	/// Whether to retry when the step fails due to a timeout (OperationCanceledException
	/// from a per-step timeout, not from orchestration-level cancellation).
	/// Default is true.
	/// </summary>
	public bool RetryOnTimeout { get; init; } = true;

	/// <summary>
	/// Calculates the delay before the given retry attempt (1-based): exponential backoff,
	/// capped by <see cref="MaxDelaySeconds"/>, plus random jitter up to <see cref="JitterSeconds"/>.
	/// </summary>
	public TimeSpan GetDelay(int attempt) => GetDelay(attempt, Random.Shared);

	/// <summary>
	/// <see cref="GetDelay(int)"/> with an explicit randomness source, for deterministic tests.
	/// </summary>
	public TimeSpan GetDelay(int attempt, Random random)
	{
		ArgumentNullException.ThrowIfNull(random);

		var seconds = GetBaseDelaySeconds(attempt);
		if (MaxDelaySeconds is { } max && seconds > max)
			seconds = Math.Max(0, max);

		if (JitterSeconds > 0)
			seconds += random.NextDouble() * JitterSeconds;

		return TimeSpan.FromSeconds(seconds);
	}

	/// <summary>The exponential backoff for <paramref name="attempt"/> before cap and jitter.</summary>
	public double GetBaseDelaySeconds(int attempt)
	{
		if (attempt <= 1)
			return BackoffSeconds;

		return BackoffSeconds * Math.Pow(BackoffMultiplier, attempt - 1);
	}
}
