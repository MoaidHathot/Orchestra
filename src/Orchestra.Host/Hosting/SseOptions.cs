namespace Orchestra.Host.Hosting;

/// <summary>
/// Options that govern the SSE event-streaming pipeline used by
/// <see cref="Orchestra.Host.Api.SseReporter"/> to push orchestration events to the
/// Portal and other attached clients.
///
/// <para>
/// All three limits below cap memory usage. They also bound how much state late-joining
/// clients can reconstruct from the event log alone. When a long-running orchestration
/// produces more events than <see cref="MaxAccumulatedEvents"/>, the oldest events are
/// silently overwritten in the replay buffer. Combined with the per-step snapshot the
/// reporter maintains separately, late attaches still see correct DAG state even when
/// events were evicted, but tuning these caps higher reduces the chance of losing
/// streaming detail (content/reasoning deltas) on reconnect.
/// </para>
///
/// <para>
/// Defaults are tuned for the typical case of a Portal user attached to a single run
/// at a time with reasonable network speed. Heavy reasoning/streaming workloads
/// (many sub-agents, very chatty prompts) may benefit from doubling
/// <see cref="MaxAccumulatedEvents"/> and <see cref="MaxChannelCapacity"/>.
/// </para>
/// </summary>
public class SseOptions
{
	/// <summary>
	/// Maximum number of events to keep in the per-execution circular replay buffer.
	/// When exceeded, the oldest events are silently overwritten. The reporter still
	/// maintains a separate per-step authoritative snapshot, so DAG node state remains
	/// correct on late attaches even when events were evicted; this cap only affects
	/// how much streaming/delta history a late joiner can replay.
	/// Default: 50000.
	/// </summary>
	public int MaxAccumulatedEvents { get; set; } = 50_000;

	/// <summary>
	/// Maximum number of events buffered per attached subscriber's outbound channel.
	/// When a slow client falls behind, the oldest events in its channel are dropped
	/// (FullMode = DropOldest) so the engine cannot be back-pressured by the UI.
	/// Default: 5000.
	/// </summary>
	public int MaxChannelCapacity { get; set; } = 5_000;

	/// <summary>
	/// Maximum number of concurrent SSE subscribers per execution. Additional
	/// attempts to subscribe past this limit still receive a replay but no future
	/// stream of live events; the connection then closes when the heartbeat task
	/// or request abort fires.
	/// Default: 50.
	/// </summary>
	public int MaxSubscribers { get; set; } = 50;

	/// <summary>
	/// How often a keepalive <c>heartbeat</c> SSE event is sent on each active
	/// stream to prevent intermediary proxies and idle TCP timeouts from silently
	/// closing the connection. Heartbeats are not retained in the replay buffer.
	/// Default: 20 seconds.
	/// </summary>
	public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(20);
}
