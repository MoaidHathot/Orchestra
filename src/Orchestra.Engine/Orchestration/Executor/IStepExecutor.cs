namespace Orchestra.Engine;

/// <summary>
/// Non-generic interface for step execution, allowing the orchestration executor
/// to dispatch step execution without knowing the concrete step type at compile time.
/// </summary>
public interface IStepExecutor
{
	/// <summary>
	/// Gets the step type this executor handles.
	/// </summary>
	OrchestrationStepType StepType { get; }

	/// <summary>
	/// Executes the given step and returns the result.
	/// </summary>
	Task<ExecutionResult> ExecuteAsync(
		OrchestrationStep step,
		OrchestrationExecutionContext context,
		CancellationToken cancellationToken = default);
}
