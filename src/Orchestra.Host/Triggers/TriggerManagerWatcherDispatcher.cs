using Microsoft.Extensions.DependencyInjection;
using Orchestra.Watchers.Runtime;

namespace Orchestra.Host.Triggers;

/// <summary>
/// Routes watcher dispatches through <see cref="TriggerManager.DispatchWatcherItemAsync"/> so a
/// watcher-launched run is indistinguishable from any other trigger-launched run: it counts on the
/// trigger's history, fires the execution callbacks that refresh the dashboard, and honours the
/// trigger's <c>inputHandlerPrompt</c>. Resolves the trigger manager lazily because the manager
/// depends on the watcher runtime (via <see cref="TriggerManager.WatcherSink"/>) and vice versa.
/// </summary>
internal sealed class TriggerManagerWatcherDispatcher : IWatcherDispatcher
{
	private readonly IServiceProvider _services;
	private TriggerManager? _triggerManager;

	public TriggerManagerWatcherDispatcher(IServiceProvider services)
	{
		_services = services ?? throw new ArgumentNullException(nameof(services));
	}

	public Task<string?> DispatchAsync(WatcherDispatch dispatch, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(dispatch);
		cancellationToken.ThrowIfCancellationRequested();

		_triggerManager ??= _services.GetRequiredService<TriggerManager>();
		return _triggerManager.DispatchWatcherItemAsync(dispatch.Subscription.TriggerId, dispatch.Parameters);
	}
}
