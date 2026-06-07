using Microsoft.Extensions.Logging;
using Orchestra.Engine;

namespace Orchestra.Copilot;

internal sealed partial class CopilotClientPool : ICopilotClientPool, IAsyncDisposable
{
	private static int s_poolCounter;

	private readonly int _poolId;
	private readonly int _minInstances;
	private readonly int _maxInstances;
	private readonly int _maxSessionsPerInstance;
	private readonly TimeSpan _idleTimeout;
	private readonly ICopilotClientFactory _clientFactory;
	private readonly ILoggerFactory _loggerFactory;
	#pragma warning disable IDE0052 // LoggerMessage source generation keeps the logger field intentionally unused in source.
	private readonly ILogger<CopilotClientPool> _logger;
	#pragma warning restore IDE0052
	private readonly SemaphoreSlim _gate = new(1, 1);
	private readonly SemaphoreSlim _availability = new(0);
	private readonly List<Worker> _workers = [];
	private readonly Timer? _idleTimer;
	private int _nextWorkerId;
	private int _workerCount;
	private int _activeSessionCount;
	private int _totalSwapsTriggered;
	private int _workersSwappedOut;
	private int _disposed;
	private int _shrinkRunning;
	private IReadOnlyList<AvailableModelInfo>? _cachedAvailableModels;

	public CopilotClientPool(
		AgentPoolConfig? requestedConfig,
		CopilotAgentPoolOptions defaults,
		ICopilotClientFactory clientFactory,
		ILoggerFactory loggerFactory)
	{
		_poolId = Interlocked.Increment(ref s_poolCounter);
		_clientFactory = clientFactory;
		_loggerFactory = loggerFactory;
		_logger = loggerFactory.CreateLogger<CopilotClientPool>();

		_maxInstances = Math.Max(1, requestedConfig?.MaxInstances ?? defaults.DefaultMaxInstancesPerRun);
		_maxSessionsPerInstance = Math.Max(1, requestedConfig?.MaxSessionsPerInstance ?? defaults.DefaultMaxSessionsPerInstance);
		_minInstances = Math.Clamp(requestedConfig?.MinInstances ?? defaults.DefaultMinInstances, 0, _maxInstances);
		var idleTimeoutSeconds = Math.Max(0, requestedConfig?.IdleTimeoutSeconds ?? defaults.DefaultIdleTimeoutSeconds);
		_idleTimeout = TimeSpan.FromSeconds(idleTimeoutSeconds);

		if (_idleTimeout > TimeSpan.Zero)
		{
			var period = TimeSpan.FromSeconds(Math.Clamp(_idleTimeout.TotalSeconds / 2, 1, 30));
			_idleTimer = new Timer(static state => _ = ((CopilotClientPool)state!).ShrinkIdleWorkersAsync(), this, period, period);
		}

		LogPoolCreated(_poolId, _minInstances, _maxInstances, _maxSessionsPerInstance, idleTimeoutSeconds);
	}

	public string Diagnostic => $"pool#{_poolId}:workers={Volatile.Read(ref _nextWorkerId)}";

	public CopilotClientPoolSnapshot GetSnapshot()
	{
		return Volatile.Read(ref _disposed) == 1
			? new CopilotClientPoolSnapshot(0, 0, Volatile.Read(ref _totalSwapsTriggered), Volatile.Read(ref _workersSwappedOut))
			: new CopilotClientPoolSnapshot(
				Volatile.Read(ref _workerCount),
				Volatile.Read(ref _activeSessionCount),
				Volatile.Read(ref _totalSwapsTriggered),
				Volatile.Read(ref _workersSwappedOut));
	}

	/// <summary>
	/// Records that a CLI swap was triggered for one of this pool's sessions. Used by
	/// <see cref="CopilotAgent"/> for diagnostic counters surfaced via
	/// <see cref="GetSnapshot"/>.
	/// </summary>
	public void RecordSwapTriggered() => Interlocked.Increment(ref _totalSwapsTriggered);

	/// <summary>
	/// Records that a worker was removed from rotation because a session it owned faulted
	/// the CLI. Counter is incremented by <see cref="PruneWorkersLockedAsync"/> when it
	/// removes an unhealthy worker.
	/// </summary>
	private void RecordWorkerSwappedOut() => Interlocked.Increment(ref _workersSwappedOut);

