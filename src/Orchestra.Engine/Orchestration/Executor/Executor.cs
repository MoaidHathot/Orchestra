namespace Orchestra.Engine;

public abstract class Executor<TStep>
	where TStep : OrchestrationStep
{
	public required TStep OrchestrationStep { get; init; }
	public abstract Task<ExecutionResult> ExecuteAsync(TStep step, ExecutionContext context);
}

