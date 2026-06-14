using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Orchestra.Engine;

namespace Orchestra.Copilot;

public partial class CopilotAgentBuilder : AgentBuilder, IAsyncDisposable
{
	private readonly ILoggerFactory _loggerFactory;
	private readonly ILogger<CopilotAgentBuilder> _logger;
	private readonly CopilotAgentPoolOptions _poolOptions;
	private readonly ICopilotClientFactory _clientFactory;
	private readonly object _activePoolsLock = new();
	private readonly HashSet<CopilotClientPool> _activePools = [];
	private static int _scopeCounter;

	/// <summary>
	/// Per-run client scoped via AsyncLocal&lt;Holder&gt;. The Holder is a mutable wrapper
	/// so mutations from inside async methods are visible to the caller's ExecutionContext.
	/// (AsyncLocal&lt;T&gt;.Value set inside an async method is NOT visible to the caller because
	/// the mutation is captured in a child EC frame that is discarded when the method returns.
	/// Mutating a field on a holder that the caller already has a reference to avoids this.)
	/// </summary>
	private readonly AsyncLocal<PoolHolder?> _runScopedClient = new();

	private sealed class PoolHolder
	{
		public CopilotClientPool? Pool;
	}

	public CopilotAgentBuilder(ILoggerFactory? loggerFactory = null, CopilotAgentPoolOptions? poolOptions = null)
		: this(loggerFactory, poolOptions, new CopilotSdkClientFactory(
			gitHubToken: poolOptions?.GitHubToken,
			useLoggedInUser: poolOptions?.UseLoggedInUser))
	{
	}

	internal CopilotAgentBuilder(
		ILoggerFactory? loggerFactory,
		CopilotAgentPoolOptions? poolOptions,
		ICopilotClientFactory clientFactory)
	{
		_loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
		_logger = _loggerFactory.CreateLogger<CopilotAgentBuilder>();
		_poolOptions = poolOptions ?? new CopilotAgentPoolOptions();
		_clientFactory = clientFactory;
	}

	/// <summary>
	/// Creates a run-scoped pool for an orchestration run.
	/// Prompt steps within the run acquire leases from this pool.
	/// The pool is disposed when the returned scope is disposed.
	/// </summary>
	public override Task<IAsyncDisposable> CreateRunScopeAsync(
		AgentPoolConfig? agentPool = null,
		CancellationToken cancellationToken = default)
	{
		// CRITICAL: Set the AsyncLocal holder SYNCHRONOUSLY before any await. This installs
		// the holder reference in the caller's ExecutionContext. The Pool field is mutated
		// inside the async helper after StartAsync completes — the caller (and any tasks it
		// spawns afterwards) sees the mutation because they share the same holder reference.
		var holder = new PoolHolder();
		_runScopedClient.Value = holder;
		return CreateRunScopeAsyncCore(holder, agentPool, cancellationToken);
	}

	private async Task<IAsyncDisposable> CreateRunScopeAsyncCore(
		PoolHolder holder,
		AgentPoolConfig? agentPool,
		CancellationToken cancellationToken)
	{
		var scopeId = Interlocked.Increment(ref _scopeCounter);
		LogRunScopeCreating(scopeId, Environment.CurrentManagedThreadId);
		var sw = System.Diagnostics.Stopwatch.StartNew();
		var pool = new CopilotClientPool(agentPool, _poolOptions, _clientFactory, _loggerFactory);
		try
		{
			await pool.PrewarmAsync(cancellationToken).ConfigureAwait(false);
		}
		catch (Exception ex)
		{
			LogRunScopeStartFailed(ex, scopeId, sw.ElapsedMilliseconds);
			try { await pool.DisposeAsync().ConfigureAwait(false); } catch { }
			// Clear the holder on failure so callers don't observe a half-initialised scope.
			holder.Pool = null;
			throw;
		}
		holder.Pool = pool;
		RegisterActivePool(pool);
		LogRunScopeCreated(scopeId, sw.ElapsedMilliseconds, pool.Diagnostic);
		LogRunScopeAsyncLocalCheck(scopeId, _runScopedClient.Value?.Pool?.Diagnostic ?? "null", Environment.CurrentManagedThreadId);
		return new RunScope(this, holder, pool, scopeId);
	}

