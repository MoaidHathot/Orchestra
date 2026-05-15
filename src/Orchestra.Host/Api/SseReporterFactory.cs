using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Orchestra.Engine;
using Orchestra.Host.Hosting;

namespace Orchestra.Host.Api;

/// <summary>
/// Factory that creates <see cref="SseReporter"/> instances for orchestration executions.
/// Registered in DI by <c>AddOrchestraHost</c> so that all execution paths
/// (manual, trigger, MCP invoke) use the same reporter type.
///
/// When constructed via DI the factory is wired with the singleton
/// <see cref="DashboardEventBroadcaster"/> (so each created reporter can fan-out HITL
/// lifecycle events to the dashboard SSE stream), the configured <see cref="SseOptions"/>
/// (so replay buffer / channel / subscriber caps are honored), and an
/// <see cref="ILoggerFactory"/> (so each reporter can emit structured warnings when
/// events are evicted or dropped). Manual construction (e.g. in unit tests) keeps the
/// broadcaster null, uses default options, and routes logging to <see cref="NullLogger"/>.
/// </summary>
public class SseReporterFactory : IOrchestrationReporterFactory
{
	private readonly DashboardEventBroadcaster? _dashboardBroadcaster;
	private readonly SseOptions _options;
	private readonly ILoggerFactory _loggerFactory;

	public SseReporterFactory()
	{
		_options = new SseOptions();
		_loggerFactory = NullLoggerFactory.Instance;
	}

	public SseReporterFactory(
		DashboardEventBroadcaster dashboardBroadcaster,
		SseOptions options,
		ILoggerFactory loggerFactory)
	{
		_dashboardBroadcaster = dashboardBroadcaster;
		_options = options;
		_loggerFactory = loggerFactory;
	}

	public IOrchestrationReporter Create() => new SseReporter(
		_dashboardBroadcaster,
		_options,
		_loggerFactory.CreateLogger<SseReporter>());
}
