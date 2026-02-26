namespace Orchestra.Engine;

/// <summary>
/// Triggers orchestration when unread emails arrive in a specified Outlook folder.
/// Polls Outlook via Microsoft Graph API and marks processed emails as read.
/// Requires Azure AD/Entra app registration with Mail.Read and Mail.ReadWrite permissions.
/// </summary>
public class EmailTriggerConfig : TriggerConfig
{
	/// <summary>
	/// Outlook folder path to poll (e.g., "Inbox", "Inbox/Teams").
	/// Default is "Inbox".
	/// </summary>
	public string FolderPath { get; init; } = "Inbox";

	/// <summary>
	/// How often to poll for new messages, in seconds.
	/// Default is 60 seconds.
	/// </summary>
	public int PollIntervalSeconds { get; init; } = 60;

	/// <summary>
	/// Maximum items to process per poll cycle.
	/// Default is 10. Useful for catching up after downtime without overwhelming the system.
	/// </summary>
	public int MaxItemsPerPoll { get; init; } = 10;

	/// <summary>
	/// Optional filter: only process emails with subject containing this string.
	/// </summary>
	public string? SubjectContains { get; init; }

	/// <summary>
	/// Optional filter: only process emails from senders containing this string.
	/// </summary>
	public string? SenderContains { get; init; }
}
