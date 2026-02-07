namespace Orchestra.Engine;

public class AgentTask<TResult, TEvent> : IAsyncEnumerable<TEvent>
{
    public IAsyncEnumerator<TEvent> GetAsyncEnumerator(CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }
}
