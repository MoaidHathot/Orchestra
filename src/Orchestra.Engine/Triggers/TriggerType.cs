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
	/// No automated trigger — the orchestration is run manually by a user.
	/// This is the default when no trigger is specified in the orchestration definition.
	/// </summary>
	Manual,
}
