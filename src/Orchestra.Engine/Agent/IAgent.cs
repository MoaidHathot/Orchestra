namespace Orchestra.Engine;

public interface IAgent<TResult, TEvent>
{
    AgentTask<TResult, TEvent> SendAsync(string prompt, CancellationToken cancellationToken = default);
}
