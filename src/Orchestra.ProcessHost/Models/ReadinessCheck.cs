namespace Orchestra.ProcessHost;

/// <summary>
/// Configuration for detecting when a managed process is ready to accept connections
/// or has completed its initialization.
/// At least one of <see cref="StdoutPattern"/> or <see cref="HealthCheckUrl"/> must be specified.
/// </summary>
public class ReadinessCheck
{
	/// <summary>
	/// A regex pattern to match against stdout/stderr lines.
	/// When a matching line is detected, the process is considered ready.
	/// </summary>
	public string? StdoutPattern { get; init; }

	/// <summary>
	/// An HTTP GET endpoint to poll. A 200 response indicates readiness.
	/// </summary>
	public string? HealthCheckUrl { get; init; }

	/// <summary>
	/// Maximum time in seconds to wait for the process to become ready.
	/// </summary>
	public int TimeoutSeconds { get; init; } = 30;

	/// <summary>
	/// Poll interval in milliseconds for HTTP health checks.
	/// Only used when <see cref="HealthCheckUrl"/> is set.
	/// </summary>
	public int IntervalMs { get; init; } = 500;
}
