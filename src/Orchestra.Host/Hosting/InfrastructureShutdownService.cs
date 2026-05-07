using Microsoft.Extensions.Hosting;
using Orchestra.Host.Mcp;
using Orchestra.ProcessHost;

namespace Orchestra.Host.Hosting;

/// <summary>
/// Lightweight hosted service that shuts down external infrastructure during the host's
/// graceful shutdown phase. Registered FIRST among hosted services so that it stops LAST
/// (IHostedService instances are stopped in reverse registration order), after triggers
/// and other hosted services have stopped using MCPs and managed services.
/// </summary>
internal sealed class InfrastructureShutdownService(ServiceManager serviceManager, McpManager mcpManager) : IHostedService
{
	public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

	public Task StopAsync(CancellationToken cancellationToken)
		=> OrchestraInfrastructureLifecycle.StopAsync(serviceManager, mcpManager, cancellationToken);
}
