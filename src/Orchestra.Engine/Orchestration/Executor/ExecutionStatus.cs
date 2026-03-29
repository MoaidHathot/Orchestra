namespace Orchestra.Engine;

public enum ExecutionStatus
{
	Pending,
	Running,
	Succeeded,
	Failed,
	Skipped,
	Cancelled,

	/// <summary>
	/// The step completed successfully but determined that no further action is needed.
	/// Downstream dependent steps will be skipped.
	/// </summary>
	NoAction,
}
