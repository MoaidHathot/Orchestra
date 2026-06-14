using GitHub.Copilot;

namespace Orchestra.Copilot;

internal interface ICopilotClient : IAsyncDisposable
{
	int DiagnosticHash { get; }
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
	IDisposable On(Action<SessionEvent> handler);
	Task<string> SendAsync(MessageOptions options, CancellationToken cancellationToken);
	Task AbortAsync(CancellationToken cancellationToken = default);

	/// <summary>
	/// Applies an opt-in sandbox policy to the live session via the runtime's options-update RPC.
	/// Default no-op so non-SDK session fakes (tests) are unaffected; only the real adapter
	/// patches the session.
	/// </summary>
	Task ApplySandboxAsync(Orchestra.Engine.SandboxPolicy policy, CancellationToken cancellationToken) => Task.CompletedTask;
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
		// SDK 1.0.0 keeps the IList<ModelInfo> return shape from 0.3.0; we just snapshot
		// it into a read-only list so callers can iterate safely from any thread.
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
	public IDisposable On(Action<SessionEvent> handler) => _session.On(handler);
	public Task<string> SendAsync(MessageOptions options, CancellationToken cancellationToken) => _session.SendAsync(options, cancellationToken);
	public Task AbortAsync(CancellationToken cancellationToken = default) => _session.AbortAsync(cancellationToken);
	public ValueTask DisposeAsync() => _session.DisposeAsync();

	// SandboxConfig + OptionsApi are evaluation-only SDK APIs (GHCP001); the options-update RPC
	// is the supported way to patch a live session's sandbox, so suppress narrowly here.
#pragma warning disable GHCP001
	public async Task ApplySandboxAsync(Orchestra.Engine.SandboxPolicy policy, CancellationToken cancellationToken)
	{
		var sandbox = BuildSandboxConfig(policy);
		await _session.Rpc.Options.UpdateAsync(sandboxConfig: sandbox, cancellationToken: cancellationToken).ConfigureAwait(false);
	}

	private static GitHub.Copilot.Rpc.SandboxConfig BuildSandboxConfig(Orchestra.Engine.SandboxPolicy policy)
		=> BuildSandboxConfigCore(policy);

	/// <summary>Maps an Orchestra <see cref="Orchestra.Engine.SandboxPolicy"/> to the SDK's SandboxConfig. Exposed for tests.</summary>
	internal static GitHub.Copilot.Rpc.SandboxConfig BuildSandboxConfigCore(Orchestra.Engine.SandboxPolicy policy)
	{
		var userPolicy = new GitHub.Copilot.Rpc.SandboxConfigUserPolicy();

		if (policy.Filesystem is { } fs)
		{
			userPolicy.Filesystem = new GitHub.Copilot.Rpc.SandboxConfigUserPolicyFilesystem
			{
				ReadonlyPaths = fs.ReadonlyPaths.Length > 0 ? fs.ReadonlyPaths : null,
				ReadwritePaths = fs.ReadwritePaths.Length > 0 ? fs.ReadwritePaths : null,
				DeniedPaths = fs.DeniedPaths.Length > 0 ? fs.DeniedPaths : null,
			};
		}

		if (policy.Network is { } net)
		{
			userPolicy.Network = new GitHub.Copilot.Rpc.SandboxConfigUserPolicyNetwork
			{
				AllowedHosts = net.AllowedHosts.Length > 0 ? net.AllowedHosts : null,
				BlockedHosts = net.BlockedHosts.Length > 0 ? net.BlockedHosts : null,
				AllowOutbound = net.AllowOutbound,
				AllowLocalNetwork = net.AllowLocalNetwork,
			};
		}

		return new GitHub.Copilot.Rpc.SandboxConfig
		{
			Enabled = true,
			UserPolicy = userPolicy,
		};
	}
#pragma warning restore GHCP001
}

internal interface ICopilotClientFactory
{
	ICopilotClient CreateClient();
}

internal sealed class CopilotSdkClientFactory : ICopilotClientFactory
{
	private readonly string? _baseDirectory;
	private readonly string? _gitHubToken;
	private readonly bool? _useLoggedInUser;

	public CopilotSdkClientFactory(string? baseDirectory = null, string? gitHubToken = null, bool? useLoggedInUser = null)
	{
		_baseDirectory = baseDirectory;
		_gitHubToken = gitHubToken;
		_useLoggedInUser = useLoggedInUser;
	}

	public ICopilotClient CreateClient()
	{
		// Resolve the Copilot CLI binary lazily through the bootstrap. The first call here
		// (in a fresh install) blocks until the npm download completes (~100 MB, one-off
		// per machine per CLI version); subsequent calls return instantly from the
		// per-process Lazy cache or the on-disk cache the bootstrap maintains.
		//
		// We block synchronously because CreateClient is sync (ICopilotClientFactory is
		// used from sync construction paths in CopilotClientPool). GetAwaiter().GetResult()
		// is safe here: the bootstrap performs only HTTP + file I/O, no SynchronizationContext-
		// bound work, so there's no deadlock risk on UI/ASP.NET request contexts.
		var cliPath = CopilotCliBootstrap.EnsureAsync().GetAwaiter().GetResult();

		// SDK 1.0.0 replaced CopilotClientOptions.CliPath with the RuntimeConnection abstraction;
		// stdio (child-process) is what we used before, so this is a like-for-like translation.
		var options = new CopilotClientOptions
		{
			Connection = RuntimeConnection.ForStdio(cliPath),
		};

		// Optional: BaseDirectory routes Copilot's per-session state (COPILOT_HOME) to a
		// caller-supplied path so multi-tenant hosts can give each tenant an isolated
		// session store. When null, the SDK uses ~/.copilot.
		if (!string.IsNullOrEmpty(_baseDirectory))
		{
			options.BaseDirectory = _baseDirectory;
		}

		// Host-level Copilot auth (orchestra.json copilot.gitHubToken / useLoggedInUser).
		// Applied at the client (CLI process) layer so it is the default for every session
		// in the run; a per-step SessionConfig.GitHubToken still overrides it for that session.
		if (!string.IsNullOrEmpty(_gitHubToken))
		{
			options.GitHubToken = _gitHubToken;
		}

		if (_useLoggedInUser is not null)
		{
			options.UseLoggedInUser = _useLoggedInUser.Value;
		}

		return new CopilotSdkClientAdapter(new CopilotClient(options));
	}
}
