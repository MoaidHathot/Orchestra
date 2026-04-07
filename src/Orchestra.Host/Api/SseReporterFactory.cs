using Orchestra.Engine;

namespace Orchestra.Host.Api;

/// <summary>
/// Factory that creates <see cref="SseReporter"/> instances for orchestration executions.
/// Registered in DI by <c>AddOrchestraHost</c> so that all execution paths
/// (manual, trigger, MCP invoke) use the same reporter type.
/// </summary>
public class SseReporterFactory : IOrchestrationReporterFactory
{
	public IOrchestrationReporter Create() => new SseReporter();
}
