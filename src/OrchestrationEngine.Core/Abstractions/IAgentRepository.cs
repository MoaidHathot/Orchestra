using OrchestrationEngine.Core.Models;

namespace OrchestrationEngine.Core.Abstractions;

/// <summary>
/// Repository for creating preconfigured and dynamic agents.
/// </summary>
public interface IAgentRepository
{
    /// <summary>
    /// Creates an agent for handling input transformation between steps.
    /// </summary>
    Task<IAgent> CreateInputHandlerAgentAsync(
        string handleInputPrompt,
        string? model = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates an agent for handling output transformation after a step.
    /// </summary>
    Task<IAgent> CreateOutputHandlerAgentAsync(
        string handleOutputPrompt,
        string? model = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates an agent for extracting and filling placeholders in prompts.
    /// </summary>
    Task<IAgent> CreatePlaceholderAgentAsync(
        string? model = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates an orchestration step agent based on the step configuration.
    /// </summary>
    Task<IAgent> CreateOrchestrationAgentAsync(
        OrchestrationStep step,
        CancellationToken cancellationToken = default);
}
