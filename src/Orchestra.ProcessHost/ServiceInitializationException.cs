namespace Orchestra.ProcessHost;

/// <summary>
/// Exception thrown when service initialization fails due to a required service
/// (beforeStart hook or process) failing to start or pass readiness checks.
/// </summary>
public class ServiceInitializationException : Exception
{
	public ServiceInitializationException(string message) : base(message) { }
	public ServiceInitializationException(string message, Exception innerException) : base(message, innerException) { }
}
