namespace Orchestra.Engine;

public class PromptExecutor : Executor<PromptOrchestrationStep, string>
{
    public override Task<ExecutionResult<string>> ExecuteAsync(PromptOrchestrationStep step, ExecutionContext context)
    {
        throw new NotImplementedException();
    }
}
