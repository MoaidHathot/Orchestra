namespace Orchestra.Engine;

/// <summary>
/// IStepExecutor adapter that wraps the existing generic <see cref="PromptExecutor"/>
/// to work with the non-generic <see cref="IStepExecutor"/> interface.
/// </summary>
public sealed class PromptStepExecutor : IStepExecutor
{
	private readonly PromptExecutor _inner;

	public PromptStepExecutor(PromptExecutor inner)
	{
		_inner = inner;
	}

	public OrchestrationStepType StepType => OrchestrationStepType.Prompt;

	public Task<ExecutionResult> ExecuteAsync(
		OrchestrationStep step,
		OrchestrationExecutionContext context,
		CancellationToken cancellationToken = default)
	{
		if (step is not PromptOrchestrationStep promptStep)
			throw new InvalidOperationException(
				$"PromptStepExecutor received a step of type '{step.GetType().Name}' " +
				$"but expected '{nameof(PromptOrchestrationStep)}'.");

		return _inner.ExecuteAsync(promptStep, context, cancellationToken);
	}
}
