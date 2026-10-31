namespace Orchestra.Engine.Tests.TestHelpers;

/// <summary>Exercises the real engine with controllable, provider-independent step work.</summary>
internal sealed class DelegateStepExecutor(
	OrchestrationStepType stepType,
	Func<OrchestrationStep, OrchestrationExecutionContext, CancellationToken, Task<ExecutionResult>> execute) : IStepExecutor
{
	public OrchestrationStepType StepType => stepType;

	public Task<ExecutionResult> ExecuteAsync(OrchestrationStep step, OrchestrationExecutionContext context, CancellationToken cancellationToken = default)
		=> execute(step, context, cancellationToken);
}
