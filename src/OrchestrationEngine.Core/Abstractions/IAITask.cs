using OrchestrationEngine.Core.Events;

namespace OrchestrationEngine.Core.Abstractions;

/// <summary>
/// Represents an async task returned by an agent that can be awaited
/// and streamed for events.
/// </summary>
public interface IAITask : IAsyncEnumerable<AgentEvent>
{
    /// <summary>
    /// Waits for the task to complete and returns the final response content.
    /// </summary>
    Task<string> GetResultAsync(CancellationToken cancellationToken = default);
}
