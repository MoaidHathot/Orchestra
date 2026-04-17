using Microsoft.Extensions.Hosting;
using Orchestra.ProcessHost;

namespace Orchestra.Host.Hosting;

/// <summary>
/// Lightweight hosted service that shuts down the <see cref="ServiceManager"/> during the
/// host's graceful shutdown phase. Registered FIRST among hosted services so that it stops
/// LAST (IHostedService instances are stopped in reverse registration order), ensuring
/// managed external processes outlive MCPs, triggers, and other hosted services that may
/// depend on them.
/// </summary>
internal sealed class ServiceManagerShutdownService(ServiceManager serviceManager) : IHostedService
{
	public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

	public Task StopAsync(CancellationToken cancellationToken)
		=> serviceManager.StopAsync(cancellationToken);
}
