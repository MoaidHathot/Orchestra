using GitHub.Copilot.SDK;
using Microsoft.Extensions.DependencyInjection;
using OrchestrationEngine.Copilot.Services;
using OrchestrationEngine.Core.Abstractions;
using OrchestrationEngine.Core.Models;

namespace OrchestrationEngine.Copilot;

/// <summary>
/// Extension methods for registering Copilot SDK services.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds GitHub Copilot SDK implementation for agents.
    /// </summary>
    public static IServiceCollection AddCopilotAgents(
        this IServiceCollection services,
        McpConfiguration mcpConfiguration)
    {
        services.AddSingleton(mcpConfiguration);
        services.AddSingleton<CopilotClient>();
        services.AddSingleton<IAgentBuilderFactory, CopilotAgentBuilderFactory>();
        services.AddSingleton<IAgentRepository, CopilotAgentRepository>();
        
        return services;
    }
}
