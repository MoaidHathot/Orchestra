using Orchestra.Engine;

namespace Orchestra.Host.Api;

/// <summary>
/// Factory that creates <see cref="SseReporter"/> instances for orchestration executions.
/// Registered in DI by <c>AddOrchestraHost</c> so that all execution paths
/// (manual, trigger, MCP invoke) use the same reporter type.
///
/// When constructed via DI the factory is wired with the singleton
/// <see cref="DashboardEventBroadcaster"/> so each created reporter can fan-out HITL
/// lifecycle events (<c>awaiting-input</c>, <c>input-received</c>, <c>input-timeout</c>) to
/// the dashboard SSE stream consumed by the Portal. Manual construction (e.g. in unit
/// tests) keeps the broadcaster null — fan-out is silently skipped.
/// </summary>
public class SseReporterFactory : IOrchestrationReporterFactory
{
	private readonly DashboardEventBroadcaster? _dashboardBroadcaster;

	public SseReporterFactory()
	{
	}

	public SseReporterFactory(DashboardEventBroadcaster dashboardBroadcaster)
	{
		_dashboardBroadcaster = dashboardBroadcaster;
	}

	public IOrchestrationReporter Create() => new SseReporter(_dashboardBroadcaster);
}
