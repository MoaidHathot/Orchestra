using Microsoft.Extensions.Logging;
using Orchestra.Engine;

namespace Orchestra.OpenCode;

/// <summary>Point-in-time view of an <see cref="OpenCodeServerPool"/> for status endpoints.</summary>
public sealed record OpenCodeServerPoolSnapshot(int Instances, int ActiveSessions, int MaxInstances);

internal interface IOpenCodeServerLease : IAsyncDisposable
{
	IOpenCodeClient Client { get; }
	string BaseUrl { get; }

	/// <summary>
	/// The loopback MCP URL exposing this worker's engine tools, or null when the engine-tool
	/// bridge is disabled. The agent registers this with the OpenCode instance so the model can
	/// call <c>orchestra_*</c> tools.
	/// </summary>
	string? EngineToolMcpUrl { get; }

	/// <summary>
	/// Per-instance engine-tool context holder. The agent sets this to the running step's
	/// <see cref="EngineToolContext"/> before prompting so the loopback MCP bridge routes
	/// tool calls to the correct step; cleared on lease release.
	/// </summary>
	EngineToolContextHolder ContextHolder { get; }
}

/// <summary>
/// A run-scoped pool of <c>opencode serve</c> workers. Each worker serves up to
/// <see cref="OpenCodeAgentPoolOptions.DefaultMaxSessionsPerInstance"/> concurrent leases;
/// the pool grows lazily to <see cref="OpenCodeAgentPoolOptions.DefaultMaxInstancesPerRun"/>.
/// Disposed when the run scope ends. Mirrors the role of <c>CopilotClientPool</c>.
/// </summary>
internal sealed partial class OpenCodeServerPool : IAsyncDisposable
{
	private readonly OpenCodeAgentPoolOptions _options;
	private readonly IOpenCodeClientFactory _clientFactory;
	private readonly ILoggerFactory _loggerFactory;
	private readonly ILogger<OpenCodeServerPool> _logger;
	private readonly int _maxInstances;
	private readonly int _maxSessionsPerInstance;
	private readonly int _minInstances;
	private readonly SemaphoreSlim _capacity;
	private readonly object _lock = new();
	private readonly List<Worker> _workers = [];
	private int _pendingSpawns;
	private int _dedicatedInstances;
	private bool _disposed;

	private static int s_poolCounter;
	private readonly int _poolId = Interlocked.Increment(ref s_poolCounter);

	public OpenCodeServerPool(
		AgentPoolConfig? agentPool,
		OpenCodeAgentPoolOptions options,
		IOpenCodeClientFactory clientFactory,
		ILoggerFactory loggerFactory)
	{
		_options = options;
		_clientFactory = clientFactory;
		_loggerFactory = loggerFactory;
		_logger = loggerFactory.CreateLogger<OpenCodeServerPool>();

		_minInstances = Math.Max(0, agentPool?.MinInstances ?? options.DefaultMinInstances);
		_maxInstances = Math.Max(1, agentPool?.MaxInstances ?? options.DefaultMaxInstancesPerRun);
		_maxSessionsPerInstance = Math.Max(1, agentPool?.MaxSessionsPerInstance ?? options.DefaultMaxSessionsPerInstance);
		_minInstances = Math.Min(_minInstances, _maxInstances);
		_capacity = new SemaphoreSlim(_maxInstances * _maxSessionsPerInstance);
	}

	public string Diagnostic => $"opencode-pool#{_poolId}";

	public async Task PrewarmAsync(CancellationToken cancellationToken)
	{
		for (var i = 0; i < _minInstances; i++)
		{
			var worker = await CreateWorkerAsync(cancellationToken).ConfigureAwait(false);
			lock (_lock)
			{
				_workers.Add(worker);
			}
		}
		LogPrewarmed(_poolId, _minInstances, _maxInstances, _maxSessionsPerInstance);
	}

