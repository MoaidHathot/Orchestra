using OrchestrationEngine.Core.Models;

namespace OrchestrationEngine.Core.Abstractions;

/// <summary>
/// Engine that executes orchestration definitions.
/// </summary>
public interface IOrchestrationEngine
{
    /// <summary>
    /// Executes the orchestration and returns the final output.
    /// </summary>
    Task<string> ExecuteAsync(
        OrchestrationDefinition orchestration,
        CancellationToken cancellationToken = default);
}
