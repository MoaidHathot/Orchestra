using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orchestra.Watchers.Runtime;
using Orchestra.Watchers.State;

namespace Orchestra.Watchers;

/// <summary>
/// DI registration for the watcher runtime.
/// </summary>
public static class WatcherServiceCollectionExtensions
{
	/// <summary>Configuration key (mirrors <c>skip-services</c>) that disables loading <c>orchestra.watchers.json</c>.</summary>
	public const string SkipWatchersConfigurationKey = "skip-watchers";

	/// <summary>
	/// Registers the watcher services. Every registration uses <c>TryAdd</c>, so a host may register
	/// its own <see cref="IWatcherDispatcher"/> or <see cref="IWatcherStateStore"/> beforehand. The
	/// default dispatcher goes straight to <see cref="Orchestra.Engine.IChildOrchestrationLauncher"/>.
	/// </summary>
	/// <param name="services">The service collection.</param>
	/// <param name="stateDirectory">Resolves the directory for state files (see <see cref="FileWatcherStateStore.ResolveStateDirectory"/>).</param>
	public static IServiceCollection AddOrchestraWatchers(this IServiceCollection services, Func<IServiceProvider, string> stateDirectory)
	{
		ArgumentNullException.ThrowIfNull(services);
		ArgumentNullException.ThrowIfNull(stateDirectory);

		services.TryAddSingleton<IWatcherStateStore>(sp => new FileWatcherStateStore(stateDirectory(sp), sp.GetRequiredService<ILogger<FileWatcherStateStore>>()));
		services.TryAddSingleton<WatcherSubscriptionRegistry>();
		services.TryAddSingleton<IWatcherFetcherFactory, WatcherFetcherFactory>();
		services.TryAddSingleton<WatcherOutputValidator>();
		services.TryAddSingleton<IWatcherDispatcher>(sp => new LauncherWatcherDispatcher(sp.GetRequiredService<Orchestra.Engine.IChildOrchestrationLauncher>()));
		services.TryAddSingleton<WatcherManager>(sp => new WatcherManager(
			sp.GetRequiredService<WatcherSubscriptionRegistry>(),
			sp.GetRequiredService<IWatcherStateStore>(),
			sp.GetRequiredService<IWatcherFetcherFactory>(),
			sp.GetRequiredService<WatcherOutputValidator>(),
			sp.GetRequiredService<IWatcherDispatcher>(),
			sp.GetRequiredService<ILoggerFactory>(),
			sp.GetService<TimeProvider>()));
		services.TryAddSingleton<IWatcherSubscriptionSink>(sp => sp.GetRequiredService<WatcherManager>());
		services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<WatcherManager>());

		return services;
	}
}
