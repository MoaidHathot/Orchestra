using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Orchestra.Copilot;

/// <summary>
/// Coordinates fault propagation for sessions sharing a single Copilot CLI client.
/// When one session fails the broker probes the underlying client and, if the client
/// is unhealthy, faults all OTHER in-flight sessions so they fast-fail instead of
/// sitting silently until their per-step timeout.
///
/// One broker per <see cref="CopilotClient"/> (i.e. per orchestration RunScope).
/// </summary>
internal interface ISessionFaultBroker
{
	/// <summary>
	/// Once true, the underlying CLI client has been declared dead and will never recover
	/// for the remainder of this run scope. New session attempts on this client should
	/// fail fast instead of issuing JSON-RPC calls that we already know will fail.
	/// Latches one-way (false → true).
	/// </summary>
	bool IsClientUnhealthy { get; }

	/// <summary>
	/// Human-readable reason the client was declared unhealthy (probe details), or null
	/// if the latch has not been set. Stable to read once <see cref="IsClientUnhealthy"/>
	/// is true.
	/// </summary>
	string? UnhealthyReason { get; }

	/// <summary>
	/// Session id of the first failure that triggered the unhealthy decision, or null
	/// if the latch has not been set.
	/// </summary>
	string? UnhealthyTriggeringSessionId { get; }

	/// <summary>
	/// Original failure reason of the first failure that triggered the unhealthy decision,
	/// or null if the latch has not been set.
	/// </summary>
	string? UnhealthyTriggeringFailureReason { get; }

	/// <summary>
	/// Registers an in-flight session with a fault callback. The returned IDisposable
	/// MUST be disposed when the session completes (success or failure) to avoid leaks
	/// and to prevent stale callbacks from being invoked.
	/// </summary>
	IDisposable RegisterSession(string sessionId, Action<Exception> onFault);

	/// <summary>
	/// Called by a session when it observes a failure. The broker probes the underlying
	/// client and, if unhealthy, faults all other registered sessions on this client.
	/// Returns true if the CLI transport responded (do not fault siblings);
	/// returns false if the client is unhealthy and siblings have been faulted.
	///
	/// Unhealthy decisions latch for the broker lifetime. Healthy probe results are
	/// not cached because they only prove the CLI transport responded at that moment,
	/// not that later sessions or upstream model calls will work.
	/// </summary>
	Task<bool> ProbeAndMaybeFaultSiblingsAsync(
		string failedSessionId,
		string failureReason,
		CancellationToken cancellationToken);
}

/// <summary>
/// Default broker implementation. The probe is injected as a delegate so the broker
/// is decoupled from the sealed <see cref="GitHub.Copilot.SDK.CopilotClient"/> type
/// and can be unit-tested with a fake probe.
/// </summary>
internal sealed partial class SessionFaultBroker : ISessionFaultBroker
{
	private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Action<Exception>> _registry = new();
	private readonly Func<CancellationToken, Task<ProbeResult>> _probe;
	private readonly ILogger<SessionFaultBroker> _logger;
	private readonly int _scopeId;
	private readonly SemaphoreSlim _probeLock = new(1, 1);

	// Latch state — set ONCE when probe declares the client unhealthy. One-way (false → true).
	private volatile bool _isClientUnhealthy;
	private string? _unhealthyReason;
	private string? _unhealthyTriggeringSessionId;
	private string? _unhealthyTriggeringFailureReason;

	public bool IsClientUnhealthy => _isClientUnhealthy;
	public string? UnhealthyReason => _unhealthyReason;
	public string? UnhealthyTriggeringSessionId => _unhealthyTriggeringSessionId;
	public string? UnhealthyTriggeringFailureReason => _unhealthyTriggeringFailureReason;

	public SessionFaultBroker(
		int scopeId,
		Func<CancellationToken, Task<ProbeResult>> probe,
		ILogger<SessionFaultBroker>? logger = null)
	{
		_scopeId = scopeId;
		_probe = probe;
		_logger = logger ?? NullLogger<SessionFaultBroker>.Instance;
	}

	public IDisposable RegisterSession(string sessionId, Action<Exception> onFault)
	{
		_registry[sessionId] = onFault;
		LogSessionRegistered(_scopeId, sessionId, _registry.Count);
		return new Registration(this, sessionId);
	}