	/// <summary>
	/// Gets the active client: run-scoped if inside a run scope, fallback otherwise.
	/// </summary>
	/// <summary>
	/// Diagnostic: returns the current AsyncLocal run-scoped client (or null).
	/// Used by external callers to verify EC flow.
	/// </summary>
	public override string? GetRunScopedClientDiagnostic()
		=> _runScopedClient.Value?.Pool?.Diagnostic;

	public override AgentRuntimeStatus GetRuntimeStatus()
	{
		CopilotClientPool[] pools;
		lock (_activePoolsLock)
		{
			pools = [.. _activePools];
		}

		var cliInstances = 0;
		var activeSessions = 0;
		foreach (var pool in pools)
		{
			try
			{
				var snapshot = pool.GetSnapshot();
				cliInstances += snapshot.CliInstances;
				activeSessions += snapshot.ActiveSessions;
			}
			catch (ObjectDisposedException)
			{
				// Scope disposal removes pools from the set; tolerate a racing status poll.
			}
		}

		return new AgentRuntimeStatus("copilot", pools.Length, cliInstances, activeSessions);
	}

	/// <summary>
	/// Gets the active run-scoped client. Throws if no <see cref="CreateRunScopeAsync"/>
	/// is currently active on the calling ExecutionContext. Every agent build MUST happen
	/// inside a per-run scope — there is no fallback shared CLI process by design.
	/// </summary>
	private Task<ICopilotClientPool> GetActivePoolAsync(CancellationToken cancellationToken)
	{
		_ = cancellationToken;
		var holder = _runScopedClient.Value;
		var pool = holder?.Pool;
		LogActiveClientCheck(pool is null ? "null" : pool.Diagnostic, Environment.CurrentManagedThreadId);
		if (pool is null)
		{
			LogBuildAgentOutsideScope(Environment.CurrentManagedThreadId, Environment.StackTrace);
			throw new InvalidOperationException(
				"BuildAgentAsync was called outside an active CreateRunScopeAsync. " +
				"Every Copilot agent build must happen inside a per-orchestration run scope " +
				"so each orchestration gets its own CLI process. " +
				"Open a scope with `await using var scope = await builder.CreateRunScopeAsync(...)` " +
				"or call BuildAgentAsync from within OrchestrationExecutor.ExecuteAsync.");
		}

		LogActiveClientResolved("run-scoped", pool.Diagnostic, Environment.CurrentManagedThreadId);
		return Task.FromResult<ICopilotClientPool>(pool);
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
		var excludedTools = ExcludedTools;

		var pool = await GetActivePoolAsync(cancellationToken).ConfigureAwait(false);

		return new CopilotAgent(
			clientPool: pool,
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
			swapOptions: CopilotAgentSwapOptions.FromPoolOptions(_poolOptions),
			logger: _loggerFactory.CreateLogger<CopilotAgent>(),
			loggerFactory: _loggerFactory,
			excludedTools: excludedTools
		);
	}

	public override async Task<IAgent> BuildAgentAsync(AgentBuildConfig config, CancellationToken cancellationToken = default)
	{
		var pool = await GetActivePoolAsync(cancellationToken).ConfigureAwait(false);

		return new CopilotAgent(
			clientPool: pool,
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
			swapOptions: CopilotAgentSwapOptions.FromPoolOptions(_poolOptions),
			logger: _loggerFactory.CreateLogger<CopilotAgent>(),
			loggerFactory: _loggerFactory,
			excludedTools: config.ExcludedTools,
			reasoningSummary: config.ReasoningSummary,
			contextTier: config.ContextTier,
			workingDirectory: config.WorkingDirectory,
			gitHubToken: config.GitHubToken,
			humanInput: config.HumanInput,
			permissionPolicy: config.PermissionPolicy
		);
	}

	public ValueTask DisposeAsync()
	{
		// No process-wide resources to clean up: each orchestration run owns its CopilotClientPool
		// via its RunScope and disposes it when the scope ends.
		GC.SuppressFinalize(this);
		return ValueTask.CompletedTask;
	}

	private void RegisterActivePool(CopilotClientPool pool)
	{
		lock (_activePoolsLock)
		{
			_activePools.Add(pool);
		}
	}

	private void UnregisterActivePool(CopilotClientPool pool)
	{
		lock (_activePoolsLock)
		{
			_activePools.Remove(pool);
		}
	}

