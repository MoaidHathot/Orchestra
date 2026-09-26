namespace Orchestra.Engine;

/// <summary>
/// How a run reacts when one of its steps ends as <see cref="ExecutionStatus.Failed"/>.
/// Set per orchestration via <c>onStepFailure</c>.
/// </summary>
public enum StepFailurePolicy
{
	/// <summary>
	/// The default. Dependents of the failed step are skipped (the skip cascades through the
	/// DAG) while independent branches keep running to completion. The run is reported Failed
	/// at the end.
	/// </summary>
	Continue,

	/// <summary>
	/// Fail fast: as soon as a step fails, every step that has not finished is cancelled and
	/// the run ends as Failed. Useful when later steps cost money or have side effects that
	/// make no sense once an earlier step has failed.
	/// </summary>
	Stop,
}
