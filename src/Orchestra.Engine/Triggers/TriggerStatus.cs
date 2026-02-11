namespace Orchestra.Engine;

/// <summary>
/// Runtime status of a trigger.
/// </summary>
public enum TriggerStatus
{
	/// <summary>Trigger is configured but not yet activated.</summary>
	Idle,

	/// <summary>Trigger is active and waiting for the next fire event.</summary>
	Waiting,

	/// <summary>Trigger has fired and the orchestration is currently running.</summary>
	Running,

	/// <summary>Trigger is paused (user disabled it or it reached max runs).</summary>
	Paused,

	/// <summary>Trigger completed its configured runs (e.g., maxRuns reached).</summary>
	Completed,

	/// <summary>Trigger encountered an error.</summary>
	Error,
}