	public async Task<IOpenCodeServerLease> AcquireAsync(CancellationToken cancellationToken)
	{
		await _capacity.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			var worker = await GetOrCreateWorkerAsync(cancellationToken).ConfigureAwait(false);
			return new Lease(this, worker);
		}
		catch
		{
			_capacity.Release();
			throw;
		}
	}

	private async Task<Worker> GetOrCreateWorkerAsync(CancellationToken cancellationToken)
	{
		// The capacity semaphore guarantees a free slot exists: either an existing worker has
		// spare session capacity, or we are below the instance cap and may spawn a new worker.
		lock (_lock)
		{
			ObjectDisposedException.ThrowIf(_disposed, this);
			var available = _workers.FirstOrDefault(w => w.Active < _maxSessionsPerInstance);
			if (available is not null)
			{
				available.Active++;
				return available;
			}

			if (_workers.Count + _pendingSpawns < _maxInstances)
			{
				_pendingSpawns++;
			}
			else
			{
				// Should not happen given the capacity semaphore; defend anyway.
				throw new InvalidOperationException("OpenCode pool capacity invariant violated.");
			}
		}

		Worker worker;
		try
		{
			worker = await CreateWorkerAsync(cancellationToken).ConfigureAwait(false);
		}
		catch
		{
			lock (_lock) { _pendingSpawns--; }
			throw;
		}

		lock (_lock)
		{
			_pendingSpawns--;
			worker.Active = 1;
			_workers.Add(worker);
		}
		return worker;
	}

	private async Task<Worker> CreateWorkerAsync(CancellationToken cancellationToken)
	{
		var plan = OpenCodeServerBootstrap.Resolve(_options);
		var process = new OpenCodeServerProcess(plan, _options, _clientFactory, _loggerFactory.CreateLogger<OpenCodeServerProcess>());
		await process.StartAsync(cancellationToken).ConfigureAwait(false);

		var holder = new EngineToolContextHolder();
		OpenCodeEngineToolBridge? bridge = null;
		if (_options.EngineToolBridgeEnabled)
		{
			try
			{
				bridge = await OpenCodeEngineToolBridge.StartAsync(holder, _options.Hostname, _loggerFactory, cancellationToken).ConfigureAwait(false);
			}
			catch (Exception ex)
			{
				LogBridgeStartError(ex, _poolId);
			}
		}

		return new Worker(process, holder, bridge);
	}

	private void Release(Worker worker)
	{
		lock (_lock)
		{
			worker.Active = Math.Max(0, worker.Active - 1);
			worker.ContextHolder.Clear();
		}
		_capacity.Release();
	}

	/// <summary>
	/// Reserves a capacity slot for a dedicated (per-step, config-having) OpenCode server spawned
	/// directly by <c>OpenCodeAgent.RunDedicatedAsync</c> rather than leased from the worker list.
	/// Blocks until the run's capacity has a free slot so dedicated servers are bounded by the same
	/// <c>maxInstances</c> cap as pooled workers, and counts the server in <see cref="GetSnapshot"/>
	/// (as one instance and one active session) until the returned handle is disposed. Each
	/// dedicated server hosts exactly one session, so instance count tracks session count.
	/// </summary>
	public async Task<IAsyncDisposable> AcquireDedicatedSlotAsync(CancellationToken cancellationToken)
	{
		await _capacity.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			lock (_lock)
			{
				ObjectDisposedException.ThrowIf(_disposed, this);
				_dedicatedInstances++;
			}
			return new DedicatedSlot(this);
		}
		catch
		{
			_capacity.Release();
			throw;
		}
	}

	private void ReleaseDedicated()
	{
		lock (_lock)
		{
			_dedicatedInstances = Math.Max(0, _dedicatedInstances - 1);
		}
		_capacity.Release();
	}

	public OpenCodeServerPoolSnapshot GetSnapshot()
	{
		lock (_lock)
		{
			return new OpenCodeServerPoolSnapshot(
				_workers.Count + _dedicatedInstances,
				_workers.Sum(w => w.Active) + _dedicatedInstances,
				_maxInstances);
		}
	}

	public async ValueTask DisposeAsync()
	{
		List<Worker> workers;
		lock (_lock)
		{
			_disposed = true;
			workers = [.. _workers];
			_workers.Clear();
		}

		foreach (var worker in workers)
		{
			try { await worker.Process.DisposeAsync().ConfigureAwait(false); }
			catch (Exception ex) { LogWorkerDisposeError(ex, _poolId); }

			if (worker.Bridge is not null)
			{
				try { await worker.Bridge.DisposeAsync().ConfigureAwait(false); }
				catch (Exception ex) { LogWorkerDisposeError(ex, _poolId); }
			}
		}

		_capacity.Dispose();
	}

	private sealed class Worker(OpenCodeServerProcess process, EngineToolContextHolder holder, OpenCodeEngineToolBridge? bridge)
	{
		public OpenCodeServerProcess Process { get; } = process;
		public int Active;
		public EngineToolContextHolder ContextHolder { get; } = holder;
		public OpenCodeEngineToolBridge? Bridge { get; } = bridge;
		public string? EngineToolMcpUrl => Bridge?.McpUrl;
	}

	private sealed class Lease(OpenCodeServerPool pool, Worker worker) : IOpenCodeServerLease
	{
		private int _disposed;

		public IOpenCodeClient Client => worker.Process.Client;
		public string BaseUrl => worker.Process.BaseUrl;
		public string? EngineToolMcpUrl => worker.EngineToolMcpUrl;
		public EngineToolContextHolder ContextHolder => worker.ContextHolder;

		public ValueTask DisposeAsync()
		{
			if (Interlocked.Exchange(ref _disposed, 1) == 0)
				pool.Release(worker);
			return ValueTask.CompletedTask;
		}
	}

	/// <summary>
	/// Handle for a reserved dedicated-server capacity slot. Disposing it releases the capacity
	/// permit and decrements the pool's dedicated-instance count.
	/// </summary>
	private sealed class DedicatedSlot(OpenCodeServerPool pool) : IAsyncDisposable
	{
		private int _disposed;

		public ValueTask DisposeAsync()
		{
			if (Interlocked.Exchange(ref _disposed, 1) == 0)
				pool.ReleaseDedicated();
			return ValueTask.CompletedTask;
		}
	}

	[LoggerMessage(EventId = 210, Level = LogLevel.Information, Message = "OpenCode pool#{PoolId}: prewarmed {Min} instance(s) (max {Max} × {Sessions} sessions)")]
	private partial void LogPrewarmed(int poolId, int min, int max, int sessions);

	[LoggerMessage(EventId = 211, Level = LogLevel.Warning, Message = "OpenCode pool#{PoolId}: error disposing worker")]
	private partial void LogWorkerDisposeError(Exception ex, int poolId);

	[LoggerMessage(EventId = 212, Level = LogLevel.Warning, Message = "OpenCode pool#{PoolId}: engine-tool bridge failed to start; engine tools will be unavailable for this worker")]
	private partial void LogBridgeStartError(Exception ex, int poolId);
}