	/// <summary>
	/// Manages the lifecycle of a per-run CopilotClientPool.
	/// Disposing the scope stops and disposes all clients owned by the pool.
	/// </summary>
	private sealed class RunScope : IAsyncDisposable
	{
		private readonly CopilotAgentBuilder _builder;
		private readonly PoolHolder _holder;
		private readonly CopilotClientPool _pool;
		private readonly int _scopeId;

		public RunScope(CopilotAgentBuilder builder, PoolHolder holder, CopilotClientPool pool, int scopeId)
		{
			_builder = builder;
			_holder = holder;
			_pool = pool;
			_scopeId = scopeId;
		}

		public async ValueTask DisposeAsync()
		{
			_builder.LogRunScopeDisposing(_scopeId, _pool.Diagnostic, Environment.CurrentManagedThreadId);

			// Clear the holder's pool field so any stragglers see a null run-scoped pool
			// and now correctly fail fast (no fallback path remains).
			_holder.Pool = null;

			var sw = System.Diagnostics.Stopwatch.StartNew();
			try { await _pool.DisposeAsync().ConfigureAwait(false); }
			catch (Exception ex) { _builder.LogRunScopeDisposeError(ex, _scopeId); }
			finally { _builder.UnregisterActivePool(_pool); }

			_builder.LogRunScopeDisposed(_scopeId, sw.ElapsedMilliseconds);
		}
	}

	#region Source-Generated Logging

	[LoggerMessage(EventId = 100, Level = LogLevel.Information,
		Message = "RunScope#{ScopeId}: creating run-scoped Copilot CLI pool (thread={ThreadId})")]
	private partial void LogRunScopeCreating(int scopeId, int threadId);

	[LoggerMessage(EventId = 101, Level = LogLevel.Information,
		Message = "RunScope#{ScopeId}: created run-scoped Copilot CLI pool in {ElapsedMs}ms ({PoolDiagnostic})")]
	private partial void LogRunScopeCreated(int scopeId, long elapsedMs, string poolDiagnostic);

	[LoggerMessage(EventId = 102, Level = LogLevel.Error,
		Message = "RunScope#{ScopeId}: failed to start CLI client after {ElapsedMs}ms")]
	private partial void LogRunScopeStartFailed(Exception ex, int scopeId, long elapsedMs);

	[LoggerMessage(EventId = 103, Level = LogLevel.Information,
		Message = "RunScope#{ScopeId}: disposing ({PoolDiagnostic}, thread={ThreadId})")]
	private partial void LogRunScopeDisposing(int scopeId, string poolDiagnostic, int threadId);

	[LoggerMessage(EventId = 104, Level = LogLevel.Information,
		Message = "RunScope#{ScopeId}: disposed in {ElapsedMs}ms")]
	private partial void LogRunScopeDisposed(int scopeId, long elapsedMs);

	[LoggerMessage(EventId = 106, Level = LogLevel.Warning,
		Message = "RunScope#{ScopeId}: error disposing CLI pool")]
	private partial void LogRunScopeDisposeError(Exception ex, int scopeId);

	[LoggerMessage(EventId = 107, Level = LogLevel.Debug,
		Message = "BuildAgent: resolved {ClientKind} pool ({PoolDiagnostic}, thread={ThreadId})")]
	private partial void LogActiveClientResolved(string clientKind, string poolDiagnostic, int threadId);

	[LoggerMessage(EventId = 110, Level = LogLevel.Debug,
		Message = "BuildAgent: AsyncLocal _runScopedClient.Value = {ClientValue} on thread {ThreadId}")]
	private partial void LogActiveClientCheck(string clientValue, int threadId);

	[LoggerMessage(EventId = 111, Level = LogLevel.Debug,
		Message = "RunScope#{ScopeId}: post-set check, _runScopedClient.Value = {ClientValue} on thread {ThreadId}")]
	private partial void LogRunScopeAsyncLocalCheck(int scopeId, string clientValue, int threadId);

	[LoggerMessage(EventId = 108, Level = LogLevel.Error,
		Message = "BuildAgent: NO RUN SCOPE active on thread {ThreadId} — refusing to build agent. Open a CreateRunScopeAsync first. Stack:\n{StackTrace}")]
	private partial void LogBuildAgentOutsideScope(int threadId, string stackTrace);

	#endregion
}