	public async Task PrewarmAsync(CancellationToken cancellationToken)
	{
		if (_minInstances == 0)
			return;

		await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			ThrowIfDisposed();
			while (_workers.Count < _minInstances)
			{
				_ = await CreateWorkerLockedAsync(cancellationToken).ConfigureAwait(false);
			}
		}
		finally
		{
			_gate.Release();
		}
	}

	public async ValueTask<ICopilotClientLease> AcquireAsync(CancellationToken cancellationToken)
	{
		while (true)
		{
			await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
			try
			{
				ThrowIfDisposed();
				await PruneWorkersLockedAsync(forceIdle: false).ConfigureAwait(false);

				var worker = FindAvailableWorkerLocked();
				if (worker is null && _workers.Count < _maxInstances)
				{
					worker = await CreateWorkerLockedAsync(cancellationToken).ConfigureAwait(false);
				}

				if (worker is not null)
				{
					worker.ActiveSessions++;
					Interlocked.Increment(ref _activeSessionCount);
					LogLeaseAcquired(_poolId, worker.Id, worker.ActiveSessions, _workers.Count);
					return new Lease(this, worker);
				}

				LogPoolAtCapacity(_poolId, _workers.Count, _maxInstances, _maxSessionsPerInstance);
			}
			finally
			{
				_gate.Release();
			}

			await _availability.WaitAsync(cancellationToken).ConfigureAwait(false);
		}
	}

	private Worker? FindAvailableWorkerLocked()
	{
		return _workers
			.Where(w => !w.FaultBroker.IsClientUnhealthy && w.ActiveSessions < _maxSessionsPerInstance)
			.OrderBy(w => w.ActiveSessions)
			.ThenBy(w => w.Id)
			.FirstOrDefault();
	}

	private async Task<Worker> CreateWorkerLockedAsync(CancellationToken cancellationToken)
	{
		var workerId = Interlocked.Increment(ref _nextWorkerId);
		var client = _clientFactory.CreateClient();
		var sw = System.Diagnostics.Stopwatch.StartNew();
		LogWorkerStarting(_poolId, workerId);

		try
		{
			await client.StartAsync(cancellationToken).ConfigureAwait(false);
		}
		catch (Exception ex)
		{
			LogWorkerStartFailed(ex, _poolId, workerId, sw.ElapsedMilliseconds);
			try { await client.DisposeAsync().ConfigureAwait(false); } catch { }
			throw;
		}

		var broker = new SessionFaultBroker(
			workerId,
			probe: ct => ProbeClientHealthAsync(client, _poolId, workerId, ct),
			logger: _loggerFactory.CreateLogger<SessionFaultBroker>());
		var worker = new Worker(workerId, client, broker);
		_workers.Add(worker);
		Volatile.Write(ref _workerCount, _workers.Count);
		LogWorkerStarted(_poolId, workerId, sw.ElapsedMilliseconds, client.DiagnosticHash, _workers.Count);
		return worker;
	}

	private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

	private async Task<ProbeResult> ProbeClientHealthAsync(ICopilotClient client, int poolId, int workerId, CancellationToken cancellationToken)
	{
		using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		probeCts.CancelAfter(ProbeTimeout);

		// SDK 1.0.0 removed the public ConnectionState/State surface (PR #1170 replaced
		// StreamJsonRpc with a custom transport whose internal state isn't exposed).
		// Health is now derived strictly from the PingAsync round-trip: if the runtime
		// answers within ProbeTimeout the client is healthy; any throw or timeout marks
		// it unhealthy so the broker can fault its in-flight sibling sessions.
		LogProbeAttempt(poolId, workerId, "(state api removed in SDK 1.0.0; using ping outcome)");

		try
		{
			var pingSw = System.Diagnostics.Stopwatch.StartNew();
			await client.PingAsync("orchestra-health-probe", probeCts.Token).ConfigureAwait(false);
			pingSw.Stop();

			return new ProbeResult(true, $"ping ok in {pingSw.ElapsedMilliseconds}ms");
		}
		catch (OperationCanceledException) when (probeCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
		{
			return new ProbeResult(false, $"ping timed out after {ProbeTimeout.TotalSeconds}s");
		}
		catch (Exception ex)
		{
			return new ProbeResult(false, $"ping threw {ex.GetType().Name}: {ex.Message}");
		}
	}

	private async Task ReleaseAsync(Worker worker)
	{
		if (Volatile.Read(ref _disposed) == 1)
			return;

		try
		{
			await _gate.WaitAsync().ConfigureAwait(false);
		}
		catch (ObjectDisposedException)
		{
			return;
		}

		try
		{
			if (Volatile.Read(ref _disposed) == 1)
				return;

			if (worker.ActiveSessions > 0)
			{
				worker.ActiveSessions--;
				Interlocked.Decrement(ref _activeSessionCount);
			}

			worker.LastUsedAt = DateTimeOffset.UtcNow;
			LogLeaseReleased(_poolId, worker.Id, worker.ActiveSessions);
			await PruneWorkersLockedAsync(forceIdle: false).ConfigureAwait(false);
		}
		finally
		{
			_gate.Release();
			SafeSignalAvailability();
		}
	}

	private async Task ShrinkIdleWorkersAsync()
	{
		if (Volatile.Read(ref _disposed) == 1)
			return;

		if (Interlocked.Exchange(ref _shrinkRunning, 1) == 1)
			return;

		try
		{
			await _gate.WaitAsync().ConfigureAwait(false);
			try
			{
				if (Volatile.Read(ref _disposed) == 0)
				{
					await PruneWorkersLockedAsync(forceIdle: true).ConfigureAwait(false);
				}
			}
			finally
			{
				_gate.Release();
			}
		}
		finally
		{
			Interlocked.Exchange(ref _shrinkRunning, 0);
		}
	}

	private async Task PruneWorkersLockedAsync(bool forceIdle)
	{
		if (_workers.Count == 0)
			return;

		var now = DateTimeOffset.UtcNow;
		var candidates = _workers
			.Where(w => w.ActiveSessions == 0)
			.Where(w => w.FaultBroker.IsClientUnhealthy
				|| (forceIdle && _idleTimeout > TimeSpan.Zero && now - w.LastUsedAt >= _idleTimeout))
			.OrderByDescending(w => w.FaultBroker.IsClientUnhealthy)
			.ThenBy(w => w.LastUsedAt)
			.ToList();

		foreach (var worker in candidates)
		{
			if (!worker.FaultBroker.IsClientUnhealthy && _workers.Count <= _minInstances)
				break;

			var wasUnhealthy = worker.FaultBroker.IsClientUnhealthy;
			_workers.Remove(worker);
			Volatile.Write(ref _workerCount, _workers.Count);
			LogWorkerStopping(_poolId, worker.Id, wasUnhealthy ? "unhealthy" : "idle", _workers.Count);
			if (wasUnhealthy)
			{
				RecordWorkerSwappedOut();
			}
			await StopWorkerAsync(worker).ConfigureAwait(false);
		}
	}

	private async Task StopWorkerAsync(Worker worker)
	{
		try { await worker.Client.StopAsync().ConfigureAwait(false); }
		catch (Exception ex) { LogWorkerStopError(ex, _poolId, worker.Id); }

		try { await worker.Client.DisposeAsync().ConfigureAwait(false); }
		catch (Exception ex) { LogWorkerDisposeError(ex, _poolId, worker.Id); }
	}

	private void SafeSignalAvailability()
	{
		try { _availability.Release(); }
		catch (SemaphoreFullException) { }
		catch (ObjectDisposedException) { }
	}

	private void ThrowIfDisposed()
	{
		ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
	}

	public async ValueTask DisposeAsync()
	{
		if (Interlocked.Exchange(ref _disposed, 1) == 1)
			return;

		if (_idleTimer is not null)
		{
			await _idleTimer.DisposeAsync().ConfigureAwait(false);
		}

		await _gate.WaitAsync().ConfigureAwait(false);
		try
		{
			foreach (var worker in _workers.ToArray())
			{
				_workers.Remove(worker);
				Volatile.Write(ref _workerCount, _workers.Count);
				await StopWorkerAsync(worker).ConfigureAwait(false);
			}
			Volatile.Write(ref _activeSessionCount, 0);
		}
		finally
		{
			_gate.Release();
			_gate.Dispose();
			_availability.Dispose();
		}

		LogPoolDisposed(_poolId);
	}

	private sealed class Worker
	{
		public Worker(int id, ICopilotClient client, SessionFaultBroker faultBroker)
		{
			Id = id;
			Client = client;
			FaultBroker = faultBroker;
			LastUsedAt = DateTimeOffset.UtcNow;
		}

		public int Id { get; }
		public ICopilotClient Client { get; }
		public SessionFaultBroker FaultBroker { get; }
		public int ActiveSessions { get; set; }
		public DateTimeOffset LastUsedAt { get; set; }
	}

	private sealed class Lease : ICopilotClientLease
	{
		private readonly CopilotClientPool _pool;
		private readonly Worker _worker;
		private int _released;

		public Lease(CopilotClientPool pool, Worker worker)
		{
			_pool = pool;
			_worker = worker;
		}

		public ICopilotClient Client => _worker.Client;
		public ISessionFaultBroker? FaultBroker => _worker.FaultBroker;
		public IReadOnlyList<AvailableModelInfo>? CachedAvailableModels => _pool._cachedAvailableModels;
		public void SetCachedAvailableModels(IReadOnlyList<AvailableModelInfo> models) => _pool._cachedAvailableModels = models;

		public async ValueTask DisposeAsync()
		{
			if (Interlocked.Exchange(ref _released, 1) == 0)
			{
				await _pool.ReleaseAsync(_worker).ConfigureAwait(false);
			}
		}
	}

	[LoggerMessage(EventId = 300, Level = LogLevel.Information,
		Message = "CopilotPool#{PoolId}: created (minInstances={MinInstances}, maxInstances={MaxInstances}, maxSessionsPerInstance={MaxSessionsPerInstance}, idleTimeoutSeconds={IdleTimeoutSeconds})")]
	private partial void LogPoolCreated(int poolId, int minInstances, int maxInstances, int maxSessionsPerInstance, int idleTimeoutSeconds);

	[LoggerMessage(EventId = 301, Level = LogLevel.Information,
		Message = "CopilotPool#{PoolId}: starting worker#{WorkerId}")]
	private partial void LogWorkerStarting(int poolId, int workerId);

	[LoggerMessage(EventId = 302, Level = LogLevel.Information,
		Message = "CopilotPool#{PoolId}: started worker#{WorkerId} in {ElapsedMs}ms (clientHash={ClientHash}, workers={WorkerCount})")]
	private partial void LogWorkerStarted(int poolId, int workerId, long elapsedMs, int clientHash, int workerCount);

	[LoggerMessage(EventId = 303, Level = LogLevel.Error,
		Message = "CopilotPool#{PoolId}: failed to start worker#{WorkerId} after {ElapsedMs}ms")]
	private partial void LogWorkerStartFailed(Exception ex, int poolId, int workerId, long elapsedMs);

	[LoggerMessage(EventId = 304, Level = LogLevel.Debug,
		Message = "CopilotPool#{PoolId}: lease acquired on worker#{WorkerId} (activeSessions={ActiveSessions}, workers={WorkerCount})")]
	private partial void LogLeaseAcquired(int poolId, int workerId, int activeSessions, int workerCount);

	[LoggerMessage(EventId = 305, Level = LogLevel.Debug,
		Message = "CopilotPool#{PoolId}: lease released from worker#{WorkerId} (activeSessions={ActiveSessions})")]
	private partial void LogLeaseReleased(int poolId, int workerId, int activeSessions);

	[LoggerMessage(EventId = 306, Level = LogLevel.Debug,
		Message = "CopilotPool#{PoolId}: at capacity (workers={WorkerCount}/{MaxInstances}, maxSessionsPerInstance={MaxSessionsPerInstance}); waiting for a free worker")]
	private partial void LogPoolAtCapacity(int poolId, int workerCount, int maxInstances, int maxSessionsPerInstance);

	[LoggerMessage(EventId = 307, Level = LogLevel.Information,
		Message = "CopilotPool#{PoolId}: stopping worker#{WorkerId} ({Reason}; remainingWorkers={RemainingWorkers})")]
	private partial void LogWorkerStopping(int poolId, int workerId, string reason, int remainingWorkers);

	[LoggerMessage(EventId = 308, Level = LogLevel.Warning,
		Message = "CopilotPool#{PoolId}: error stopping worker#{WorkerId}")]
	private partial void LogWorkerStopError(Exception ex, int poolId, int workerId);

	[LoggerMessage(EventId = 309, Level = LogLevel.Warning,
		Message = "CopilotPool#{PoolId}: error disposing worker#{WorkerId}")]
	private partial void LogWorkerDisposeError(Exception ex, int poolId, int workerId);

	[LoggerMessage(EventId = 310, Level = LogLevel.Information,
		Message = "CopilotPool#{PoolId}: disposed")]
	private partial void LogPoolDisposed(int poolId);

	[LoggerMessage(EventId = 311, Level = LogLevel.Debug,
		Message = "CopilotPool#{PoolId}/worker#{WorkerId}: probing CLI client health (currentState={State})")]
	private partial void LogProbeAttempt(int poolId, int workerId, string state);
}
