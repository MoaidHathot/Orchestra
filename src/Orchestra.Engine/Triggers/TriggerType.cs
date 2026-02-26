namespace Orchestra.Engine;

public enum TriggerType
{
	/// <summary>
	/// Cron-expression or interval-based scheduling.
	/// </summary>
	Scheduler,

	/// <summary>
	/// Automatically re-runs the orchestration when it completes.
	/// </summary>
	Loop,

	/// <summary>
	/// Triggered by an external HTTP POST (Power Automate, Zapier, etc.).
	/// </summary>
	Webhook,

	/// <summary>
	/// Triggered by unread emails in an Outlook folder.
	/// Polls Outlook via COM interop and marks processed emails as read.
	/// </summary>
	Email,
}
