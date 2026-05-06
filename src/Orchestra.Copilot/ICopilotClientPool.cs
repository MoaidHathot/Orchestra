namespace Orchestra.Copilot;

internal interface ICopilotClientPool
{
	ValueTask<ICopilotClientLease> AcquireAsync(CancellationToken cancellationToken);
}

internal interface ICopilotClientLease : IAsyncDisposable
{
	ICopilotClient Client { get; }
	ISessionFaultBroker? FaultBroker { get; }
	IReadOnlyList<Engine.AvailableModelInfo>? CachedAvailableModels { get; }
	void SetCachedAvailableModels(IReadOnlyList<Engine.AvailableModelInfo> models);
}

internal sealed class FixedCopilotClientPool : ICopilotClientPool
{
	private readonly ICopilotClient _client;
	private readonly ISessionFaultBroker? _faultBroker;
	private IReadOnlyList<Engine.AvailableModelInfo>? _cachedAvailableModels;

	public FixedCopilotClientPool(ICopilotClient client, ISessionFaultBroker? faultBroker = null)
	{
		_client = client;
		_faultBroker = faultBroker;
	}

	public ValueTask<ICopilotClientLease> AcquireAsync(CancellationToken cancellationToken)
	{
		_ = cancellationToken;
		return ValueTask.FromResult<ICopilotClientLease>(new Lease(this));
	}

	private sealed class Lease : ICopilotClientLease
	{
		private readonly FixedCopilotClientPool _pool;

		public Lease(FixedCopilotClientPool pool)
		{
			_pool = pool;
		}

		public ICopilotClient Client => _pool._client;
		public ISessionFaultBroker? FaultBroker => _pool._faultBroker;
		public IReadOnlyList<Engine.AvailableModelInfo>? CachedAvailableModels => _pool._cachedAvailableModels;
		public void SetCachedAvailableModels(IReadOnlyList<Engine.AvailableModelInfo> models) => _pool._cachedAvailableModels = models;
		public ValueTask DisposeAsync() => ValueTask.CompletedTask;
	}
}
