using GitHub.Copilot.SDK;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Orchestra.Engine;

namespace Orchestra.Copilot;

public partial class CopilotAgentBuilder : AgentBuilder, IAsyncDisposable
{
	private readonly ILoggerFactory _loggerFactory;
	private readonly ILogger<CopilotAgentBuilder> _logger;
	private static int _scopeCounter;
	private static int _fallbackBuildCounter;

	/// <summary>
	/// Per-run client scoped via AsyncLocal&lt;Holder&gt;. The Holder is a mutable wrapper
	/// so mutations from inside async methods are visible to the caller's ExecutionContext.
	/// (AsyncLocal&lt;T&gt;.Value set inside an async method is NOT visible to the caller because
	/// the mutation is captured in a child EC frame that is discarded when the method returns.
	/// Mutating a field on a holder that the caller already has a reference to avoids this.)
	/// </summary>
	private readonly AsyncLocal<ClientHolder?> _runScopedClient = new();

	private sealed class ClientHolder
	{
		public CopilotClient? Client;
	}

	/// <summary>
	/// Fallback client for code paths that don't create a run scope (e.g., tests, standalone usage).
	/// Created lazily on first use and disposed with the builder.
	/// </summary>
	private CopilotClient? _fallbackClient;
	private volatile bool _fallbackStarted;
	private readonly SemaphoreSlim _fallbackLock = new(1, 1);

	/// <summary>
	/// Cached available model info from the last ListModelsAsync call.
	/// Avoids repeated network calls when multiple steps hit model mismatch in the same run.
	/// </summary>
	internal IReadOnlyList<AvailableModelInfo>? CachedAvailableModels { get; set; }

	public CopilotAgentBuilder(ILoggerFactory? loggerFactory = null)
	{
		_loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
		_logger = _loggerFactory.CreateLogger<CopilotAgentBuilder>();
	}

	/// <summary>
	/// Creates a run-scoped client for an orchestration run.
	/// All steps within the run share this client (each gets its own session).
	/// The client is disposed when the returned scope is disposed.
	/// </summary>
	public override Task<IAsyncDisposable> CreateRunScopeAsync(CancellationToken cancellationToken = default)
	{
		// CRITICAL: Set the AsyncLocal holder SYNCHRONOUSLY before any await. This installs
		// the holder reference in the caller's ExecutionContext. The Client field is mutated
		// inside the async helper after StartAsync completes — the caller (and any tasks it
		// spawns afterwards) sees the mutation because they share the same holder reference.
		var holder = new ClientHolder();
		_runScopedClient.Value = holder;
		return CreateRunScopeAsyncCore(holder, cancellationToken);
	}

	private async Task<IAsyncDisposable> CreateRunScopeAsyncCore(ClientHolder holder, CancellationToken cancellationToken)
	{
		var scopeId = Interlocked.Increment(ref _scopeCounter);
		LogRunScopeCreating(scopeId, Environment.CurrentManagedThreadId);
		var sw = System.Diagnostics.Stopwatch.StartNew();
		var client = new CopilotClient();
		try
		{
			await client.StartAsync(cancellationToken).ConfigureAwait(false);
		}
		catch (Exception ex)
		{
			LogRunScopeStartFailed(ex, scopeId, sw.ElapsedMilliseconds);
			try { await client.DisposeAsync().ConfigureAwait(false); } catch { }
			// Clear the holder on failure so callers don't observe a half-initialised scope.
			holder.Client = null;
			throw;
		}
		holder.Client = client;
		LogRunScopeCreated(scopeId, sw.ElapsedMilliseconds, client.GetHashCode());
		LogRunScopeAsyncLocalCheck(scopeId, _runScopedClient.Value?.Client?.GetHashCode().ToString() ?? "null", Environment.CurrentManagedThreadId);
		return new RunScope(this, holder, client, scopeId);
	}

	/// <summary>
	/// Gets the active client: run-scoped if inside a run scope, fallback otherwise.
	/// </summary>
	/// <summary>
	/// Diagnostic: returns the current AsyncLocal run-scoped client (or null).
	/// Used by external callers to verify EC flow.
	/// </summary>
	public override string? GetRunScopedClientDiagnostic()
		=> _runScopedClient.Value?.Client?.GetHashCode().ToString();

