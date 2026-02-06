namespace OrchestrationEngine.Core.Abstractions;

/// <summary>
/// Represents an agent that can process prompts and return streaming results.
/// </summary>
public interface IAgent : IAsyncDisposable
{
    /// <summary>
    /// Sends a prompt to the agent and returns a streaming task.
    /// </summary>
    IAITask SendAsync(string prompt, CancellationToken cancellationToken = default);
}
