using GitHub.Copilot.SDK;

namespace Orchestra.Copilot;

internal interface ICopilotClient : IAsyncDisposable
{
	int DiagnosticHash { get; }
	ConnectionState State { get; }
	Task StartAsync(CancellationToken cancellationToken);
	Task StopAsync();
	Task PingAsync(string message, CancellationToken cancellationToken);
	Task<ICopilotSession> CreateSessionAsync(SessionConfig config, CancellationToken cancellationToken);

	/// <summary>
	/// Resumes an existing on-disk Copilot session by id. The Copilot SDK persists session
	/// state across CLI process boundaries, so a session created/started on a previous (now
	/// dead) CLI worker can be resumed on a freshly spawned worker as long as both workers
	/// share the same config dir (the default on Windows: <c>%USERPROFILE%\.copilot\</c>).
	/// Used by <see cref="CopilotAgent"/>'s swap-and-resume loop to recover an in-flight
	/// session after a JSON-RPC transport failure without losing conversation history.
	/// </summary>
	Task<ICopilotSession> ResumeSessionAsync(string sessionId, ResumeSessionConfig config, CancellationToken cancellationToken);

	/// <summary>
	/// Returns the most recently used session id known to the CLI, or null when no
	/// session has been seen yet. Diagnostic only — Orchestra always tracks the id it
	/// just created itself, so this is exposed mainly for tests and future tooling.
	/// </summary>
	Task<string?> GetLastSessionIdAsync(CancellationToken cancellationToken);

	/// <summary>
	/// Permanently deletes a session and all its on-disk data. Used as best-effort cleanup
	/// when a resume attempt is abandoned because the dying CLI never released the lock
	/// (<c>SessionResumeData.AlreadyInUse</c> remained true past our grace window), so
	/// stale session files don't accumulate on disk across orchestration runs.
	/// </summary>
	Task DeleteSessionAsync(string sessionId, CancellationToken cancellationToken);

	Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken cancellationToken);
}

internal interface ICopilotSession : IAsyncDisposable
{
	string SessionId { get; }
	IDisposable On(SessionEventHandler handler);
	Task<string> SendAsync(MessageOptions options, CancellationToken cancellationToken);
	Task AbortAsync(CancellationToken cancellationToken = default);
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

	public async Task<ICopilotSession> CreateSessionAsync(SessionConfig config, CancellationToken cancellationToken)
	{
		var session = await _client.CreateSessionAsync(config, cancellationToken).ConfigureAwait(false);
		return new CopilotSdkSessionAdapter(session);
	}

	public async Task<ICopilotSession> ResumeSessionAsync(string sessionId, ResumeSessionConfig config, CancellationToken cancellationToken)
	{
		var session = await _client.ResumeSessionAsync(sessionId, config, cancellationToken).ConfigureAwait(false);
		return new CopilotSdkSessionAdapter(session);
	}

	public Task<string?> GetLastSessionIdAsync(CancellationToken cancellationToken)
		=> _client.GetLastSessionIdAsync(cancellationToken);

	public Task DeleteSessionAsync(string sessionId, CancellationToken cancellationToken)
		=> _client.DeleteSessionAsync(sessionId, cancellationToken);

	public async Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken cancellationToken)
	{
		var models = await _client.ListModelsAsync(cancellationToken).ConfigureAwait(false);
		return [.. models];
	}

	public ValueTask DisposeAsync()
		=> _ownsClient ? _client.DisposeAsync() : ValueTask.CompletedTask;
}

internal sealed class CopilotSdkSessionAdapter : ICopilotSession
{
	private readonly CopilotSession _session;

	public CopilotSdkSessionAdapter(CopilotSession session)
	{
		_session = session;
	}

	public string SessionId => _session.SessionId;
	public IDisposable On(SessionEventHandler handler) => _session.On(handler);
	public Task<string> SendAsync(MessageOptions options, CancellationToken cancellationToken) => _session.SendAsync(options, cancellationToken);
	public Task AbortAsync(CancellationToken cancellationToken = default) => _session.AbortAsync(cancellationToken);
	public ValueTask DisposeAsync() => _session.DisposeAsync();
}

internal interface ICopilotClientFactory
{
	ICopilotClient CreateClient();
}

internal sealed class CopilotSdkClientFactory : ICopilotClientFactory
{
	public ICopilotClient CreateClient() => new CopilotSdkClientAdapter(new CopilotClient());
}
