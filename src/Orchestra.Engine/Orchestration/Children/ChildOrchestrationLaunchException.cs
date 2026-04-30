namespace Orchestra.Engine;

/// <summary>
/// Thrown by <see cref="IChildOrchestrationLauncher.LaunchAsync"/> when the launch cannot
/// proceed because of a pre-execution failure (orchestration not found, parse error, nesting
/// depth exceeded, etc.). Errors that occur during execution are reflected on
/// <see cref="ChildOrchestrationResult.Status"/> and <see cref="ChildOrchestrationResult.ErrorMessage"/>
/// instead.
/// </summary>
public sealed class ChildOrchestrationLaunchException : Exception
{
	/// <summary>
	/// Stable code identifying the failure category. One of:
	/// <c>orchestration_not_found</c>, <c>parse_failed</c>, <c>max_nesting_depth_exceeded</c>.
	/// </summary>
	public string ErrorCode { get; }

	public ChildOrchestrationLaunchException(string errorCode, string message)
		: base(message)
	{
		ErrorCode = errorCode;
	}

	public ChildOrchestrationLaunchException(string errorCode, string message, Exception innerException)
		: base(message, innerException)
	{
		ErrorCode = errorCode;
	}

	public const string OrchestrationNotFound = "orchestration_not_found";
	public const string ParseFailed = "parse_failed";
	public const string MaxNestingDepthExceeded = "max_nesting_depth_exceeded";
}
