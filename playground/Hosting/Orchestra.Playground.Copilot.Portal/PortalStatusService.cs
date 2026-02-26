namespace Orchestra.Playground.Copilot.Portal;

/// <summary>
/// Service for tracking and reporting portal status.
/// </summary>
public class PortalStatusService
{
	/// <summary>
	/// Gets the current status as a snapshot object.
	/// </summary>
	public PortalStatus GetStatus()
	{
		return new PortalStatus
		{
			StartTime = _startTime,
			UptimeSeconds = (int)(DateTime.UtcNow - _startTime).TotalSeconds
		};
	}

	private readonly DateTime _startTime = DateTime.UtcNow;
}

/// <summary>
/// Snapshot of portal status for API responses.
/// </summary>
public class PortalStatus
{
	public required DateTime StartTime { get; init; }
	public required int UptimeSeconds { get; init; }
}
