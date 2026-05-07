using Orchestra.Host.Mcp;
using Orchestra.ProcessHost;

namespace Orchestra.Host.Hosting;

internal static class OrchestraInfrastructureLifecycle
{
	public static Task InitializeAsync(
		ServiceManager serviceManager,
		McpManager mcpManager,
		ServiceEntry[] serviceEntries,
		Engine.Mcp[] globalMcps,
		CancellationToken cancellationToken = default)
		=> InitializeAsync(
			ct => serviceManager.InitializeAsync(serviceEntries, ct),
			ct => mcpManager.InitializeAsync(globalMcps, ct),
			ct => serviceManager.StopAsync(ct),
			ct => mcpManager.StopAsync(ct),
			cancellationToken);

	internal static async Task InitializeAsync(
		Func<CancellationToken, Task> initializeServicesAsync,
		Func<CancellationToken, Task> initializeMcpsAsync,
		Func<CancellationToken, Task> stopServicesAsync,
		Func<CancellationToken, Task> stopMcpsAsync,
		CancellationToken cancellationToken = default)
	{
		using var startupCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

		var serviceInitialization = StartLifecycleTask(initializeServicesAsync, startupCts.Token);
		var mcpInitialization = StartLifecycleTask(initializeMcpsAsync, startupCts.Token);

		try
		{
			var firstCompleted = await Task.WhenAny(serviceInitialization, mcpInitialization);
			if (!firstCompleted.IsCompletedSuccessfully)
			{
				startupCts.Cancel();
			}

			await Task.WhenAll(serviceInitialization, mcpInitialization);
		}
		catch
		{
			startupCts.Cancel();

			try
			{
				await Task.WhenAll(serviceInitialization, mcpInitialization);
			}
			catch
			{
				// Preserve the original startup failure; cleanup is best-effort here.
			}

			try
			{
				await StopAsync(stopServicesAsync, stopMcpsAsync, CancellationToken.None);
			}
			catch
			{
				// Preserve the original startup failure; cleanup is best-effort here.
			}

			throw;
		}
	}

	public static Task StopAsync(
		ServiceManager serviceManager,
		McpManager mcpManager,
		CancellationToken cancellationToken = default)
		=> StopAsync(
			ct => serviceManager.StopAsync(ct),
			ct => mcpManager.StopAsync(ct),
			cancellationToken);

	internal static Task StopAsync(
		Func<CancellationToken, Task> stopServicesAsync,
		Func<CancellationToken, Task> stopMcpsAsync,
		CancellationToken cancellationToken = default)
	{
		var stopServices = StartLifecycleTask(stopServicesAsync, cancellationToken);
		var stopMcps = StartLifecycleTask(stopMcpsAsync, cancellationToken);

		return Task.WhenAll(stopServices, stopMcps);
	}

	private static Task StartLifecycleTask(
		Func<CancellationToken, Task> operation,
		CancellationToken cancellationToken)
		// Schedule each lifecycle operation independently so synchronous setup work
		// before the first await cannot serialize the other operation.
		=> Task.Run(() => operation(cancellationToken));
}
