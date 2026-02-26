namespace Orchestra.Outlook;

/// <summary>
/// Configuration options for polling Outlook for new messages.
/// </summary>
public class OutlookPollingOptions
{
	/// <summary>
	/// Outlook folder path to poll (e.g., "Inbox", "Inbox/Subfolder").
	/// Default is "Inbox".
	/// </summary>
	public string FolderPath { get; init; } = "Inbox";

	/// <summary>
	/// How often to poll for new messages, in seconds.
	/// Default is 60 seconds.
	/// </summary>
	public int PollIntervalSeconds { get; init; } = 60;

	/// <summary>
	/// Maximum number of items to process per poll cycle.
	/// Default is 10. Useful for catching up after downtime without overwhelming the system.
	/// </summary>
	public int MaxItemsPerPoll { get; init; } = 10;

	/// <summary>
	/// Whether to only poll unread messages.
	/// Default is true.
	/// </summary>
	public bool UnreadOnly { get; init; } = true;

	/// <summary>
	/// Optional filter: only process emails with subject containing this string.
	/// </summary>
	public string? SubjectContains { get; init; }

	/// <summary>
	/// Optional filter: only process emails from senders containing this string.
	/// </summary>
	public string? SenderContains { get; init; }
}
