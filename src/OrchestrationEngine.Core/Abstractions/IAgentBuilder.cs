namespace OrchestrationEngine.Core.Abstractions;

/// <summary>
/// Fluent builder for configuring and creating agents.
/// </summary>
public interface IAgentBuilder
{
    /// <summary>
    /// Sets the system prompt for the agent.
    /// </summary>
    IAgentBuilder WithSystemPrompt(string systemPrompt);

    /// <summary>
    /// Sets the model to use.
    /// </summary>
    IAgentBuilder WithModel(string model);

    /// <summary>
    /// Adds MCP server tools by name.
    /// </summary>
    IAgentBuilder WithMcpServers(params string[] mcpServerNames);

    /// <summary>
    /// Enables streaming mode.
    /// </summary>
    IAgentBuilder WithStreaming(bool enabled = true);

    /// <summary>
    /// Builds and returns the configured agent.
    /// </summary>
    Task<IAgent> BuildAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Factory for creating agent builders.
/// </summary>
public interface IAgentBuilderFactory
{
    /// <summary>
    /// Creates a new agent builder instance.
    /// </summary>
    IAgentBuilder Create();
}
