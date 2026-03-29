namespace Orchestra.Engine;

/// <summary>
/// Shared mutable context passed to engine tools during a prompt step execution.
/// Engine tools record side effects here, which the executor inspects after completion.
/// </summary>
public sealed class EngineToolContext
{
	/// <summary>
	/// When set, the prompt step result will be overridden to the specified status
	/// regardless of the LLM's output content.
	/// </summary>
	public ExecutionStatus? StatusOverride { get; private set; }

	/// <summary>
	/// The reason provided by the LLM when signaling the execution status via the set_status tool.
	/// </summary>
	public string? StatusReason { get; private set; }

	/// <summary>
	/// Whether the status has been explicitly set by an engine tool.
	/// </summary>
	public bool HasStatusOverride => StatusOverride is not null;

	/// <summary>
	/// Sets the execution status override. Can only transition to a "worse" state
	/// (e.g., from null to Failed). Once failed, cannot be reset to succeeded.
	/// </summary>
	public void SetStatus(ExecutionStatus status, string? reason = null)
	{
		// Only allow setting status if not already failed
		if (StatusOverride == ExecutionStatus.Failed)
			return;

		StatusOverride = status;
		StatusReason = reason;
	}
}
