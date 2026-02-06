using Microsoft.Extensions.DependencyInjection;
using OrchestrationEngine.Core.Abstractions;
using OrchestrationEngine.Core.Services;

namespace OrchestrationEngine.Core;

/// <summary>
/// Extension methods for registering core services.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds core orchestration engine services.
    /// </summary>
    public static IServiceCollection AddOrchestrationCore(
        this IServiceCollection services,
        string promptsDirectory = "prompts")
    {
        services.AddSingleton<ConfigurationLoader>();
        services.AddSingleton(new PromptLoader(promptsDirectory));
        services.AddScoped<IOrchestrationEngine, OrchestrationExecutor>();
        return services;
    }
}
