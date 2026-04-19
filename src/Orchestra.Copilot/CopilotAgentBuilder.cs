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
		/// <summary>
		/// Cached available model info from the last ListModelsAsync call within this run.
		/// Lives on the holder (not the singleton builder) so concurrent runs cannot
		/// stomp on each other's cache.
		/// </summary>
		public IReadOnlyList<AvailableModelInfo>? CachedAvailableModels;

		/// <summary>
		/// Per-run fault broker. When one session on this client errors out, the broker
		/// probes the CLI; if the CLI is unhealthy, all other in-flight sessions on this
		/// client are faulted with <see cref="CopilotClientUnhealthyException"/> so they
		/// fail fast instead of waiting for their per-step timeout.
		/// Created at the same time as <see cref="Client"/> in CreateRunScopeAsyncCore.
		/// </summary>
		public SessionFaultBroker? FaultBroker;
	}

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
		holder.FaultBroker = new SessionFaultBroker(
			scopeId,
			probe: ct => ProbeClientHealthAsync(client, scopeId, ct),
			logger: _loggerFactory.CreateLogger<SessionFaultBroker>());
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

	/// <summary>
	/// Gets the active run-scoped client. Throws if no <see cref="CreateRunScopeAsync"/>
	/// is currently active on the calling ExecutionContext. Every agent build MUST happen
	/// inside a per-run scope — there is no fallback shared CLI process by design.
	/// </summary>
	private Task<CopilotClient> GetActiveClientAsync(CancellationToken cancellationToken)
	{
		_ = cancellationToken;
		var holder = _runScopedClient.Value;
		var client = holder?.Client;
		LogActiveClientCheck(client is null ? "null" : client.GetHashCode().ToString(), Environment.CurrentManagedThreadId);
		if (client is null)
		{
			LogBuildAgentOutsideScope(Environment.CurrentManagedThreadId, Environment.StackTrace);
			throw new InvalidOperationException(
				"BuildAgentAsync was called outside an active CreateRunScopeAsync. " +
				"Every Copilot agent build must happen inside a per-orchestration run scope " +
				"so each orchestration gets its own CLI process. " +
				"Open a scope with `await using var scope = await builder.CreateRunScopeAsync(...)` " +
				"or call BuildAgentAsync from within OrchestrationExecutor.ExecuteAsync.");
		}

		LogActiveClientResolved("run-scoped", client.GetHashCode(), Environment.CurrentManagedThreadId);
		return Task.FromResult(client);
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

		var holder = _runScopedClient.Value!; // GetActiveClientAsync threw if null
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
			cachedAvailableModels: holder.CachedAvailableModels,
			onAvailableModelsListed: models => holder.CachedAvailableModels = models,
			faultBroker: holder.FaultBroker,
			logger: _loggerFactory.CreateLogger<CopilotAgent>(),
			loggerFactory: _loggerFactory
		);
	}

	public override async Task<IAgent> BuildAgentAsync(AgentBuildConfig config, CancellationToken cancellationToken = default)
	{
		var holder = _runScopedClient.Value!; // GetActiveClientAsync below throws if null
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
			cachedAvailableModels: holder.CachedAvailableModels,
			onAvailableModelsListed: models => holder.CachedAvailableModels = models,
			faultBroker: holder.FaultBroker,
			logger: _loggerFactory.CreateLogger<CopilotAgent>(),
			loggerFactory: _loggerFactory
		);
	}

	public ValueTask DisposeAsync()
	{
		// No process-wide resources to clean up: each orchestration run owns its CopilotClient
		// via its RunScope and disposes it when the scope ends.
		GC.SuppressFinalize(this);
		return ValueTask.CompletedTask;
	}

	/// <summary>
	/// Health probe used by <see cref="SessionFaultBroker"/>. Sends a short-deadline ping
	/// and inspects the SDK <see cref="CopilotClient.State"/>. The CLI is considered
	/// healthy only when both succeed within the deadline.
	/// </summary>
	private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

	private async Task<ProbeResult> ProbeClientHealthAsync(CopilotClient client, int scopeId, CancellationToken cancellationToken)
	{
		using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		probeCts.CancelAfter(ProbeTimeout);

		var state = client.State.ToString();
		LogProbeAttempt(scopeId, state);

		try
		{
			var pingSw = System.Diagnostics.Stopwatch.StartNew();
			_ = await client.PingAsync("orchestra-health-probe", probeCts.Token).ConfigureAwait(false);
			pingSw.Stop();

			var stateAfter = client.State;
			if (stateAfter != ConnectionState.Connected)
			{
				return new ProbeResult(false, $"ping ok in {pingSw.ElapsedMilliseconds}ms but state={stateAfter}");
			}

			return new ProbeResult(true, $"ping ok in {pingSw.ElapsedMilliseconds}ms, state=Connected");
		}
		catch (OperationCanceledException) when (probeCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
		{
			return new ProbeResult(false, $"ping timed out after {ProbeTimeout.TotalSeconds}s, state={client.State}");
		}
		catch (Exception ex)
		{
			return new ProbeResult(false, $"ping threw {ex.GetType().Name}: {ex.Message}, state={client.State}");
		}
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
			// and now correctly fail fast (no fallback path remains). The CachedAvailableModels
			// lives on the holder itself and is naturally GCed when the holder is dropped.
			_holder.Client = null;

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

	[LoggerMessage(EventId = 108, Level = LogLevel.Error,
		Message = "BuildAgent: NO RUN SCOPE active on thread {ThreadId} — refusing to build agent. Open a CreateRunScopeAsync first. Stack:\n{StackTrace}")]
	private partial void LogBuildAgentOutsideScope(int threadId, string stackTrace);

	[LoggerMessage(EventId = 112, Level = LogLevel.Debug,
		Message = "RunScope#{ScopeId}: probing CLI client health (currentState={State})")]
	private partial void LogProbeAttempt(int scopeId, string state);

	#endregion
}
