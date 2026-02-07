namespace Orchestra.Engine;

public abstract class Executor<TStep, TResult>
	where TStep : OrchestrationStep
{
	public required TStep OrchestrationStep { get; init; }
	public abstract Task<ExecutionResult<TResult>> ExecuteAsync(TStep step, ExecutionContext context);
}

