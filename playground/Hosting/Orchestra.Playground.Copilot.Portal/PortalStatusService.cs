using Orchestra.Outlook;

namespace Orchestra.Playground.Copilot.Portal;

/// <summary>
/// Service for tracking and reporting portal status, including Outlook connection state.
/// </summary>
public class PortalStatusService
{
	private readonly object _lock = new();

	/// <summary>
	/// Current Outlook connection status.
	/// </summary>
	public OutlookConnectionStatus OutlookStatus { get; private set; } = OutlookConnectionStatus.Disconnected;

	/// <summary>
	/// Timestamp of the last successful Outlook poll.
	/// </summary>
	public DateTime? LastOutlookPoll { get; private set; }

	/// <summary>
	/// Last error message from Outlook operations.
	/// </summary>
	public string? LastOutlookError { get; private set; }

	/// <summary>
	/// Total number of emails processed since portal started.
	/// </summary>
	public int ProcessedEmailCount { get; private set; }

	/// <summary>
	/// Number of active email triggers.
	/// </summary>
	public int ActiveEmailTriggerCount { get; private set; }

	/// <summary>
	/// Updates the Outlook connection status.
	/// </summary>
	public void UpdateOutlookStatus(OutlookConnectionStatus status, string? error = null)
	{
		lock (_lock)
		{
			OutlookStatus = status;
			if (error != null)
			{
				LastOutlookError = error;
			}
			else if (status == OutlookConnectionStatus.Connected)
			{
				LastOutlookError = null;
			}
		}
	}

	/// <summary>
	/// Records a successful poll operation.
	/// </summary>
	public void RecordSuccessfulPoll(int processedCount = 0)
	{
		lock (_lock)
		{
			LastOutlookPoll = DateTime.UtcNow;
			ProcessedEmailCount += processedCount;
			LastOutlookError = null;
		}
	}

	/// <summary>
	/// Updates the count of active email triggers.
	/// </summary>
	public void UpdateActiveEmailTriggerCount(int count)
	{
		lock (_lock)
		{
			ActiveEmailTriggerCount = count;
		}
	}

	/// <summary>
	/// Gets the current status as a snapshot object.
	/// </summary>
	public PortalStatus GetStatus()
	{
		lock (_lock)
		{
			return new PortalStatus
			{
				Outlook = new OutlookStatusInfo
				{
					Status = OutlookStatus.ToString(),
					LastPoll = LastOutlookPoll,
					LastError = LastOutlookError,
					ProcessedCount = ProcessedEmailCount,
					ActiveTriggers = ActiveEmailTriggerCount,
				}
			};
		}
	}
}

/// <summary>
/// Snapshot of portal status for API responses.
/// </summary>
public class PortalStatus
{
	public required OutlookStatusInfo Outlook { get; init; }
}

/// <summary>
/// Outlook-specific status information.
/// </summary>
public class OutlookStatusInfo
{
	public required string Status { get; init; }
	public DateTime? LastPoll { get; init; }
	public string? LastError { get; init; }
	public int ProcessedCount { get; init; }
	public int ActiveTriggers { get; init; }
}
