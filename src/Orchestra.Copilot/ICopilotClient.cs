using GitHub.Copilot.SDK;

namespace Orchestra.Copilot;

internal interface ICopilotClient : IAsyncDisposable
{
	int DiagnosticHash { get; }
	ConnectionState State { get; }
	Task StartAsync(CancellationToken cancellationToken);
	Task StopAsync();
	Task PingAsync(string message, CancellationToken cancellationToken);
	Task<CopilotSession> CreateSessionAsync(SessionConfig config, CancellationToken cancellationToken);
	Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken cancellationToken);
}

internal sealed class CopilotSdkClientAdapter : ICopilotClient
{
	private readonly CopilotClient _client;
	private readonly bool _ownsClient;

	public CopilotSdkClientAdapter(CopilotClient client, bool ownsClient = true)
	{
		_client = client;
		_ownsClient = ownsClient;
	}

	public int DiagnosticHash => _client.GetHashCode();
	public ConnectionState State => _client.State;
	public Task StartAsync(CancellationToken cancellationToken) => _client.StartAsync(cancellationToken);
	public Task StopAsync() => _client.StopAsync();

	public async Task PingAsync(string message, CancellationToken cancellationToken)
	{
		_ = await _client.PingAsync(message, cancellationToken).ConfigureAwait(false);
	}

	public Task<CopilotSession> CreateSessionAsync(SessionConfig config, CancellationToken cancellationToken)
		=> _client.CreateSessionAsync(config, cancellationToken);

	public async Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken cancellationToken)
	{
		var models = await _client.ListModelsAsync(cancellationToken).ConfigureAwait(false);
		return [.. models];
	}

	public ValueTask DisposeAsync()
		=> _ownsClient ? _client.DisposeAsync() : ValueTask.CompletedTask;
}

internal interface ICopilotClientFactory
{
	ICopilotClient CreateClient();
}

internal sealed class CopilotSdkClientFactory : ICopilotClientFactory
{
	public ICopilotClient CreateClient() => new CopilotSdkClientAdapter(new CopilotClient());
}