	private async Task<CopilotClient> GetActiveClientAsync(CancellationToken cancellationToken)
	{
		// Prefer run-scoped client (set by CreateRunScopeAsync via the holder)
		var holder = _runScopedClient.Value;
		var client = holder?.Client;
		LogActiveClientCheck(client is null ? "null" : client.GetHashCode().ToString(), Environment.CurrentManagedThreadId);
		if (client is not null)
		{
			LogActiveClientResolved("run-scoped", client.GetHashCode(), Environment.CurrentManagedThreadId);
			return client;
		}

		// Fallback: lazily create a shared client (for standalone/test usage)
		var fallback = await GetOrCreateFallbackClientAsync(cancellationToken).ConfigureAwait(false);
		LogActiveClientResolved("fallback", fallback.GetHashCode(), Environment.CurrentManagedThreadId);
		return fallback;
	}

	private async Task<CopilotClient> GetOrCreateFallbackClientAsync(CancellationToken cancellationToken)
	{
		if (_fallbackStarted && _fallbackClient is not null)
			return _fallbackClient;

		await _fallbackLock.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			if (!_fallbackStarted || _fallbackClient is null)
			{
				var fallbackId = Interlocked.Increment(ref _fallbackBuildCounter);
				LogFallbackClientCreating(fallbackId, Environment.CurrentManagedThreadId, Environment.StackTrace);
				_fallbackClient = new CopilotClient();
				await _fallbackClient.StartAsync(cancellationToken).ConfigureAwait(false);
				_fallbackStarted = true;
				LogFallbackClientCreated(fallbackId, _fallbackClient.GetHashCode());
			}
			return _fallbackClient;
		}
		finally
		{
			_fallbackLock.Release();
		}
	}

	public override async Task<IAgent> BuildAgentAsync(CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(Model, nameof(Model));

		// Capture state immediately to avoid race conditions with concurrent builder usage
		var model = Model;
		var systemPrompt = SystemPrompt;
		var mcps = Mcps;
		var subagents = Subagents;
		var reasoningLevel = ReasoningLevel;
		var systemPromptMode = SystemPromptMode;
		var systemPromptSections = SystemPromptSectionOverrides;
		var reporter = Reporter;
		var engineTools = EngineTools;
		var engineToolCtx = EngineToolCtx;
		var skillDirectories = SkillDirectories;
		var infiniteSessionConfig = InfiniteSession;
		var attachments = Attachments;

		var client = await GetActiveClientAsync(cancellationToken).ConfigureAwait(false);

		return new CopilotAgent(
			client: client,
			model: model,
			systemPrompt: systemPrompt,
			mcps: mcps,
			subagents: subagents,
			reasoningLevel: reasoningLevel,
			systemPromptMode: systemPromptMode,
			systemPromptSections: systemPromptSections,
			reporter: reporter,
			engineTools: engineTools,
			engineToolContext: engineToolCtx,
			skillDirectories: skillDirectories,
			infiniteSessionConfig: infiniteSessionConfig,
			attachments: attachments,
			cachedAvailableModels: CachedAvailableModels,
			onAvailableModelsListed: models => CachedAvailableModels = models,
			logger: _loggerFactory.CreateLogger<CopilotAgent>()
		);
	}

	public override async Task<IAgent> BuildAgentAsync(AgentBuildConfig config, CancellationToken cancellationToken = default)
	{
		var client = await GetActiveClientAsync(cancellationToken).ConfigureAwait(false);

		return new CopilotAgent(
			client: client,
			model: config.Model,
			systemPrompt: config.SystemPrompt,
			mcps: config.Mcps,
			subagents: config.Subagents,
			reasoningLevel: config.ReasoningLevel,
			systemPromptMode: config.SystemPromptMode,
			systemPromptSections: config.SystemPromptSections,
			reporter: config.Reporter,
			engineTools: config.EngineTools,
			engineToolContext: config.EngineToolCtx,
			skillDirectories: config.SkillDirectories,
			infiniteSessionConfig: config.InfiniteSessionConfig,
			attachments: config.Attachments,
			cachedAvailableModels: CachedAvailableModels,
			onAvailableModelsListed: models => CachedAvailableModels = models,
			logger: _loggerFactory.CreateLogger<CopilotAgent>()
		);
	}

	public async ValueTask DisposeAsync()
	{
		if (_fallbackClient is not null)
		{
			try { await _fallbackClient.StopAsync().ConfigureAwait(false); } catch { }
			try { await _fallbackClient.DisposeAsync().ConfigureAwait(false); } catch { }
		}
		_fallbackLock.Dispose();

		GC.SuppressFinalize(this);
	}

	/// <summary>
	/// Manages the lifecycle of a per-run CopilotClient.
	/// Disposing the scope stops and disposes the client.
	/// </summary>
	private sealed class RunScope : IAsyncDisposable
	{
		private readonly CopilotAgentBuilder _builder;
		private readonly ClientHolder _holder;
		private readonly CopilotClient _client;
		private readonly int _scopeId;

		public RunScope(CopilotAgentBuilder builder, ClientHolder holder, CopilotClient client, int scopeId)
		{
			_builder = builder;
			_holder = holder;
			_client = client;
			_scopeId = scopeId;
		}

		public async ValueTask DisposeAsync()
		{
			_builder.LogRunScopeDisposing(_scopeId, _client.GetHashCode(), Environment.CurrentManagedThreadId);

			// Clear the holder's client field so any stragglers see a null run-scoped client
			// and fall through to the fallback path (which will warn). We don't clear the
			// AsyncLocal itself because the holder reference may still be in flight in other
			// async branches and we want them to observe a definitive null.
			_holder.Client = null;
			_builder.CachedAvailableModels = null;

			var sw = System.Diagnostics.Stopwatch.StartNew();
			try { await _client.StopAsync().ConfigureAwait(false); }
			catch (Exception ex) { _builder.LogRunScopeStopError(ex, _scopeId); }

			try { await _client.DisposeAsync().ConfigureAwait(false); }
			catch (Exception ex) { _builder.LogRunScopeDisposeError(ex, _scopeId); }

			_builder.LogRunScopeDisposed(_scopeId, sw.ElapsedMilliseconds);
		}
	}

	#region Source-Generated Logging

	[LoggerMessage(EventId = 100, Level = LogLevel.Information,
		Message = "RunScope#{ScopeId}: creating run-scoped Copilot CLI client (thread={ThreadId})")]
	private partial void LogRunScopeCreating(int scopeId, int threadId);

	[LoggerMessage(EventId = 101, Level = LogLevel.Information,
		Message = "RunScope#{ScopeId}: created run-scoped Copilot CLI client in {ElapsedMs}ms (clientHash={ClientHash})")]
	private partial void LogRunScopeCreated(int scopeId, long elapsedMs, int clientHash);

	[LoggerMessage(EventId = 102, Level = LogLevel.Error,
		Message = "RunScope#{ScopeId}: failed to start CLI client after {ElapsedMs}ms")]
	private partial void LogRunScopeStartFailed(Exception ex, int scopeId, long elapsedMs);

	[LoggerMessage(EventId = 103, Level = LogLevel.Information,
		Message = "RunScope#{ScopeId}: disposing (clientHash={ClientHash}, thread={ThreadId})")]
	private partial void LogRunScopeDisposing(int scopeId, int clientHash, int threadId);

	[LoggerMessage(EventId = 104, Level = LogLevel.Information,
		Message = "RunScope#{ScopeId}: disposed in {ElapsedMs}ms")]
	private partial void LogRunScopeDisposed(int scopeId, long elapsedMs);

	[LoggerMessage(EventId = 105, Level = LogLevel.Warning,
		Message = "RunScope#{ScopeId}: error stopping CLI client during dispose")]
	private partial void LogRunScopeStopError(Exception ex, int scopeId);

	[LoggerMessage(EventId = 106, Level = LogLevel.Warning,
		Message = "RunScope#{ScopeId}: error disposing CLI client")]
	private partial void LogRunScopeDisposeError(Exception ex, int scopeId);

	[LoggerMessage(EventId = 107, Level = LogLevel.Debug,
		Message = "BuildAgent: resolved {ClientKind} client (clientHash={ClientHash}, thread={ThreadId})")]
	private partial void LogActiveClientResolved(string clientKind, int clientHash, int threadId);

	[LoggerMessage(EventId = 110, Level = LogLevel.Debug,
		Message = "BuildAgent: AsyncLocal _runScopedClient.Value = {ClientValue} on thread {ThreadId}")]
	private partial void LogActiveClientCheck(string clientValue, int threadId);

	[LoggerMessage(EventId = 111, Level = LogLevel.Debug,
		Message = "RunScope#{ScopeId}: post-set check, _runScopedClient.Value = {ClientValue} on thread {ThreadId}")]
	private partial void LogRunScopeAsyncLocalCheck(int scopeId, string clientValue, int threadId);

	[LoggerMessage(EventId = 108, Level = LogLevel.Warning,
		Message = "Fallback#{FallbackId}: NO RUN SCOPE active on thread {ThreadId} — creating fallback CLI client. Stack:\n{StackTrace}")]
	private partial void LogFallbackClientCreating(int fallbackId, int threadId, string stackTrace);

	[LoggerMessage(EventId = 109, Level = LogLevel.Warning,
		Message = "Fallback#{FallbackId}: fallback CLI client started (clientHash={ClientHash})")]
	private partial void LogFallbackClientCreated(int fallbackId, int clientHash);

	#endregion
}
