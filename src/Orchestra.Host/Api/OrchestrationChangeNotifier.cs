using Microsoft.Extensions.Logging;
using Orchestra.Host.Registry;
using Orchestra.Host.Triggers;

namespace Orchestra.Host.Api;

/// <summary>
/// Bridges <see cref="OrchestrationRegistry.Changed"/> and <see cref="TriggerManager.TriggersChanged"/>
/// onto the dashboard SSE stream as a single <c>orchestrations-changed</c> event, so the Portal
/// can refresh its orchestration list when something actually changed instead of polling it.
/// </summary>
/// <remarks>
/// Both sources can fire in bursts (a directory sync registers many files; enabling a profile
/// flips many triggers). Notifications are coalesced over a short window and published once,
/// off the mutating thread, so registry mutation never blocks on SSE fan-out.
/// </remarks>
public sealed partial class OrchestrationChangeNotifier : IDisposable
{
	/// <summary>How long to wait for further changes before publishing one event.</summary>
	public static readonly TimeSpan CoalesceWindow = TimeSpan.FromMilliseconds(250);

	private readonly OrchestrationRegistry _registry;
	private readonly TriggerManager _triggerManager;
	private readonly DashboardEventBroadcaster _broadcaster;
	private readonly ILogger<OrchestrationChangeNotifier> _logger;
	private readonly Lock _gate = new();
	private readonly TimeSpan _window;
	private readonly TimeProvider _timeProvider;
	private ITimer? _timer;
	private string? _pendingReason;
	private bool _started;
	private bool _disposed;

	public OrchestrationChangeNotifier(
		OrchestrationRegistry registry,
		TriggerManager triggerManager,
		DashboardEventBroadcaster broadcaster,
		ILogger<OrchestrationChangeNotifier> logger)
		: this(registry, triggerManager, broadcaster, logger, CoalesceWindow)
	{
	}

	internal OrchestrationChangeNotifier(
		OrchestrationRegistry registry,
		TriggerManager triggerManager,
		DashboardEventBroadcaster broadcaster,
		ILogger<OrchestrationChangeNotifier> logger,
		TimeSpan coalesceWindow,
		TimeProvider? timeProvider = null)
	{
		_registry = registry;
		_triggerManager = triggerManager;
		_broadcaster = broadcaster;
		_logger = logger;
		_window = coalesceWindow;
		_timeProvider = timeProvider ?? TimeProvider.System;
	}

	/// <summary>Subscribes to both sources. Idempotent.</summary>
	public void Start()
	{
		lock (_gate)
		{
			if (_started || _disposed)
				return;
			_started = true;
			_registry.Changed += OnRegistryChanged;
			_triggerManager.TriggersChanged += OnTriggersChanged;
		}
	}

	private void OnRegistryChanged(string reason) => Schedule($"registry:{reason}");

	private void OnTriggersChanged(string reason) => Schedule($"trigger:{reason}");

	private void Schedule(string reason)
	{
		lock (_gate)
		{
			if (_disposed)
				return;

			// Keep the first reason of a burst; it names what started the change.
			_pendingReason ??= reason;
			if (_timer is null)
				_timer = _timeProvider.CreateTimer(_ => Flush(), null, _window, Timeout.InfiniteTimeSpan);
			else
				_timer.Change(_window, Timeout.InfiniteTimeSpan);
		}
	}

	private void Flush()
	{
		string? reason;
		lock (_gate)
		{
			reason = _pendingReason;
			_pendingReason = null;
			if (reason is null || _disposed)
				return;
		}

		try
		{
			_broadcaster.BroadcastOrchestrationsChanged(reason);
			LogPublished(reason);
		}
		catch (Exception ex)
		{
			LogPublishFailed(reason, ex);
		}
	}

	public void Dispose()
	{
		lock (_gate)
		{
			if (_disposed)
				return;
			_disposed = true;
			_timer?.Dispose();
			_timer = null;
		}

		if (_started)
		{
			_registry.Changed -= OnRegistryChanged;
			_triggerManager.TriggersChanged -= OnTriggersChanged;
		}
	}

	[LoggerMessage(Level = LogLevel.Debug, Message = "Published orchestrations-changed ({Reason})")]
	private partial void LogPublished(string reason);

	[LoggerMessage(Level = LogLevel.Warning, Message = "Failed to publish orchestrations-changed ({Reason})")]
	private partial void LogPublishFailed(string reason, Exception ex);
}
