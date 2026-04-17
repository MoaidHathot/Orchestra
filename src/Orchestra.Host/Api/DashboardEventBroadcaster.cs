using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Orchestra.Host.Api;

/// <summary>
/// Broadcasts dashboard-wide events (profile activation changes, execution lifecycle, etc.) to
/// all connected SSE subscribers. Unlike <see cref="SseReporter"/> — which is per-execution —
/// this is a singleton that lives for the lifetime of the host and fans out small "something
/// changed, go re-fetch" notifications to the Portal so it doesn't have to poll aggressively.
///
/// Memory-bounded: each subscriber uses a bounded channel (256 capacity, DropOldest) and the
/// total number of subscribers is capped (<see cref="MaxSubscribers"/>). No event history is
/// retained — events are "hints to refresh", not authoritative state, so late joiners simply
/// do a full refresh on connect.
/// </summary>
public sealed partial class DashboardEventBroadcaster : IDisposable
{
	private static readonly JsonSerializerOptions s_jsonOptions = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
	};

	/// <summary>Maximum number of concurrent Portal subscribers.</summary>
	public const int MaxSubscribers = 50;

	/// <summary>Per-subscriber channel capacity (events coalesced via DropOldest).</summary>
	public const int MaxChannelCapacity = 256;

	private readonly ILogger<DashboardEventBroadcaster> _logger;
	private readonly Lock _lock = new();
	private readonly List<Channel<SseEvent>> _subscribers = [];
	private bool _disposed;

	public DashboardEventBroadcaster(ILogger<DashboardEventBroadcaster> logger)
	{
		_logger = logger;
	}

	/// <summary>
	/// Current number of active subscribers.
	/// </summary>
	public int SubscriberCount
	{
		get
		{
			lock (_lock) return _subscribers.Count;
		}
	}

	/// <summary>
	/// Subscribes a new client. Returns null if the max subscriber cap is reached.
	/// </summary>
	public ChannelReader<SseEvent>? Subscribe()
	{
		var channel = Channel.CreateBounded<SseEvent>(
			new BoundedChannelOptions(MaxChannelCapacity)
			{
				SingleReader = true,
				SingleWriter = false,
				FullMode = BoundedChannelFullMode.DropOldest,
			});

		lock (_lock)
		{
			if (_disposed)
			{
				channel.Writer.TryComplete();
				return null;
			}

			if (_subscribers.Count >= MaxSubscribers)
			{
				LogSubscriberLimitReached(MaxSubscribers);
				channel.Writer.TryComplete();
				return null;
			}

			_subscribers.Add(channel);
			LogSubscriberAdded(_subscribers.Count);
			return channel.Reader;
		}
	}

	/// <summary>
	/// Unsubscribes a client (e.g., when the Portal disconnects).
	/// </summary>
	public void Unsubscribe(ChannelReader<SseEvent>? reader)
	{
		if (reader is null) return;

		lock (_lock)
		{
			for (var i = _subscribers.Count - 1; i >= 0; i--)
			{
				if (_subscribers[i].Reader == reader)
				{
					_subscribers[i].Writer.TryComplete();
					_subscribers.RemoveAt(i);
					LogSubscriberRemoved(_subscribers.Count);
					break;
				}
			}
		}
	}

	/// <summary>
	/// Broadcasts that the effective active profile set changed. The Portal should refresh
	/// /api/profiles (for the dropdown) and /api/orchestrations (for enabled/disabled state).
	/// </summary>
	public void BroadcastProfileActiveSetChanged(
		IReadOnlyCollection<string> activatedOrchestrationIds,
		IReadOnlyCollection<string> deactivatedOrchestrationIds,
		string trigger)
	{
		Publish("profile-active-set-changed", new
		{
			activatedOrchestrationIds,
			deactivatedOrchestrationIds,
			trigger,
		});
	}

	/// <summary>
	/// Broadcasts that an orchestration execution started. The Portal should refresh
	/// /api/active.
	/// </summary>
	public void BroadcastExecutionStarted(string executionId, string orchestrationId, string orchestrationName, string triggeredBy)
	{
		Publish("execution-started", new
		{
			executionId,
			orchestrationId,
			orchestrationName,
			triggeredBy,
		});
	}

	/// <summary>
	/// Broadcasts that an orchestration execution completed (in any terminal state). The Portal
	/// should refresh /api/active and /api/history.
	/// </summary>
	public void BroadcastExecutionCompleted(string executionId, string orchestrationId, string orchestrationName, string status)
	{
		Publish("execution-completed", new
		{
			executionId,
			orchestrationId,
			orchestrationName,
			status,
		});
	}

	/// <summary>
	/// Broadcasts that the profile list changed (profile created, updated, or deleted).
	/// The Portal should refresh /api/profiles.
	/// Unlike <see cref="BroadcastProfileActiveSetChanged"/> which only fires when the effective
	/// active orchestration set changes, this fires whenever the profile list itself is modified
	/// (e.g., a new inactive profile was added via file sync).
	/// </summary>
	public void BroadcastProfilesChanged(string reason)
	{
		Publish("profiles-changed", new { reason });
	}

	/// <summary>
	/// Broadcasts a heartbeat to keep SSE connections alive through proxies/load balancers.
	/// </summary>
	public void SendHeartbeat()
	{
		var evt = new SseEvent("heartbeat", "{}");
		lock (_lock)
		{
			if (_disposed) return;
			foreach (var channel in _subscribers)
			{
				channel.Writer.TryWrite(evt);
			}
		}
	}

	private void Publish(string eventType, object data)
	{
		var json = JsonSerializer.Serialize(data, s_jsonOptions);
		var evt = new SseEvent(eventType, json);

		lock (_lock)
		{
			if (_disposed) return;
			foreach (var channel in _subscribers)
			{
				channel.Writer.TryWrite(evt);
			}
		}

		LogEventPublished(eventType, _subscribers.Count);
	}

	public void Dispose()
	{
		lock (_lock)
		{
			if (_disposed) return;
			_disposed = true;

			foreach (var channel in _subscribers)
			{
				channel.Writer.TryComplete();
			}
			_subscribers.Clear();
		}
	}

	// ── Logging (source-generated) ──

	[LoggerMessage(Level = LogLevel.Debug, Message = "Dashboard event broadcaster: subscriber added (count={SubscriberCount})")]
	private partial void LogSubscriberAdded(int subscriberCount);

	[LoggerMessage(Level = LogLevel.Debug, Message = "Dashboard event broadcaster: subscriber removed (count={SubscriberCount})")]
	private partial void LogSubscriberRemoved(int subscriberCount);

	[LoggerMessage(Level = LogLevel.Warning, Message = "Dashboard event broadcaster: subscriber limit reached ({MaxSubscribers}); new subscription rejected")]
	private partial void LogSubscriberLimitReached(int maxSubscribers);

	[LoggerMessage(Level = LogLevel.Debug, Message = "Dashboard event broadcaster: published '{EventType}' to {SubscriberCount} subscriber(s)")]
	private partial void LogEventPublished(string eventType, int subscriberCount);
}