	public async Task<bool> ProbeAndMaybeFaultSiblingsAsync(
		string failedSessionId,
		string failureReason,
		CancellationToken cancellationToken)
	{
		// Fast path: unhealthy is a one-way latch for this client.
		if (IsClientUnhealthy)
		{
			LogProbeCachedUnhealthy(_scopeId, failedSessionId);
			return false;
		}

		await _probeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			if (IsClientUnhealthy)
			{
				LogProbeCachedUnhealthy(_scopeId, failedSessionId);
				return false;
			}

			LogProbeStarting(_scopeId, failedSessionId, failureReason, _registry.Count);

			ProbeResult probeResult;
			var sw = System.Diagnostics.Stopwatch.StartNew();
			try
			{
				probeResult = await _probe(cancellationToken).ConfigureAwait(false);
			}
			catch (Exception ex)
			{
				probeResult = new ProbeResult(false, $"probe threw: {ex.GetType().Name}: {ex.Message}");
			}

			if (probeResult.Healthy)
			{
				LogProbeHealthy(_scopeId, failedSessionId, sw.ElapsedMilliseconds, probeResult.Details ?? "(no details)");
				return true;
			}

			var siblings = _registry
				.Where(kv => kv.Key != failedSessionId)
				.ToArray();

			LogProbeUnhealthy(_scopeId, failedSessionId, sw.ElapsedMilliseconds, probeResult.Details ?? "(no details)", siblings.Length);

			// Latch unhealthy state so subsequent session attempts on this client fail fast
			// instead of issuing JSON-RPC calls we already know will fail.
			_unhealthyReason = probeResult.Details;
			_unhealthyTriggeringSessionId = failedSessionId;
			_unhealthyTriggeringFailureReason = failureReason;
			_isClientUnhealthy = true;

			// Fault all other registered sessions.
			foreach (var (siblingId, callback) in siblings)
			{
				try
				{
					var siblingException = new CopilotClientUnhealthyException(
						triggeringSessionId: failedSessionId,
						triggeringFailureReason: failureReason,
						probeDetails: probeResult.Details,
						message: $"Copilot CLI client is unhealthy. Sibling session '{failedSessionId}' failed " +
								 $"with: {failureReason}. Health probe result: {probeResult.Details ?? "no details"}. " +
								 $"This session ('{siblingId}') is being faulted to prevent silent timeout.");
					callback(siblingException);
					LogSiblingFaulted(_scopeId, siblingId, failedSessionId);
				}
				catch (Exception ex)
				{
					LogSiblingFaultCallbackFailed(ex, _scopeId, siblingId);
				}
			}

			return false;
		}
		finally
		{
			_probeLock.Release();
		}
	}

	private void Unregister(string sessionId)
	{
		if (_registry.TryRemove(sessionId, out _))
		{
			LogSessionUnregistered(_scopeId, sessionId, _registry.Count);
		}
	}

	private sealed class Registration : IDisposable
	{
		private readonly SessionFaultBroker _broker;
		private readonly string _sessionId;
		private int _disposed;

		public Registration(SessionFaultBroker broker, string sessionId)
		{
			_broker = broker;
			_sessionId = sessionId;
		}

		public void Dispose()
		{
			if (Interlocked.Exchange(ref _disposed, 1) == 0)
			{
				_broker.Unregister(_sessionId);
			}
		}
	}

	#region Source-Generated Logging

	[LoggerMessage(EventId = 200, Level = LogLevel.Debug,
		Message = "FaultBroker#{ScopeId}: session '{SessionId}' registered (in-flight={InFlightCount})")]
	private partial void LogSessionRegistered(int scopeId, string sessionId, int inFlightCount);

	[LoggerMessage(EventId = 201, Level = LogLevel.Debug,
		Message = "FaultBroker#{ScopeId}: session '{SessionId}' unregistered (in-flight={InFlightCount})")]
	private partial void LogSessionUnregistered(int scopeId, string sessionId, int inFlightCount);

	[LoggerMessage(EventId = 202, Level = LogLevel.Warning,
		Message = "FaultBroker#{ScopeId}: probing CLI health after session '{SessionId}' failed (reason={FailureReason}, in-flight={InFlightCount})")]
	private partial void LogProbeStarting(int scopeId, string sessionId, string failureReason, int inFlightCount);

	[LoggerMessage(EventId = 203, Level = LogLevel.Information,
		Message = "FaultBroker#{ScopeId}: CLI transport responded after session '{SessionId}' failure (probeMs={ProbeMs}, details={Details}). Not faulting sibling sessions.")]
	private partial void LogProbeHealthy(int scopeId, string sessionId, long probeMs, string details);

	[LoggerMessage(EventId = 204, Level = LogLevel.Error,
		Message = "FaultBroker#{ScopeId}: CLI UNHEALTHY after session '{SessionId}' failure (probeMs={ProbeMs}, details={Details}). Faulting {SiblingCount} sibling session(s) to prevent silent timeout.")]
	private partial void LogProbeUnhealthy(int scopeId, string sessionId, long probeMs, string details, int siblingCount);

	[LoggerMessage(EventId = 205, Level = LogLevel.Error,
		Message = "FaultBroker#{ScopeId}: faulted sibling session '{SiblingId}' (triggered by '{TriggerId}')")]
	private partial void LogSiblingFaulted(int scopeId, string siblingId, string triggerId);

	[LoggerMessage(EventId = 206, Level = LogLevel.Warning,
		Message = "FaultBroker#{ScopeId}: failed to invoke fault callback for sibling '{SiblingId}'")]
	private partial void LogSiblingFaultCallbackFailed(Exception ex, int scopeId, string siblingId);

	[LoggerMessage(EventId = 207, Level = LogLevel.Debug,
		Message = "FaultBroker#{ScopeId}: client already latched unhealthy; skipping probe for session '{SessionId}'")]
	private partial void LogProbeCachedUnhealthy(int scopeId, string sessionId);

	#endregion
}

/// <summary>
/// Result of a CLI health probe.
/// </summary>
/// <param name="Healthy">True when the CLI client is responsive and in a usable state.</param>
/// <param name="Details">Human-readable description (state, ping outcome) for logging.</param>
internal readonly record struct ProbeResult(bool Healthy, string? Details);
