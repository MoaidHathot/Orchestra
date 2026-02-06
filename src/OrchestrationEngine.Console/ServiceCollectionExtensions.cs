using Microsoft.Extensions.DependencyInjection;
using OrchestrationEngine.Console.Tui;
using OrchestrationEngine.Core.Abstractions;

namespace OrchestrationEngine.Console;

/// <summary>
/// Extension methods for registering Console TUI services.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds the default Spectre.Console TUI implementation of IProgressReporter.
    /// </summary>
    public static IServiceCollection AddConsoleTui(this IServiceCollection services)
    {
        services.AddSingleton<IProgressReporter, SpectreProgressReporter>();
        return services;
    }
}
