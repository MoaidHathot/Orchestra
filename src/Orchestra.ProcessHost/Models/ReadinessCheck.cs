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
	/// Built-in default readiness timeout (seconds), used when neither the service's own
	/// <see cref="TimeoutSeconds"/> nor the global <c>defaultReadinessTimeoutSeconds</c>
	/// (in orchestra.services.json) is set.
	/// </summary>
	public const int DefaultTimeoutSeconds = 30;

	/// <summary>
	/// Maximum time in seconds to wait for the process to become ready.
	/// When unset (<see langword="null"/>), the global <c>defaultReadinessTimeoutSeconds</c>
	/// from orchestra.services.json applies, falling back to <see cref="DefaultTimeoutSeconds"/>.
	/// </summary>
	public int? TimeoutSeconds { get; set; }

	/// <summary>
	/// Poll interval in milliseconds for HTTP health checks.
	/// Only used when <see cref="HealthCheckUrl"/> is set.
	/// </summary>
	public int IntervalMs { get; init; } = 500;
}
