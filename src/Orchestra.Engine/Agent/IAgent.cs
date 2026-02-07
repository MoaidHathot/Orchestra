namespace Orchestra.Engine;

public interface IAgent
{
    AgentTask SendAsync(string prompt, CancellationToken cancellationToken = default);
}
