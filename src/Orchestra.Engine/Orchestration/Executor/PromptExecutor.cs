namespace Orchestra.Engine;

public class PromptExecutor : Executor<PromptOrchestrationStep>
{
    public override Task<ExecutionResult> ExecuteAsync(PromptOrchestrationStep step, ExecutionContext context)
    {
        throw new NotImplementedException();
    }
}
