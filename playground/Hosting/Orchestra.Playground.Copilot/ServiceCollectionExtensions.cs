using Microsoft.Extensions.DependencyInjection;
using Orchestra.Copilot;
using Orchestra.Engine;

namespace Orchestra.Playground.Copilot;

public static class ServiceCollectionExtensions
{
	public static IServiceCollection AddOrchestra(this IServiceCollection services)
	{
		services.AddSingleton<AgentBuilder, CopilotAgentBuilder>();
		services.AddSingleton<IScheduler, OrchestrationScheduler>();
		services.AddSingleton<OrchestraWorker>();

		return services;
	}
}
