using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Orchestra.Engine;

namespace Orchestra.OpenCode;

/// <summary>
/// <see cref="AgentBuilder"/> implementation backed by OpenCode servers. Each orchestration run
/// gets its own <see cref="OpenCodeServerPool"/> via <see cref="CreateRunScopeAsync"/>; prompt
/// steps acquire leases from that pool. Mirrors <c>CopilotAgentBuilder</c>: the per-run pool is
/// stored in an <see cref="AsyncLocal{T}"/> holder so concurrent runs stay isolated, and
/// <see cref="BuildAgentAsync(AgentBuildConfig, CancellationToken)"/> fails fast outside a scope.
/// </summary>
public sealed partial class OpenCodeAgentBuilder : AgentBuilder, IAsyncDisposable
{
	private readonly ILoggerFactory _loggerFactory;
	private readonly ILogger<OpenCodeAgentBuilder> _logger;
	private readonly OpenCodeAgentPoolOptions _options;
	private readonly IOpenCodeClientFactory _clientFactory;
	private readonly object _activePoolsLock = new();
	private readonly HashSet<OpenCodeServerPool> _activePools = [];
	private readonly AsyncLocal<PoolHolder?> _runScopedPool = new();
	private static int s_scopeCounter;

	private sealed class PoolHolder
	{
		public OpenCodeServerPool? Pool;
	}

	public OpenCodeAgentBuilder(ILoggerFactory? loggerFactory = null, OpenCodeAgentPoolOptions? options = null)
		: this(loggerFactory, options, new OpenCodeHttpClientFactory())
	{
	}

	internal OpenCodeAgentBuilder(ILoggerFactory? loggerFactory, OpenCodeAgentPoolOptions? options, IOpenCodeClientFactory clientFactory)
	{
		_loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
		_logger = _loggerFactory.CreateLogger<OpenCodeAgentBuilder>();
		_options = options ?? new OpenCodeAgentPoolOptions();
		_clientFactory = clientFactory;
	}

	public override Task<IAsyncDisposable> CreateRunScopeAsync(AgentPoolConfig? agentPool = null, CancellationToken cancellationToken = default)
	{
		// CRITICAL (same rationale as CopilotAgentBuilder): install the AsyncLocal holder
		// synchronously before any await so the per-run pool is visible to the step-execution
		// continuation on this ExecutionContext.
		var holder = new PoolHolder();
		_runScopedPool.Value = holder;
		return CreateRunScopeAsyncCore(holder, agentPool, cancellationToken);
	}

	private async Task<IAsyncDisposable> CreateRunScopeAsyncCore(PoolHolder holder, AgentPoolConfig? agentPool, CancellationToken cancellationToken)
	{
		var scopeId = Interlocked.Increment(ref s_scopeCounter);
		var pool = new OpenCodeServerPool(agentPool, _options, _clientFactory, _loggerFactory);
		try
		{
			await pool.PrewarmAsync(cancellationToken).ConfigureAwait(false);
		}
		catch
		{
			try { await pool.DisposeAsync().ConfigureAwait(false); } catch { }
			holder.Pool = null;
			throw;
		}

		holder.Pool = pool;
		lock (_activePoolsLock) { _activePools.Add(pool); }
		LogRunScopeCreated(scopeId, pool.Diagnostic);
		return new RunScope(this, holder, pool);
	}

	public override string? GetRunScopedClientDiagnostic() => _runScopedPool.Value?.Pool?.Diagnostic;

	public override AgentRuntimeStatus GetRuntimeStatus()
	{
		OpenCodeServerPool[] pools;
		lock (_activePoolsLock) { pools = [.. _activePools]; }

		var instances = 0;
		var sessions = 0;
		foreach (var pool in pools)
		{
			try
			{
				var snapshot = pool.GetSnapshot();
				instances += snapshot.Instances;
				sessions += snapshot.ActiveSessions;
			}
			catch (ObjectDisposedException)
			{
			}
		}

		return new AgentRuntimeStatus("opencode", pools.Length, instances, sessions);
	}

	private OpenCodeServerPool GetActivePool()
	{
		var pool = _runScopedPool.Value?.Pool;
		if (pool is null)
		{
			throw new InvalidOperationException(
				"BuildAgentAsync was called outside an active CreateRunScopeAsync. Every OpenCode agent " +
				"build must happen inside a per-orchestration run scope so each run gets its own server pool. " +
				"Open a scope with `await using var scope = await builder.CreateRunScopeAsync(...)` or build " +
				"from within OrchestrationExecutor.ExecuteAsync.");
		}
		return pool;
	}

	public override Task<IAgent> BuildAgentAsync(AgentBuildConfig config, CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(config.Model, nameof(config.Model));
		var pool = GetActivePool();
		IAgent agent = new OpenCodeAgent(pool, _options, _clientFactory, _loggerFactory, config);
		return Task.FromResult(agent);
	}

	/// <summary>
	/// OpenCode honors the model, system prompt (Replace semantics — the universal baseline),
	/// MCP servers, inline sub-agents, reasoning level, working directory, skill directories,
	/// engine tools, attachments, human-input routing, and permission policy. Copilot-specific
	/// knobs (reasoning summary, context tier, GitHub token, sandbox policy, Append/Customize
	/// system-prompt modes + sections, infinite sessions, excluded tools) are not supported and
	/// are reported as warnings when a step requests them.
	/// </summary>
	public override AgentProviderCapabilities GetCapabilities() => new()
	{
		Provider = "opencode",
		Mcps = true,
		Subagents = true,
		ReasoningLevel = true,
		WorkingDirectory = true,
		SkillDirectories = true,
		EngineTools = true,
		Attachments = true,
		HumanInput = true,
		PermissionPolicy = true,
	};

	public override Task<IAgent> BuildAgentAsync(CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(Model, nameof(Model));
		var config = new AgentBuildConfig
		{
			Model = Model!,
			SystemPrompt = SystemPrompt,
			Mcps = Mcps,
			Subagents = Subagents,
			ReasoningLevel = ReasoningLevel,
			SystemPromptMode = SystemPromptMode,
			Reporter = Reporter,
			EngineTools = EngineTools,
			EngineToolCtx = EngineToolCtx,
			SkillDirectories = SkillDirectories,
			InfiniteSessionConfig = InfiniteSession,
			Attachments = Attachments,
			ExcludedTools = ExcludedTools,
		};
		return BuildAgentAsync(config, cancellationToken);
	}

	public ValueTask DisposeAsync()
	{
		// Each run owns its pool via its RunScope and disposes it when the scope ends.
		GC.SuppressFinalize(this);
		return ValueTask.CompletedTask;
	}

	private void UnregisterActivePool(OpenCodeServerPool pool)
	{
		lock (_activePoolsLock) { _activePools.Remove(pool); }
	}

	private sealed class RunScope(OpenCodeAgentBuilder builder, PoolHolder holder, OpenCodeServerPool pool) : IAsyncDisposable
	{
		public async ValueTask DisposeAsync()
		{
			holder.Pool = null;
			try { await pool.DisposeAsync().ConfigureAwait(false); }
			finally { builder.UnregisterActivePool(pool); }
		}
	}

	[LoggerMessage(EventId = 230, Level = LogLevel.Information, Message = "OpenCode run scope #{ScopeId} created ({PoolDiagnostic})")]
	private partial void LogRunScopeCreated(int scopeId, string poolDiagnostic);
}
