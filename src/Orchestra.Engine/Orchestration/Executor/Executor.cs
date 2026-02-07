namespace Orchestra.Engine;

public abstract class Executor<TStep> where TStep : OrchestrationStep
{
	public abstract Task<ExecutionResult> ExecuteAsync(TStep step, OrchestrationExecutionContext context, CancellationToken cancellationToken = default);
}
