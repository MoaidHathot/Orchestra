namespace Orchestra.Outlook;

/// <summary>
/// Represents an email message retrieved from Outlook.
/// </summary>
public class OutlookMessage
{
	/// <summary>
	/// Unique identifier for the message in Outlook.
	/// Used to mark the message as read after processing.
	/// </summary>
	public required string EntryId { get; init; }

	/// <summary>
	/// Email subject line.
	/// </summary>
	public required string Subject { get; init; }

	/// <summary>
	/// Plain text body of the email.
	/// </summary>
	public required string Body { get; init; }

	/// <summary>
	/// HTML body of the email, if available.
	/// </summary>
	public string? HtmlBody { get; init; }

	/// <summary>
	/// Display name of the sender.
	/// </summary>
	public required string Sender { get; init; }

	/// <summary>
	/// Email address of the sender.
	/// </summary>
	public required string SenderEmail { get; init; }

	/// <summary>
	/// When the email was received.
	/// </summary>
	public required DateTime ReceivedTime { get; init; }

	/// <summary>
	/// List of recipient email addresses.
	/// </summary>
	public required string[] Recipients { get; init; }

	/// <summary>
	/// Whether the email is currently unread.
	/// </summary>
	public required bool IsUnread { get; init; }

	/// <summary>
	/// Conversation ID for threading.
	/// </summary>
	public string? ConversationId { get; init; }
}
