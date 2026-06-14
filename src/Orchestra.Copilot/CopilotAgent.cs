using System.Threading.Channels;
using GitHub.Copilot;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Orchestra.Engine;

namespace Orchestra.Copilot;

public partial class CopilotAgent : IAgent
{
	private readonly ICopilotClientPool _clientPool;
	private readonly string _model;
	private readonly string? _systemPrompt;
	private readonly Mcp[] _mcps;
	private readonly Subagent[] _subagents;
	private readonly ReasoningLevel? _reasoningLevel;
	private readonly Engine.ReasoningSummaryLevel? _reasoningSummary;
	private readonly Engine.ContextTier? _contextTier;
	private readonly string? _workingDirectory;
	private readonly string? _gitHubToken;
	private readonly bool _humanInput;
	private readonly Engine.PermissionPolicy? _permissionPolicy;
	/// <summary>Serializes human-approval permission waits so they don't collide on the per-step waiter key.</summary>
	private readonly SemaphoreSlim _permissionGate = new(1, 1);
	private readonly SystemPromptMode? _systemPromptMode;
	private readonly Dictionary<string, SystemPromptSectionOverride>? _systemPromptSections;
	private readonly IOrchestrationReporter _reporter;
	private readonly IReadOnlyCollection<IEngineTool> _engineTools;
	private readonly EngineToolContext? _engineToolContext;
	private readonly string[] _skillDirectories;
	private readonly Engine.InfiniteSessionConfig? _infiniteSessionConfig;
	private readonly ImageAttachment[] _attachments;
	/// <summary>
	/// SDK 1.0.0 PR #1098: tool names removed from the main agent's catalog.
	/// Forwarded to DefaultAgentConfig.ExcludedTools in BuildSessionConfig.
	/// </summary>
	private readonly string[] _excludedTools;
	private readonly CopilotAgentSwapOptions _swapOptions;
	private readonly ILogger<CopilotAgent> _logger;
	private readonly ILoggerFactory _loggerFactory;

	internal CopilotAgent(
			CopilotClient client,
			string model,
			string? systemPrompt,
			Mcp[] mcps,
			Subagent[] subagents,
			ReasoningLevel? reasoningLevel,
			SystemPromptMode? systemPromptMode,
			Dictionary<string, SystemPromptSectionOverride>? systemPromptSections,
			IOrchestrationReporter reporter,
			IReadOnlyCollection<IEngineTool> engineTools,
			EngineToolContext? engineToolContext,
			string[] skillDirectories,
			Engine.InfiniteSessionConfig? infiniteSessionConfig,
			ImageAttachment[] attachments,
			ILogger<CopilotAgent> logger,
			ILoggerFactory? loggerFactory = null,
			string[]? excludedTools = null)
		: this(
			clientPool: new FixedCopilotClientPool(new CopilotSdkClientAdapter(client, ownsClient: false)),
			model,
			systemPrompt,
			mcps,
			subagents,
			reasoningLevel,
			systemPromptMode,
			systemPromptSections,
			reporter,
			engineTools,
			engineToolContext,
			skillDirectories,
			infiniteSessionConfig,
			attachments,
			swapOptions: null,
			logger,
			loggerFactory,
			excludedTools)
	{
	}

	internal CopilotAgent(
			ICopilotClientPool clientPool,
			string model,
			string? systemPrompt,
			Mcp[] mcps,
			Subagent[] subagents,
			ReasoningLevel? reasoningLevel,
			SystemPromptMode? systemPromptMode,
			Dictionary<string, SystemPromptSectionOverride>? systemPromptSections,
			IOrchestrationReporter reporter,
			IReadOnlyCollection<IEngineTool> engineTools,
			EngineToolContext? engineToolContext,
			string[] skillDirectories,
			Engine.InfiniteSessionConfig? infiniteSessionConfig,
			ImageAttachment[] attachments,
			ILogger<CopilotAgent> logger,
			ILoggerFactory? loggerFactory = null,
			string[]? excludedTools = null)
		: this(
			clientPool,
			model,
			systemPrompt,
			mcps,
			subagents,
			reasoningLevel,
			systemPromptMode,
			systemPromptSections,
			reporter,
			engineTools,
			engineToolContext,
			skillDirectories,
			infiniteSessionConfig,
			attachments,
			swapOptions: null,
			logger,
			loggerFactory,
			excludedTools)
	{
	}

	internal CopilotAgent(
			ICopilotClientPool clientPool,
			string model,
			string? systemPrompt,
			Mcp[] mcps,
			Subagent[] subagents,
			ReasoningLevel? reasoningLevel,
			SystemPromptMode? systemPromptMode,
			Dictionary<string, SystemPromptSectionOverride>? systemPromptSections,
			IOrchestrationReporter reporter,
			IReadOnlyCollection<IEngineTool> engineTools,
			EngineToolContext? engineToolContext,
			string[] skillDirectories,
			Engine.InfiniteSessionConfig? infiniteSessionConfig,
			ImageAttachment[] attachments,
			CopilotAgentSwapOptions? swapOptions,
			ILogger<CopilotAgent> logger,
			ILoggerFactory? loggerFactory = null,
			string[]? excludedTools = null,
			Engine.ReasoningSummaryLevel? reasoningSummary = null,
			Engine.ContextTier? contextTier = null,
			string? workingDirectory = null,
			string? gitHubToken = null,
			bool humanInput = false,
			Engine.PermissionPolicy? permissionPolicy = null)
	{
		_clientPool = clientPool;
		_model = model;
		_systemPrompt = systemPrompt;
		_mcps = mcps;
		_subagents = subagents;
		_reasoningLevel = reasoningLevel;
		_reasoningSummary = reasoningSummary;
		_contextTier = contextTier;
		_workingDirectory = workingDirectory;
		_gitHubToken = gitHubToken;
		_humanInput = humanInput;
		_permissionPolicy = permissionPolicy;
		_systemPromptMode = systemPromptMode;
		_systemPromptSections = systemPromptSections;
		_reporter = reporter;
		_engineTools = engineTools;
		_engineToolContext = engineToolContext;
		_skillDirectories = skillDirectories;
		_infiniteSessionConfig = infiniteSessionConfig;
		_attachments = attachments;
		// SDK 1.0.0 PR #1098: forwarded to DefaultAgentConfig.ExcludedTools in
		// BuildSessionConfig / BuildResumeSessionConfig. Default-empty so existing
		// agents (none of which pass this) behave exactly as they did pre-1.0.
		_excludedTools = excludedTools ?? [];
		_swapOptions = swapOptions ?? CopilotAgentSwapOptions.Defaults;
		_logger = logger;
		_loggerFactory = loggerFactory ?? Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance;
	}

	public AgentTask SendAsync(string prompt, CancellationToken cancellationToken = default)
	{
		var channel = Channel.CreateUnbounded<AgentEvent>();
		var resultTask = RunSessionAsync(prompt, channel.Writer, cancellationToken);
		return new AgentTask(channel.Reader, resultTask);
	}

	/// <summary>
	/// Drives the full prompt-step lifecycle, including the CLI-swap-and-resume recovery
	/// loop. On a transport-class failure (broker latched the worker unhealthy, or the
	/// CLI's own retry budget was exhausted) the loop abandons the dying CLI, acquires
	/// a fresh worker, and either resumes the prior session (Phase 2) or cold-restarts
	/// (re-send the original prompt). Budget is bounded by
	/// <see cref="CopilotAgentSwapOptions.CliSwapBudgetPerStep"/>; non-recoverable errors
	/// short-circuit immediately.
	/// </summary>
	private async Task<AgentResult> RunSessionAsync(
			string prompt,
			ChannelWriter<AgentEvent> writer,
			CancellationToken cancellationToken)
	{
		try
		{
			string? priorSessionId = null;
			int swapAttempt = 0;

			while (true)
			{
				// Box that the inner attempt updates as soon as CreateSession/Resume succeeds.
				// We can't rely on the exception's TriggeringSessionId because the fault
				// broker may have been latched by a SIBLING session (e.g. another concurrent
				// step on the same CLI) — that id belongs to someone else and must NOT
				// become this step's resume target.
				var attemptSessionIdBox = new SessionIdBox();

				try
				{
					return await RunOneAttemptAsync(
						prompt,
						writer,
						priorSessionId,
						swapAttempt,
						attemptSessionIdBox,
						cancellationToken).ConfigureAwait(false);
				}
				catch (OperationCanceledException)
				{
					throw;
				}
				catch (Exception ex) when (TryClassifySwapEligibleFailure(ex, out var reason))
				{
					// We saw a CLI-class failure. Decide whether we still have budget,
					// and figure out the recovery mode (resume vs cold restart).
					var attemptedSessionId = attemptSessionIdBox.Value ?? priorSessionId;
					if (swapAttempt >= _swapOptions.CliSwapBudgetPerStep)
					{
						LogSwapBudgetExhausted(
							attemptedSessionId ?? "(none)",
							swapAttempt,
							_swapOptions.CliSwapBudgetPerStep,
							reason);
						throw;
					}

					swapAttempt++;
					// resume_locked / resume_session_missing both mean the prior session can't
					// be replayed (lock contention or the CLI no longer has the session id).
					// Force a cold restart so we don't loop on the same dead id; everything
					// else honours the resume policy.
					var nextMode = reason is "resume_locked" or "resume_session_missing"
						? SwapMode.ColdRestart
						: ResolveSwapMode(attemptedSessionId);
					LogSwapTriggered(
						attemptedSessionId ?? "(none)",
						swapAttempt,
						_swapOptions.CliSwapBudgetPerStep,
						reason,
						nextMode);

					EmitSwapEvent(
						writer,
						priorSessionId: attemptedSessionId,
						swapAttempt: swapAttempt,
						swapBudget: _swapOptions.CliSwapBudgetPerStep,
						reason: reason,
						mode: nextMode);

					_clientPool.RecordSwapTriggered();
					_reporter.ReportCliSwapTriggered(
						_stepName,
						priorSessionId: attemptedSessionId,
						swapAttempt: swapAttempt,
						swapBudget: _swapOptions.CliSwapBudgetPerStep,
						reason: reason,
						mode: nextMode == SwapMode.Resume ? "resume" : "cold_restart");

					priorSessionId = nextMode == SwapMode.Resume ? attemptedSessionId : null;
					// Loop back for another attempt on a fresh worker.
				}
			}
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		finally
		{
			writer.TryComplete();
		}
	}

	/// <summary>
	/// One pass of acquire-lease → create-or-resume-session → send → await-completion.
	/// Throws on any failure; the outer <see cref="RunSessionAsync"/> classifies the
	/// exception and either retries (CLI-class failures, budget permitting) or rethrows
	/// (non-recoverable failures, cancellation, or budget exhausted).
	/// </summary>
	/// <param name="attemptSessionIdBox">Mutable wrapper the inner attempt populates as
	/// soon as it has issued (or resumed) a session id, so the outer swap loop knows
	/// which session id is OURS — distinct from any sibling session that may have
	/// latched the fault broker first.</param>
	private async Task<AgentResult> RunOneAttemptAsync(
		string prompt,
		ChannelWriter<AgentEvent> writer,
		string? priorSessionId,
		int swapAttempt,
		SessionIdBox attemptSessionIdBox,
		CancellationToken cancellationToken)
	{
		ICopilotClientLease? lease = null;
		try
		{
			lease = await _clientPool.AcquireAsync(cancellationToken).ConfigureAwait(false);
			var client = lease.Client;
			var faultBroker = lease.FaultBroker;

			// Fast-fail: if a sibling session has already declared this CLI client unhealthy,
			// don't even attempt CreateSessionAsync — the JSON-RPC call would just hang or
			// throw "connection lost" anyway. Surface a loud, structured exception so the
			// swap loop classifies it as transport_lost and tries again on a fresh worker.
			if (faultBroker?.IsClientUnhealthy == true)
			{
				LogSessionSkippedClientUnhealthy(
					client.DiagnosticHash,
					faultBroker.UnhealthyTriggeringSessionId ?? "(unknown)",
					faultBroker.UnhealthyReason ?? "(no details)");

				throw new CopilotClientUnhealthyException(
					triggeringSessionId: faultBroker.UnhealthyTriggeringSessionId ?? "(unknown)",
					triggeringFailureReason: faultBroker.UnhealthyTriggeringFailureReason ?? "(unknown)",
					probeDetails: faultBroker.UnhealthyReason,
					message: $"Copilot CLI client is unhealthy and will not be used. " +
							 $"First failure: session '{faultBroker.UnhealthyTriggeringSessionId ?? "(unknown)"}' " +
							 $"({faultBroker.UnhealthyTriggeringFailureReason ?? "(unknown)"}). " +
							 $"Probe: {faultBroker.UnhealthyReason ?? "(no details)"}.");
			}

			LogMcpConfiguration();

			var isResumeAttempt = swapAttempt > 0
				&& priorSessionId is not null
				&& _swapOptions.ResumeOnSwapEnabled;

			// SDK 1.0.0: construct the handler + completion source BEFORE the config so we
			// can wire the handler in via SessionConfig.OnEvent. The old pattern called
			// session.On(handler.HandleEvent) right after CreateSessionAsync returned, which
			// left a tiny window where events emitted by the runtime between session
			// materialisation and our subscription were dropped. With OnEvent set at config
			// time the runtime invokes the handler from the first event onward.
			var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			var handler = new CopilotSessionHandler(writer, _reporter, _model, done, _loggerFactory.CreateLogger<CopilotSessionHandler>());

			var attemptSessionConfig = BuildSessionConfig(handler.HandleEvent, cancellationToken);

			LogSessionCreating(
				client.DiagnosticHash,
				_model,
				_mcps.Length,
				Environment.CurrentManagedThreadId,
				isResumeAttempt ? "resume" : "create",
				priorSessionId ?? "(none)");

			var sw = System.Diagnostics.Stopwatch.StartNew();
			ICopilotSession session;
			try
			{
				if (isResumeAttempt)
				{
					var resumeConfig = BuildResumeSessionConfig(attemptSessionConfig, handler.HandleEvent);
					session = await client.ResumeSessionAsync(priorSessionId!, resumeConfig, cancellationToken)
						.ConfigureAwait(false);
				}
				else
				{
					session = await client.CreateSessionAsync(attemptSessionConfig, cancellationToken)
						.ConfigureAwait(false);
				}
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
				throw;
			}
			catch (Exception ex)
			{
				LogSessionCreateFailed(
					ex,
					client.DiagnosticHash,
					sw.ElapsedMilliseconds,
					isResumeAttempt ? "resume" : "create");
				await ProbeAfterSdkFailureAsync(
					faultBroker,
					failedSessionId: isResumeAttempt
						? $"(session-resume:{priorSessionId})"
						: "(session-create)",
					failureReason: $"{(isResumeAttempt ? "ResumeSessionAsync" : "CreateSessionAsync")} failed: {ex.Message}").ConfigureAwait(false);

				// If the probe latched the broker, surface a structured unhealthy exception
				// so the outer swap loop can route this attempt to a fresh worker. Otherwise
				// rethrow the SDK's exception unchanged.
				if (faultBroker?.IsClientUnhealthy == true)
				{
					throw NewClientUnhealthyFromBroker(faultBroker, ex,
						triggeringSessionId: priorSessionId ?? "(session-create)");
				}

				// Resume-specific fallback: if ResumeSessionAsync failed because the CLI no
				// longer knows about the prior session id ("Session not found"), the worker
				// itself is fine — the saved session just isn't replayable anymore (e.g. the
				// CLI was restarted/cleaned between our prior attempt and this one). Surface
				// a structured unhealthy exception with reason "resume_session_missing" so
				// the outer swap loop classifies it as swap-eligible and forces a cold
				// restart (priorSessionId=null) on the next attempt, re-running the step's
				// prompt from scratch instead of failing the whole orchestration.
				if (isResumeAttempt && IsResumeSessionMissing(ex))
				{
					LogResumeSessionMissingFallback(priorSessionId ?? "(unknown)", ex.Message);
					throw new CopilotClientUnhealthyException(
						triggeringSessionId: priorSessionId ?? "(unknown)",
						triggeringFailureReason: "resume_session_missing",
						probeDetails: ex.Message,
						message: $"Resume of session '{priorSessionId}' failed because the CLI no longer has that session; falling back to cold restart.");
				}

				throw;
			}
			LogSessionCreated(
				client.DiagnosticHash,
				sw.ElapsedMilliseconds,
				session.SessionId,
				isResumeAttempt ? "resume" : "create");
			// Tell the outer swap loop this is OUR session id (distinct from any sibling
			// that might have failed first on the same CLI). The classifier prefers this
			// over UnhealthyTriggeringSessionId for the resume target.
			attemptSessionIdBox.Value = session.SessionId;
			await using var _sessionDispose = session;

			// SDK 1.0.0: handler is already wired through SessionConfig.OnEvent (set when
			// the config was built above), so there is no session.On(...) call here. The
			// runtime is already invoking handler.HandleEvent for every event it emits.

			// Register this session with the per-worker fault broker so that, if a sibling
			// session on the same CopilotClient detects a CLI-level fault, this session
			// gets faulted too instead of waiting for its per-step timeout.
			using var _faultRegistration = faultBroker?.RegisterSession(
				session.SessionId,
				faultException => done.TrySetException(faultException));

			// On the resume path, wait briefly for the SDK's SessionResumeEvent so we can
			// detect an AlreadyInUse race against the dying CLI's lock and fall back to
			// cold restart if the lock isn't released within our grace window.
			if (isResumeAttempt && _swapOptions.ResumeAlreadyInUseWait > TimeSpan.Zero)
			{
				var resumeOutcome = await WaitForResumeOutcomeAsync(
					handler,
					sessionId: session.SessionId,
					cancellationToken).ConfigureAwait(false);

				if (resumeOutcome == ResumeOutcome.AlreadyInUseLocked)
				{
					// Abandon this resume attempt. Best-effort delete the on-disk session so
					// it doesn't pile up across runs, then throw a synthetic unhealthy
					// exception with the special "resume_locked" reason. The outer swap loop
					// catches that and re-enters with priorSessionId=null (cold restart).
					LogResumeAlreadyInUseFallback(session.SessionId, _swapOptions.ResumeAlreadyInUseWait);
					try
					{
						await client.DeleteSessionAsync(session.SessionId, CancellationToken.None)
							.ConfigureAwait(false);
					}
					catch (Exception delEx)
					{
						LogResumeDeleteFailed(delEx, session.SessionId);
					}

					throw new CopilotClientUnhealthyException(
						triggeringSessionId: session.SessionId,
						triggeringFailureReason: "resume_locked",
						probeDetails: $"SessionResumeEvent.AlreadyInUse=true persisted past {_swapOptions.ResumeAlreadyInUseWait.TotalSeconds:0.#}s grace window",
						message: $"Resumed session '{session.SessionId}' remained AlreadyInUse past the grace window; falling back to cold restart.");
				}
			}

			// Build message options with optional attachments
			var messageOptions = new MessageOptions { Prompt = prompt };
			if (_attachments.Length > 0)
			{
				messageOptions.Attachments = BuildAttachments();
			}

			// On a resume that succeeded (not AlreadyInUseLocked), the SDK has restored the
			// conversation. We still send the same prompt; the model continues from where
			// the previous turn left off. If the user wants a different behavior in future,
			// this is the place to gate it.
			try
			{
				LogSessionSendStarting(session.SessionId, _model, prompt.Length, _attachments.Length);
				var sendSw = System.Diagnostics.Stopwatch.StartNew();
				await session.SendAsync(messageOptions, cancellationToken).ConfigureAwait(false);
				LogSessionSendCompleted(session.SessionId, _model, sendSw.ElapsedMilliseconds);
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
				throw;
			}
			catch (Exception ex)
			{
				LogSessionSendFailed(ex, session.SessionId, _model);
				await ProbeAfterSdkFailureAsync(
					faultBroker,
					failedSessionId: session.SessionId,
					failureReason: $"SendAsync failed: {ex.Message}").ConfigureAwait(false);

				// Same idea as the CreateSessionAsync catch: if the probe latched the
				// broker, the right classification of this failure is "CLI is dead, retry
				// on a fresh worker", not "the SDK's specific InvalidOperationException".
				if (faultBroker?.IsClientUnhealthy == true)
				{
					throw NewClientUnhealthyFromBroker(faultBroker, ex,
						triggeringSessionId: session.SessionId);
				}
				throw;
			}

			using var registration = cancellationToken.Register(() =>
			{
				// Abort the in-flight message so the CLI stops processing
				_ = session.AbortAsync();
				done.TrySetCanceled(cancellationToken);
			});

			try
			{
				await done.Task.ConfigureAwait(false);
			}
			catch (CopilotSessionFailedException sessionEx) when (faultBroker is not null)
			{
				// A session-level error fired. Probe the CLI; if it's unhealthy, the broker
				// will fault all OTHER in-flight sessions on this client so they fast-fail
				// instead of hanging until their per-step timeout. We always re-throw the
				// original exception for THIS session — the broker's job is to defend its
				// siblings, not change our own outcome.
				await ProbeAfterSdkFailureAsync(
					faultBroker,
					failedSessionId: session.SessionId,
					failureReason: sessionEx.Message).ConfigureAwait(false);
				throw;
			}

			// Handle model mismatch detection and reporting
			var availableModels = await GetAvailableModelsAsync(client, lease, cancellationToken).ConfigureAwait(false);
			ReportModelMismatchIfNeeded(handler.ActualModel, availableModels);

			return new AgentResult
			{
				Content = handler.FinalContent ?? string.Empty,
				SelectedModel = handler.SelectedModel,
				ActualModel = handler.ActualModel,
				Usage = handler.Usage,
				// SDK 1.0.0: end-of-session billing summary projected from the structured
				// SessionShutdownEvent payload. Null when the session ended without a
				// shutdown event (e.g. SessionIdle-only path or a fault before any model
				// call); callers should treat null as "no roll-up available".
				FinalUsage = handler.ShutdownSummary,
				AvailableModels = availableModels,
				RequestedModelInfo = FindModelInfo(availableModels, _model),
				SelectedModelInfo = FindModelInfo(availableModels, handler.SelectedModel),
				ActualModelInfo = FindModelInfo(availableModels, handler.ActualModel),
			};
		}
		finally
		{
			if (lease is not null)
			{
				await lease.DisposeAsync().ConfigureAwait(false);
			}
		}
	}

	/// <summary>
	/// Recovery mode used by the swap loop. Resume preserves conversation history by
	/// reattaching to the prior session id on a fresh CLI worker; ColdRestart re-creates
	/// the session from scratch and re-sends the original prompt.
	/// </summary>
	private enum SwapMode
	{
		Resume,
		ColdRestart,
	}

	/// <summary>
	/// Outcome of waiting for the SDK's <c>SessionResumeEvent</c> on a resume attempt.
	/// </summary>
	private enum ResumeOutcome
	{
		/// <summary>Resume event arrived with AlreadyInUse=false (clean resume).</summary>
		Resumed,
		/// <summary>Resume event arrived with AlreadyInUse=true and never cleared inside the grace window.</summary>
		AlreadyInUseLocked,
		/// <summary>No resume event observed before the grace window expired; treat as clean (the SDK may simply not emit one when the session has no replayable state).</summary>
		NoEventObserved,
	}

	private SwapMode ResolveSwapMode(string? attemptedSessionId)
		=> _swapOptions.ResumeOnSwapEnabled && !string.IsNullOrEmpty(attemptedSessionId)
			? SwapMode.Resume
			: SwapMode.ColdRestart;

	/// <summary>
	/// Returns true if the exception from <c>ResumeSessionAsync</c> indicates the CLI no
	/// longer recognises the prior session id (typical SDK message:
	/// <c>"Communication error with Copilot CLI: Request session.resume failed with message: Session not found: &lt;guid&gt;"</c>).
	/// In that case the worker itself is fine; only the saved session is stale, so we
	/// can safely fall back to a cold restart of the same step.
	/// </summary>
	private static bool IsResumeSessionMissing(Exception ex)
	{
		var message = ex.Message ?? string.Empty;
		return message.Contains("Session not found", StringComparison.OrdinalIgnoreCase)
			|| message.Contains("session.resume failed", StringComparison.OrdinalIgnoreCase);
	}

	/// <summary>
	/// Classifies an exception thrown from <see cref="RunOneAttemptAsync"/> as a CLI-class
	/// swap-eligible failure. Returns true for: <see cref="CopilotClientUnhealthyException"/>
	/// (transport / fault-broker latched), <see cref="CopilotSessionFailedException"/> with
	/// the CLI's own "retried N times" exhaustion pattern, <see cref="CopilotSessionFailedException"/>
	/// with <c>Kind == AbnormalShutdown</c>, and <see cref="CopilotSessionFailedException"/>
	/// whose <see cref="AgentSessionErrorDetails.TransientUpstreamFailure"/> flag is set
	/// (5xx broker error, 403/permission_denied identity-handshake error, 429 rate limit).
	/// Returns false for everything else (validation errors, cancellation, plain model
	/// errors) so they propagate without being retried.
	/// </summary>
	private static bool TryClassifySwapEligibleFailure(Exception ex, out string reason)
	{
		reason = string.Empty;

		switch (ex)
		{
			case CopilotClientUnhealthyException unhealthy:
				reason = unhealthy.TriggeringFailureReason switch
				{
					"resume_locked" => "resume_locked",
					"resume_session_missing" => "resume_session_missing",
					_ => "transport_lost",
				};
				return true;

			case CopilotSessionFailedException sessionFailed:
				if (sessionFailed.Kind == CopilotSessionFailureKind.AbnormalShutdown)
				{
					reason = "abnormal_shutdown";
					return true;
				}
				if (sessionFailed.Details?.ExhaustedCliRetries == true)
				{
					reason = "cli_exhausted_retries";
					return true;
				}
				if (sessionFailed.Details?.TransientUpstreamFailure == true)
				{
					reason = "transient_upstream";
					return true;
				}
				return false;

			default:
				return false;
		}
	}

	/// <summary>
	/// Small mutable wrapper used to thread the session id of the current attempt back to
	/// the outer swap loop without throwing it through the exception. The outer loop reads
	/// <see cref="Value"/> after a failed attempt; resume swaps use it as the resume target.
	/// </summary>
	private sealed class SessionIdBox
	{
		public string? Value;
	}

	/// <summary>
	/// Waits for the SDK's <c>SessionResumeEvent</c> with a bounded grace window. When
	/// the event arrives with <c>AlreadyInUse=true</c> we poll up to
	/// <see cref="CopilotAgentSwapOptions.ResumeAlreadyInUseWait"/> for the flag to clear
	/// (the dying CLI typically releases the lock within seconds). If it never clears,
	/// returns <see cref="ResumeOutcome.AlreadyInUseLocked"/> so the caller can abandon
	/// the resume and cold-restart instead.
	/// </summary>
	private async Task<ResumeOutcome> WaitForResumeOutcomeAsync(
		CopilotSessionHandler handler,
		string sessionId,
		CancellationToken cancellationToken)
	{
		var deadline = DateTimeOffset.UtcNow + _swapOptions.ResumeAlreadyInUseWait;

		// First, wait briefly for the FIRST resume event (or no event at all — some sessions
		// don't emit one if there's nothing to replay).
		var firstWait = Task.WhenAny(
			handler.ResumeEventReceived,
			Task.Delay(_swapOptions.ResumeAlreadyInUseWait, cancellationToken));
		var winner = await firstWait.ConfigureAwait(false);
		if (winner != handler.ResumeEventReceived)
		{
			// Timed out without seeing the resume event. Treat as clean (the SDK didn't
			// promise we'd always get one; clean is safer than nuking the session here).
			return ResumeOutcome.NoEventObserved;
		}

		var data = await handler.ResumeEventReceived.ConfigureAwait(false);
		if (data.AlreadyInUse != true)
		{
			return ResumeOutcome.Resumed;
		}

		// AlreadyInUse=true. Poll a few times inside the remaining grace window. We can't
		// re-call ResumeSessionAsync without disposing/recreating, so we just observe the
		// handler's _lastResumeData (the SDK may emit subsequent SessionResumeEvent
		// notifications if internal state changes). In practice, AlreadyInUse remains true
		// for the whole resume, so this poll usually returns immediately with the locked
		// outcome — that's the trigger to fall back to cold restart.
		while (DateTimeOffset.UtcNow < deadline)
		{
			try
			{
				await Task.Delay(_swapOptions.ResumeAlreadyInUsePollInterval, cancellationToken).ConfigureAwait(false);
			}
			catch (OperationCanceledException)
			{
				throw;
			}

			var latest = handler.LastResumeData;
			if (latest is not null && latest.AlreadyInUse != true)
			{
				LogResumeAlreadyInUseCleared(sessionId);
				return ResumeOutcome.Resumed;
			}
		}

		return ResumeOutcome.AlreadyInUseLocked;
	}

	private static void EmitSwapEvent(
		ChannelWriter<AgentEvent> writer,
		string? priorSessionId,
		int swapAttempt,
		int swapBudget,
		string reason,
		SwapMode mode)
	{
		writer.TryWrite(new AgentEvent
		{
			Type = AgentEventType.CliInstanceSwapped,
			PriorSessionId = priorSessionId,
			SwapAttempt = swapAttempt,
			SwapBudget = swapBudget,
			SwapReason = reason,
			SwapMode = mode == SwapMode.Resume ? "resume" : "cold_restart",
		});
	}

	private async Task ProbeAfterSdkFailureAsync(
		ISessionFaultBroker? faultBroker,
		string failedSessionId,
		string failureReason)
	{
		if (faultBroker is null)
			return;

		try
		{
			_ = await faultBroker.ProbeAndMaybeFaultSiblingsAsync(
				failedSessionId,
				failureReason,
				CancellationToken.None).ConfigureAwait(false);
		}
		catch (Exception probeEx)
		{
			LogFaultBrokerProbeThrew(probeEx, failedSessionId);
		}
	}

	/// <summary>
	/// Builds a <see cref="CopilotClientUnhealthyException"/> from a freshly-latched fault
	/// broker, carrying the original SDK exception as the <c>InnerException</c>. Used to
	/// convert opaque SDK transport exceptions into a structured signal that the swap
	/// loop's classifier recognises.
	/// </summary>
	private static CopilotClientUnhealthyException NewClientUnhealthyFromBroker(
		ISessionFaultBroker faultBroker,
		Exception innerException,
		string triggeringSessionId)
	{
		var reason = faultBroker.UnhealthyTriggeringFailureReason ?? "transport_lost";
		var probe = faultBroker.UnhealthyReason ?? "(no probe details)";
		var attributedSessionId = faultBroker.UnhealthyTriggeringSessionId ?? triggeringSessionId;

		var ex = new CopilotClientUnhealthyException(
			triggeringSessionId: attributedSessionId,
			triggeringFailureReason: reason,
			probeDetails: probe,
			message: $"Copilot CLI client latched unhealthy after this attempt's SDK failure " +
					 $"(inner: {innerException.GetType().Name}: {innerException.Message}). " +
					 $"Triggering session: '{attributedSessionId}'. Probe: {probe}.");
		// Preserve the original SDK exception for diagnostics. CopilotClientUnhealthyException
		// inherits from Exception so we can't set InnerException via constructor, but xUnit
		// and structured loggers serialize Data[] entries.
		ex.Data["InnerSdkException"] = innerException.ToString();
		return ex;
	}

	/// <summary>
	/// Builds a <see cref="ResumeSessionConfig"/> from the same source data as a normal
	/// <see cref="SessionConfig"/>, with the fields the SDK actually accepts on resume
	/// (model, MCPs, sub-agents, tools, hooks). The SDK requires <c>OnPermissionRequest</c>
	/// on resume or it throws <see cref="ArgumentException"/>; we set it unconditionally
	/// from <see cref="PermissionHandler.ApproveAll"/> mirroring the create path.
	/// </summary>
	/// <param name="baseConfig">The create-time config whose carry-over fields we mirror.</param>
	/// <param name="onEvent">
	/// Optional event handler registered at config time via SDK 1.0.0's
	/// <see cref="SessionConfigBase.OnEvent"/>. When supplied, the SDK invokes the handler
	/// for every event the resumed session emits — including events that fire before our
	/// call to <see cref="CopilotSession.SendAsync(MessageOptions, CancellationToken)"/>
	/// returns. This is what closes the small subscribe-after-create race window the old
	/// <c>session.On(...)</c> pattern left open.
	/// </param>
	internal ResumeSessionConfig BuildResumeSessionConfig(SessionConfig baseConfig, Action<SessionEvent>? onEvent = null)
	{
		var config = new ResumeSessionConfig
		{
			// SDK 1.0.0 introduces ClientName as a stable identifier the runtime / telemetry
			// pipeline can use to partition events by host. We pin it to "orchestra" so
			// downstream observers (SDK-internal logs, OpenTelemetry traces, CLI-side
			// process accounting) can correlate sessions back to this orchestrator.
			ClientName = "orchestra",
			Model = baseConfig.Model,
			Streaming = true,
			OnPermissionRequest = baseConfig.OnPermissionRequest,
			ReasoningEffort = baseConfig.ReasoningEffort,
			// SDK 1.0.1: carry the reasoning-summary / context-tier / token knobs across a
			// CLI swap+resume so the resumed session keeps the original per-step tuning.
			ReasoningSummary = baseConfig.ReasoningSummary,
			ContextTier = baseConfig.ContextTier,
			GitHubToken = baseConfig.GitHubToken,
			SystemMessage = baseConfig.SystemMessage,
			McpServers = baseConfig.McpServers,
			CustomAgents = baseConfig.CustomAgents,
			// SDK 1.0.0 PR #1098: carry the main-agent excluded-tools list across resume
			// so a swap-and-resume cycle preserves the exclusion policy. Without this,
			// the resumed session would silently re-enable the excluded tools — a
			// security-relevant regression.
			DefaultAgent = baseConfig.DefaultAgent,
			Tools = baseConfig.Tools,
			SkillDirectories = baseConfig.SkillDirectories,
			// SDK 1.0.0 added InstructionDirectories — carry it across resume so the agent
			// has the same auxiliary-instructions surface after a swap as the original create.
			InstructionDirectories = baseConfig.InstructionDirectories,
			InfiniteSessions = baseConfig.InfiniteSessions,
			Hooks = baseConfig.Hooks,
			WorkingDirectory = baseConfig.WorkingDirectory,
			// Carry the opt-in HITL handlers across a CLI swap+resume so plan/elicitation
			// gating still routes to the operator on the resumed session.
			OnElicitationRequest = baseConfig.OnElicitationRequest,
			OnExitPlanModeRequest = baseConfig.OnExitPlanModeRequest,
			// SDK 1.0.0 lets us register the event handler at config-time via OnEvent
			// rather than calling session.On(...) after CreateSessionAsync/ResumeSessionAsync
			// returns. Closing the window also matters on resume because the runtime starts
			// replaying historical events the moment the resumed session is materialised.
			OnEvent = onEvent,
		};
		return config;
	}

	internal SessionConfig BuildSessionConfig(Action<SessionEvent>? onEvent = null, CancellationToken cancellationToken = default)
	{
		var config = new SessionConfig
		{
			// SDK 1.0.0 introduces ClientName — pin to "orchestra" so the runtime / telemetry
			// pipeline can partition events by host (see BuildResumeSessionConfig for the
			// full rationale).
			ClientName = "orchestra",
			Model = _model,
			Streaming = true,
			OnPermissionRequest = BuildPermissionHandler(cancellationToken),
			// SDK 1.0.0 lets us register the event handler at config-time via OnEvent
			// rather than calling session.On(...) after CreateSessionAsync returns. The
			// post-create subscription window had a tiny race where events fired between
			// the SDK creating the session and our subscription would be dropped (in
			// particular the very first SessionStartEvent could slip through on cold
			// CLI workers). Setting OnEvent eliminates that gap.
			OnEvent = onEvent,
		};

		if (_reasoningLevel is not null)
		{
			config.ReasoningEffort = _reasoningLevel.Value.ToString().ToLowerInvariant();
		}

		// SDK 1.0.1: reasoning-summary verbosity + context-window tier are per-session knobs
		// on SessionConfigBase. Map Orchestra's engine-neutral enums onto the SDK value types.
		if (_reasoningSummary is not null)
		{
			config.ReasoningSummary = _reasoningSummary.Value switch
			{
				Engine.ReasoningSummaryLevel.None => GitHub.Copilot.ReasoningSummary.None,
				Engine.ReasoningSummaryLevel.Concise => GitHub.Copilot.ReasoningSummary.Concise,
				_ => GitHub.Copilot.ReasoningSummary.Detailed,
			};
		}

		if (_contextTier is not null)
		{
			config.ContextTier = _contextTier.Value == Engine.ContextTier.LongContext
				? GitHub.Copilot.ContextTier.LongContext
				: GitHub.Copilot.ContextTier.Default;
		}

		// Per-step working directory for the agent's shell/file tools + config discovery.
		if (!string.IsNullOrWhiteSpace(_workingDirectory))
		{
			config.WorkingDirectory = _workingDirectory;
		}

		// Per-step GitHub token override (host-level default is applied at the client layer).
		if (!string.IsNullOrWhiteSpace(_gitHubToken))
		{
			config.GitHubToken = _gitHubToken;
		}

		// Configure system message with Append, Replace, or Customize mode
		if (_systemPrompt is not null)
		{
			config.SystemMessage = new SystemMessageConfig
			{
				Content = _systemPrompt,
			};

			if (_systemPromptMode is not null)
			{
				config.SystemMessage.Mode = _systemPromptMode.Value switch
				{
					SystemPromptMode.Append => SystemMessageMode.Append,
					SystemPromptMode.Customize => SystemMessageMode.Customize,
					_ => SystemMessageMode.Replace,
				};

				// Apply section overrides for Customize mode
				if (_systemPromptMode.Value == SystemPromptMode.Customize && _systemPromptSections is { Count: > 0 })
				{
					// SDK 1.0.0 changed Sections from Dictionary<string, SectionOverride>
					// to Dictionary<SystemMessageSection, SectionOverride>. The struct has
					// a (string) ctor so we forward the legacy string key as-is; unknown
					// section names are still accepted by the runtime and treated as
					// additional-instructions hooks (per the SDK README).
					config.SystemMessage.Sections = _systemPromptSections
						.ToDictionary(
							kvp => new SystemMessageSection(kvp.Key),
							kvp => new SectionOverride
							{
								Action = kvp.Value.Action switch
								{
									SystemPromptSectionAction.Replace => SectionOverrideAction.Replace,
									SystemPromptSectionAction.Remove => SectionOverrideAction.Remove,
									SystemPromptSectionAction.Append => SectionOverrideAction.Append,
									SystemPromptSectionAction.Prepend => SectionOverrideAction.Prepend,
									_ => SectionOverrideAction.Replace,
								},
								Content = kvp.Value.Content,
							});
				}
			}
		}

		if (_mcps.Length > 0)
		{
			config.McpServers = BuildMcpServers();
		}

		if (_subagents.Length > 0)
		{
			config.CustomAgents = BuildCustomAgents();
		}

		// Register engine tools as custom AIFunction instances
		if (_engineTools.Count > 0 && _engineToolContext is not null)
		{
			config.Tools = BuildEngineTools();
		}

		if (_skillDirectories.Length > 0)
		{
			config.SkillDirectories = [.. _skillDirectories];
		}

		// Configure infinite sessions
		if (_infiniteSessionConfig is not null)
		{
			config.InfiniteSessions = new GitHub.Copilot.InfiniteSessionConfig();

			if (_infiniteSessionConfig.Enabled.HasValue)
				config.InfiniteSessions.Enabled = _infiniteSessionConfig.Enabled.Value;

			if (_infiniteSessionConfig.BackgroundCompactionThreshold.HasValue)
				config.InfiniteSessions.BackgroundCompactionThreshold = _infiniteSessionConfig.BackgroundCompactionThreshold.Value;

			if (_infiniteSessionConfig.BufferExhaustionThreshold.HasValue)
				config.InfiniteSessions.BufferExhaustionThreshold = _infiniteSessionConfig.BufferExhaustionThreshold.Value;
		}

		// SDK 1.0.0: forward Orchestra's skill directories as instruction directories too
		// so the runtime's instruction-discovery pass picks up any *.md files alongside
		// any *.skill.yml files. Skill directories remain the primary registration; this
		// is an additive enhancement that turns directories with mixed contents into a
		// one-stop-shop for both kinds.
		if (_skillDirectories.Length > 0)
		{
			config.InstructionDirectories = [.. _skillDirectories];
		}

		// Configure session hooks for structured audit logging
		config.Hooks = BuildSessionHooks();

		// SDK 1.0.0 PR #1098: forward the main-agent tool-exclusion list. Sub-agents
		// are NOT affected here — they get their own Subagent.Tools filter via
		// CustomAgentConfig.Tools in BuildCustomAgents. The DefaultAgentConfig is
		// only set when there is at least one excluded tool, so the typical "no
		// exclusions" path leaves config.DefaultAgent null and the SDK uses its
		// full built-in catalog.
		if (_excludedTools.Length > 0)
		{
			config.DefaultAgent = new DefaultAgentConfig
			{
				ExcludedTools = [.. _excludedTools],
			};
		}

		// Opt-in human-in-the-loop (humanInput=true): route the SDK's elicitation and
		// exit-plan-mode handshakes to Orchestra's pending-input surface instead of resolving
		// them autonomously. Left null by default so existing runs are unchanged. Requires the
		// engine-tool context (run identity + HITL waiter); the internal output-handler sub-call
		// has no context and falls through to the runtime default.
		if (_humanInput && _engineToolContext is not null)
		{
			config.OnElicitationRequest = ctx => HandleElicitationAsync(ctx, cancellationToken);
			config.OnExitPlanModeRequest = (req, invocation) => HandleExitPlanModeAsync(req, invocation, cancellationToken);
		}

		return config;
	}

	/// <summary>
	/// Routes a Copilot <c>elicitation.requested</c> handshake to Orchestra's human-in-the-loop.
	/// Accepts with the operator's reply (a JSON object is passed through verbatim; free-form text
	/// is wrapped under a <c>response</c> key) or declines on cancellation / missing run identity.
	/// </summary>
	// UIElicitationResponseAction is an evaluation-only SDK API (GHCP001); the elicitation
	// request/response handshake is the SDK's supported integration point, so suppress narrowly.
#pragma warning disable GHCP001
	private async Task<ElicitationResult> HandleElicitationAsync(ElicitationContext context, CancellationToken cancellationToken)
	{
		var prompt = string.IsNullOrWhiteSpace(context.Message)
			? "The agent is requesting input to continue."
			: context.Message;

		try
		{
			var response = await _engineToolContext!
				.RequestHumanInputAsync(prompt, choices: null, Engine.PendingInputKind.Elicitation, cancellationToken)
				.ConfigureAwait(false);

			if (response is null)
			{
				return new ElicitationResult { Action = GitHub.Copilot.Rpc.UIElicitationResponseAction.Decline, Content = new Dictionary<string, object>() };
			}

			return new ElicitationResult
			{
				Action = GitHub.Copilot.Rpc.UIElicitationResponseAction.Accept,
				Content = BuildElicitationContent(response.ResolveContent()),
			};
		}
		catch (OperationCanceledException)
		{
			// Step/orchestration timeout or external cancellation — decline so the turn unwinds.
			return new ElicitationResult { Action = GitHub.Copilot.Rpc.UIElicitationResponseAction.Decline, Content = new Dictionary<string, object>() };
		}
	}
#pragma warning restore GHCP001

	private static IDictionary<string, object> BuildElicitationContent(string reply)
	{
		if (!string.IsNullOrWhiteSpace(reply))
		{
			try
			{
				var parsed = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(reply);
				if (parsed is not null)
				{
					return parsed;
				}
			}
			catch (System.Text.Json.JsonException)
			{
				// Not a JSON object — fall through to the free-form wrapper.
			}
		}

		return new Dictionary<string, object> { ["response"] = reply };
	}

	/// <summary>
	/// Routes a Copilot <c>exitPlanMode.requested</c> (plan approval) handshake to Orchestra's
	/// human-in-the-loop. Approves when the operator replies "approve"; otherwise returns the
	/// operator's feedback so the agent keeps planning.
	/// </summary>
	private async Task<ExitPlanModeResult> HandleExitPlanModeAsync(ExitPlanModeRequest request, ExitPlanModeInvocation invocation, CancellationToken cancellationToken)
	{
		var prompt = BuildPlanApprovalPrompt(request);

		try
		{
			var response = await _engineToolContext!
				.RequestHumanInputAsync(prompt, choices: ["approve", "reject"], Engine.PendingInputKind.ExitPlanMode, cancellationToken)
				.ConfigureAwait(false);

			if (response is null)
			{
				return new ExitPlanModeResult { Approved = false, Feedback = "No operator available to approve the plan." };
			}

			var approved = IsApproval(response);
			return new ExitPlanModeResult
			{
				Approved = approved,
				Feedback = approved ? string.Empty : response.ResolveContent(),
			};
		}
		catch (OperationCanceledException)
		{
			return new ExitPlanModeResult { Approved = false, Feedback = "Plan approval cancelled." };
		}
	}

	private static bool IsApproval(Engine.UserInputResponse response)
	{
		if (!string.IsNullOrWhiteSpace(response.Choice))
		{
			return response.Choice.Trim().Equals("approve", StringComparison.OrdinalIgnoreCase);
		}

		var content = response.ResolveContent().Trim();
		return content.Equals("approve", StringComparison.OrdinalIgnoreCase)
			|| content.Equals("approved", StringComparison.OrdinalIgnoreCase)
			|| content.Equals("yes", StringComparison.OrdinalIgnoreCase);
	}

	private static string BuildPlanApprovalPrompt(ExitPlanModeRequest request)
	{
		var sb = new System.Text.StringBuilder();
		sb.AppendLine("The agent has finished planning and is requesting approval to proceed.");
		if (!string.IsNullOrWhiteSpace(request.Summary))
		{
			sb.AppendLine();
			sb.AppendLine(request.Summary);
		}
		if (!string.IsNullOrWhiteSpace(request.PlanContent))
		{
			sb.AppendLine();
			sb.AppendLine(request.PlanContent);
		}
		sb.AppendLine();
		sb.Append("Reply 'approve' to proceed, or provide feedback to send the agent back to planning.");
		return sb.ToString();
	}

	/// <summary>
	/// Builds the SDK permission handler from the step's <see cref="Engine.PermissionPolicy"/>:
	/// auto-approve (default), deny-by-glob, or route to a human operator.
	/// </summary>
	// PermissionDecision is an evaluation-only SDK API (GHCP001); the permission handler is the
	// SDK's supported gate, so suppress narrowly across the policy methods.
#pragma warning disable GHCP001
	private Func<PermissionRequest, PermissionInvocation, Task<GitHub.Copilot.Rpc.PermissionDecision>> BuildPermissionHandler(CancellationToken cancellationToken)
	{
		var policy = _permissionPolicy;
		if (policy is null || policy.Mode == Engine.PermissionMode.ApproveAll)
		{
			return PermissionHandler.ApproveAll;
		}

		if (policy.Mode == Engine.PermissionMode.DenyList)
		{
			var deny = policy.Deny;
			return (request, invocation) => Task.FromResult(EvaluateDenyList(request, deny));
		}

		// RequireHumanApproval
		return (request, invocation) => EvaluateHumanApprovalAsync(request, cancellationToken);
	}

	private static GitHub.Copilot.Rpc.PermissionDecision EvaluateDenyList(PermissionRequest request, string[] deny)
	{
		var (kind, target) = ExtractPermission(request);
		if (IsDeniedByPolicy(kind, target, deny))
		{
			return GitHub.Copilot.Rpc.PermissionDecision.Reject(
				$"Denied by orchestration permission policy ({kind}{(target is null ? string.Empty : $": {target}")}).");
		}

		return GitHub.Copilot.Rpc.PermissionDecision.ApproveOnce();
	}

	private async Task<GitHub.Copilot.Rpc.PermissionDecision> EvaluateHumanApprovalAsync(PermissionRequest request, CancellationToken cancellationToken)
	{
		if (_engineToolContext is null)
		{
			return GitHub.Copilot.Rpc.PermissionDecision.UserNotAvailable();
		}

		var (kind, target) = ExtractPermission(request);
		var prompt = $"The agent is requesting permission to {kind}{(target is null ? string.Empty : $": {target}")}.\n" +
			"Reply 'approve' to allow this action once, or provide a reason to deny it.";

		// Serialize approvals so multiple concurrent permission requests don't collide on the
		// single per-(run,step) pending-input waiter key.
		await _permissionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			var response = await _engineToolContext
				.RequestHumanInputAsync(prompt, choices: ["approve", "deny"], Engine.PendingInputKind.Permission, cancellationToken)
				.ConfigureAwait(false);

			if (response is null)
			{
				return GitHub.Copilot.Rpc.PermissionDecision.UserNotAvailable();
			}

			return IsApproval(response)
				? GitHub.Copilot.Rpc.PermissionDecision.ApproveOnce()
				: GitHub.Copilot.Rpc.PermissionDecision.Reject(response.ResolveContent());
		}
		catch (OperationCanceledException)
		{
			return GitHub.Copilot.Rpc.PermissionDecision.Reject("Permission request cancelled.");
		}
		finally
		{
			_permissionGate.Release();
		}
	}

	/// <summary>Flattens an SDK permission request to (kind, target) for policy matching.</summary>
	private static (string Kind, string? Target) ExtractPermission(PermissionRequest request) => request switch
	{
		PermissionRequestRead read => ("read", read.Path),
		PermissionRequestWrite write => ("write", write.FileName),
		PermissionRequestShell shell => ("shell", shell.FullCommandText),
		PermissionRequestUrl url => ("url", url.Url),
		PermissionRequestMcp mcp => ("mcp", mcp.ToolName ?? mcp.ServerName),
		PermissionRequestMemory memory => ("memory", memory.Subject),
		PermissionRequestCustomTool customTool => ("customTool", customTool.ToolName),
		PermissionRequestHook hook => ("hook", hook.ToolName),
		PermissionRequestExtensionManagement extMgmt => ("extensionManagement", extMgmt.ExtensionName),
		PermissionRequestExtensionPermissionAccess extAccess => ("extensionPermissionAccess", extAccess.ExtensionName),
		_ => (request.Kind ?? "unknown", null),
	};

	private static bool GlobMatches(string pattern, string value)
	{
		if (string.IsNullOrEmpty(pattern))
		{
			return false;
		}

		if (!pattern.Contains('*') && !pattern.Contains('?'))
		{
			return string.Equals(pattern, value, StringComparison.OrdinalIgnoreCase);
		}

		var regex = "^" + System.Text.RegularExpressions.Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
		return System.Text.RegularExpressions.Regex.IsMatch(value, regex, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
	}
#pragma warning restore GHCP001

	/// <summary>
	/// True when a permission request's <paramref name="kind"/> or <paramref name="target"/>
	/// matches any deny glob. Extracted for unit testing the deny-list policy logic.
	/// </summary>
	internal static bool IsDeniedByPolicy(string kind, string? target, string[] deny)
	{
		foreach (var pattern in deny)
		{
			if (GlobMatches(pattern, kind) || (target is not null && GlobMatches(pattern, target)))
			{
				return true;
			}
		}

		return false;
	}
#pragma warning restore GHCP001

	/// <summary>
	/// Builds session hooks that capture structured audit log entries.
	/// Hooks fire at well-defined points in the session lifecycle and record
	/// tool calls, prompt submissions, errors, and lifecycle events.
	/// </summary>
	private SessionHooks BuildSessionHooks()
	{
		return new SessionHooks
		{
			OnSessionStart = (input, invocation) =>
			{
				// SDK 1.0.0 renamed input.Cwd to input.WorkingDirectory and now exposes
				// SessionId on every hook input (PR series #1295/#1306), so we can log
				// the actual session id rather than reconstructing it from invocation.
				LogHookSessionStart(input.SessionId ?? "(unknown)", input.Source ?? "(unspecified)", input.WorkingDirectory ?? "(none)");
				_reporter.ReportAuditLogEntry(_stepName, new AuditLogEntry
				{
					Sequence = 0,
					Timestamp = DateTimeOffset.UtcNow,
					EventType = AuditEventType.SessionStart,
					SessionSource = input.Source,
					AdditionalContext = input.WorkingDirectory,
				});
				return Task.FromResult<SessionStartHookOutput?>(null);
			},

			OnUserPromptSubmitted = (input, invocation) =>
			{
				_reporter.ReportAuditLogEntry(_stepName, new AuditLogEntry
				{
					Sequence = 0,
					Timestamp = DateTimeOffset.UtcNow,
					EventType = AuditEventType.PromptSubmitted,
					Prompt = input.Prompt?.Length > 500 ? input.Prompt[..500] + "..." : input.Prompt,
				});
				return Task.FromResult<UserPromptSubmittedHookOutput?>(null);
			},

			OnPreToolUse = (input, invocation) =>
			{
				string? argsJson = null;
				if (input.ToolArgs is not null)
				{
					try { argsJson = System.Text.Json.JsonSerializer.Serialize(input.ToolArgs); }
					catch { /* ignore */ }
				}

				_reporter.ReportAuditLogEntry(_stepName, new AuditLogEntry
				{
					Sequence = 0,
					Timestamp = DateTimeOffset.UtcNow,
					EventType = AuditEventType.PreToolUse,
					ToolName = input.ToolName,
					ToolArguments = argsJson,
					PermissionDecision = "allow",
				});

				return Task.FromResult<PreToolUseHookOutput?>(
					new PreToolUseHookOutput { PermissionDecision = "allow" });
			},

			OnPostToolUse = (input, invocation) =>
			{
				string? resultStr = input.ToolResult?.ToString();

				_reporter.ReportAuditLogEntry(_stepName, new AuditLogEntry
				{
					Sequence = 0,
					Timestamp = DateTimeOffset.UtcNow,
					EventType = AuditEventType.PostToolUse,
					ToolName = input.ToolName,
					ToolResult = resultStr?.Length > 500 ? resultStr[..500] + "..." : resultStr,
				});
				return Task.FromResult<PostToolUseHookOutput?>(null);
			},

			// SDK 1.0.0 (PR #1013) added OnPostToolUseFailure — a dedicated hook that
			// fires only when a tool call fails. OnPostToolUse only fires on success,
			// so without this hook every tool fault would disappear from the audit log
			// even though the model usually sees them and adapts. We emit a parallel
			// AuditEventType.PostToolUseFailure entry that carries the error message
			// (input.Error) alongside the tool name and arguments. AdditionalContext is
			// also injected back into the model's next turn — useful for retry guidance.
			OnPostToolUseFailure = (input, invocation) =>
			{
				string? argsJson = null;
				if (input.ToolArgs is not null)
				{
					try { argsJson = System.Text.Json.JsonSerializer.Serialize(input.ToolArgs); }
					catch { /* ignore: arguments are best-effort for the trace */ }
				}

				var errorMessage = input.Error;
				LogHookPostToolUseFailure(
					input.SessionId ?? "(unknown)",
					input.ToolName ?? "(unknown)",
					errorMessage ?? "(no message)");

				_reporter.ReportAuditLogEntry(_stepName, new AuditLogEntry
				{
					Sequence = 0,
					Timestamp = DateTimeOffset.UtcNow,
					EventType = AuditEventType.PostToolUseFailure,
					ToolName = input.ToolName,
					ToolArguments = argsJson,
					ToolSuccess = false,
					Error = errorMessage,
				});
				return Task.FromResult<PostToolUseFailureHookOutput?>(null);
			},

			OnErrorOccurred = (input, invocation) =>
			{
				_reporter.ReportAuditLogEntry(_stepName, new AuditLogEntry
				{
					Sequence = 0,
					Timestamp = DateTimeOffset.UtcNow,
					EventType = AuditEventType.Error,
					Error = input.Error,
					ErrorContext = input.ErrorContext,
				});
				return Task.FromResult<ErrorOccurredHookOutput?>(null);
			},

			OnSessionEnd = (input, invocation) =>
			{
				_reporter.ReportAuditLogEntry(_stepName, new AuditLogEntry
				{
					Sequence = 0,
					Timestamp = DateTimeOffset.UtcNow,
					EventType = AuditEventType.SessionEnd,
					SessionEndReason = input.Reason,
				});
				return Task.FromResult<SessionEndHookOutput?>(null);
			},
		};
	}

	/// <summary>
	/// The step name for audit log correlation. Set from the reporter context.
	/// Defaults to the model name if no step name is available.
	/// </summary>
	private string _stepName => _engineToolContext?.StepName ?? _model;

	/// <summary>
	/// Builds image attachments for the Copilot SDK message.
	/// </summary>
	private List<Attachment> BuildAttachments()
	{
		var attachments = new List<Attachment>();

		foreach (var attachment in _attachments)
		{
			switch (attachment)
			{
				case FileImageAttachment file:
					attachments.Add(new AttachmentFile
					{
						Path = file.Path,
						DisplayName = file.DisplayName ?? System.IO.Path.GetFileName(file.Path),
					});
					break;

				case BlobImageAttachment blob:
					attachments.Add(new AttachmentBlob
					{
						Data = blob.Data,
						MimeType = blob.MimeType,
					});
					break;
			}
		}

		return attachments;
	}

	private void LogMcpConfiguration()
	{
		LogMcpCount(_mcps.Length);

		if (_mcps.Length > 0)
		{
			foreach (var mcp in _mcps)
			{
				switch (mcp)
				{
					case LocalMcp local:
						LogLocalMcpServer(mcp.Name, local.Command, string.Join(", ", local.Arguments), local.WorkingDirectory);
						break;
					case RemoteMcp remote:
						LogRemoteMcpServer(mcp.Name, remote.Endpoint);
						break;
				}
			}
		}
		else
		{
			LogNoMcpsConfigured();
		}
	}

	private async Task<IReadOnlyList<AvailableModelInfo>?> GetAvailableModelsAsync(
		ICopilotClient client,
		ICopilotClientLease lease,
		CancellationToken cancellationToken)
	{
		// Use cached models if available to avoid repeated network calls
		// across parallel steps in the same orchestration run.
		var availableModels = lease.CachedAvailableModels;

		if (availableModels is null)
		{
			try
			{
				var models = await client.ListModelsAsync(cancellationToken).ConfigureAwait(false);
				availableModels = models
					.OrderBy(m => m.Id)
					.Select(MapAvailableModelInfo)
					.ToList();

				// Cache for other agents in this run
				lease.SetCachedAvailableModels(availableModels);
			}
			catch (Exception ex)
			{
				// Unable to list models - log and continue without them
				LogListModelsFailed(ex);
			}
		}

		return availableModels;
	}

	private void ReportModelMismatchIfNeeded(string? actualModel, IReadOnlyList<AvailableModelInfo>? availableModels)
	{
		if (actualModel is null || string.Equals(actualModel, _model, StringComparison.OrdinalIgnoreCase))
			return;

		_reporter.ReportModelMismatch(new ModelMismatchInfo
		{
			ConfiguredModel = _model,
			ActualModel = actualModel,
			SystemPromptMode = _systemPromptMode?.ToString() ?? "(SDK default)",
			ReasoningLevel = _reasoningLevel?.ToString() ?? "(none)",
			SystemPromptPreview = _systemPrompt is not null
				? $"{_systemPrompt[..Math.Min(_systemPrompt.Length, 80)]}..."
				: "(none)",
			McpServers = _mcps.Length > 0
				? _mcps.Select(m => m.Name).ToArray()
				: null,
			AvailableModels = availableModels,
		});
	}

	private static AvailableModelInfo? FindModelInfo(IReadOnlyList<AvailableModelInfo>? availableModels, string? modelId)
		=> modelId is null
			? null
			: availableModels?.FirstOrDefault(m => string.Equals(m.Id, modelId, StringComparison.OrdinalIgnoreCase));

	private static AvailableModelInfo MapAvailableModelInfo(ModelInfo model)
		=> new()
		{
			Id = model.Id,
			Name = model.Name,
			DefaultReasoningEffort = model.DefaultReasoningEffort,
			BillingMultiplier = model.Billing?.Multiplier,
			ReasoningEfforts = model.SupportedReasoningEfforts is { Count: > 0 }
				? [.. model.SupportedReasoningEfforts]
				: null,
			PolicyState = model.Policy?.State,
			PolicyTerms = model.Policy?.Terms,
			SupportsReasoningEffort = model.Capabilities?.Supports?.ReasoningEffort,
			SupportsVision = model.Capabilities?.Supports?.Vision,
			MaxContextWindowTokens = model.Capabilities?.Limits?.MaxContextWindowTokens,
			MaxPromptTokens = model.Capabilities?.Limits?.MaxPromptTokens,
			VisionSupportedMediaTypes = model.Capabilities?.Limits?.Vision?.SupportedMediaTypes is { Count: > 0 }
				? [.. model.Capabilities.Limits.Vision.SupportedMediaTypes]
				: null,
			MaxPromptImages = model.Capabilities?.Limits?.Vision?.MaxPromptImages,
			MaxPromptImageSize = model.Capabilities?.Limits?.Vision?.MaxPromptImageSize,
		};

	private Dictionary<string, McpServerConfig> BuildMcpServers() => BuildMcpServerDictionary(_mcps);

	private List<CustomAgentConfig> BuildCustomAgents()
	{
		var customAgents = new List<CustomAgentConfig>();

		foreach (var subagent in _subagents)
		{
			var config = new CustomAgentConfig
			{
				Name = subagent.Name,
				Prompt = subagent.Prompt,
			};

			if (subagent.DisplayName is not null)
				config.DisplayName = subagent.DisplayName;

			if (subagent.Description is not null)
				config.Description = subagent.Description;

			if (subagent.Tools is { Length: > 0 })
				config.Tools = [.. subagent.Tools];

			if (!subagent.Infer)
				config.Infer = false;

			// SDK 1.0.0 added per-sub-agent model overrides (PR #1309). Forward
			// Subagent.Model when set so a sub-agent can run on a different model than
			// the main step (e.g. main step on claude-opus-4.6, a researcher sub-agent
			// on gpt-5-mini for cheap fan-out). Null leaves the runtime to inherit the
			// main session's model, preserving the old behaviour for sub-agents that
			// don't specify one.
			if (!string.IsNullOrEmpty(subagent.Model))
				config.Model = subagent.Model;

			// SDK 1.0.0 added per-sub-agent skills (PR #995) so a sub-agent can scope
			// its instruction surface to a curated subset of the host's skill catalog.
			// Empty / null leaves the sub-agent on the main session's skill resolution
			// (which itself may be filtered by the orchestration-level SkillDirectories).
			if (subagent.Skills is { Length: > 0 })
				config.Skills = [.. subagent.Skills];

			// Add MCP servers specific to this subagent
			if (subagent.Mcps.Length > 0)
			{
				config.McpServers = BuildMcpServersFor(subagent.Mcps);
			}

			customAgents.Add(config);
		}

		LogSubagentConfiguration();
		return customAgents;
	}

	private Dictionary<string, McpServerConfig> BuildMcpServersFor(Mcp[] mcps) => BuildMcpServerDictionary(mcps);

	private static Dictionary<string, McpServerConfig> BuildMcpServerDictionary(Mcp[] mcps)
	{
		var servers = new Dictionary<string, McpServerConfig>();

		foreach (var mcp in mcps)
		{
			// Optional per-server tool-call timeout (in milliseconds for the SDK).
			// When set on the YAML 'mcps' entry as 'timeoutSeconds', this overrides the SDK's
			// default ~3-minute MCP request timeout. Use this for long-running tools such as
			// orchestra MCP's invoke_orchestration in sync mode.
			int? timeoutMs = mcp.Timeout is { TotalMilliseconds: > 0 } ts
				? (int)Math.Min(int.MaxValue, ts.TotalMilliseconds)
				: null;

			switch (mcp)
			{
				case LocalMcp local:
					var stdio = new McpStdioServerConfig
					{
						Command = local.Command,
						Args = [.. local.Arguments],
						// SDK 1.0.0 renamed Cwd -> WorkingDirectory on McpStdioServerConfig
						// (cross-SDK naming consolidation). Same semantics; only the property
						// name changed.
						WorkingDirectory = local.WorkingDirectory,
						Tools = ["*"],
						Timeout = timeoutMs,
					};
					// SDK 1.0.0 added McpStdioServerConfig.Env so per-server env vars can
					// be injected at session-creation time (commonly used for API keys
					// resolved from {{env.*}} templates). Only set the dict when the engine
					// LocalMcp actually has values — the SDK will otherwise use the
					// inherited process environment.
					if (local.Environment is { Count: > 0 } envEntries)
					{
						stdio.Env = envEntries.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
					}
					servers[mcp.Name] = stdio;
					break;

				case RemoteMcp remote:
					servers[mcp.Name] = new McpHttpServerConfig
					{
						Url = remote.Endpoint,
						Headers = remote.Headers,
						Tools = ["*"],
						Timeout = timeoutMs,
					};
					break;
			}
		}

		return servers;
	}

	private void LogSubagentConfiguration()
	{
		LogSubagentCount(_subagents.Length);

		foreach (var subagent in _subagents)
		{
			LogSubagentDetails(
				subagent.Name,
				subagent.DisplayName ?? "(none)",
				subagent.Tools is { Length: > 0 } ? string.Join(", ", subagent.Tools) : "all",
				subagent.Mcps.Length,
				subagent.Infer);
		}
	}

	/// <summary>
	/// Converts engine tools to <see cref="AIFunctionDeclaration"/> instances that the
	/// Copilot SDK can register on the session. Each engine tool is wrapped in an
	/// <see cref="EngineToolAIFunction"/> that delegates to <see cref="IEngineTool.Execute"/>
	/// with the shared <see cref="EngineToolContext"/>.
	/// </summary>
	/// <remarks>
	/// On SDK 1.0.0 the helper API <see cref="CopilotTool.DefineTool"/> is the new front-door
	/// for declaring host-side tools, but it generates the JSON schema from the delegate
	/// signature via <see cref="AIFunctionFactory.Create"/> and cannot accept a hand-built
	/// schema. Our engine tools each ship their own well-formed schema through
	/// <see cref="IEngineTool.ParametersSchema"/>, so we keep <see cref="EngineToolAIFunction"/>
	/// (a custom <see cref="AIFunction"/> subclass) which preserves that schema verbatim and
	/// stamps the <c>skip_permission</c> additional property the SDK 1.0.0 runtime reads to
	/// bypass per-call permission prompts (the same flag <see cref="CopilotTool.DefineTool"/>
	/// sets internally when <see cref="CopilotToolOptions.SkipPermission"/> is configured).
	/// Result: engine tools integrate with the SDK 1.0.0 permission system identically to
	/// DefineTool-created tools while keeping their richer parameter schemas.
	/// </remarks>
	private List<AIFunctionDeclaration> BuildEngineTools()
	{
		var functions = new List<AIFunctionDeclaration>();

		foreach (var tool in _engineTools)
		{
			functions.Add(new EngineToolAIFunction(tool, _engineToolContext!));
		}

		return functions;
	}

	#region Source-Generated Logging

	[LoggerMessage(
			EventId = 1,
			Level = LogLevel.Information,
			Message = "Agent has {McpCount} MCPs configured")]
	private partial void LogMcpCount(int mcpCount);

	[LoggerMessage(
			EventId = 2,
			Level = LogLevel.Information,
			Message = "Configuring local MCP server '{Name}': Command={Command}, Args=[{Args}], Cwd={WorkingDirectory}")]
	private partial void LogLocalMcpServer(string name, string command, string args, string? workingDirectory);

	[LoggerMessage(
			EventId = 3,
			Level = LogLevel.Information,
			Message = "Configuring remote MCP server '{Name}': Url={Url}")]
	private partial void LogRemoteMcpServer(string name, string url);

	[LoggerMessage(
			EventId = 4,
			Level = LogLevel.Debug,
			Message = "No MCPs configured for this agent")]
	private partial void LogNoMcpsConfigured();

	[LoggerMessage(
			EventId = 5,
			Level = LogLevel.Debug,
			Message = "Agent has {SubagentCount} subagents configured")]
	private partial void LogSubagentCount(int subagentCount);

	[LoggerMessage(
			EventId = 6,
			Level = LogLevel.Debug,
			Message = "Configuring subagent '{Name}': DisplayName={DisplayName}, Tools=[{Tools}], McpCount={McpCount}, Infer={Infer}")]
	private partial void LogSubagentDetails(string name, string displayName, string tools, int mcpCount, bool infer);

	[LoggerMessage(
			EventId = 7,
			Level = LogLevel.Warning,
			Message = "Failed to list available models for model mismatch report")]
	private partial void LogListModelsFailed(Exception ex);

	[LoggerMessage(EventId = 8, Level = LogLevel.Information,
		Message = "Session: {Operation} on client#{ClientHash} (model={Model}, mcps={McpCount}, thread={ThreadId}, priorSessionId={PriorSessionId})")]
	private partial void LogSessionCreating(int clientHash, string model, int mcpCount, int threadId, string operation, string priorSessionId);

	[LoggerMessage(EventId = 9, Level = LogLevel.Information,
		Message = "Session: {Operation} succeeded on client#{ClientHash} in {ElapsedMs}ms (sessionId={SessionId})")]
	private partial void LogSessionCreated(int clientHash, long elapsedMs, string sessionId, string operation);

	[LoggerMessage(EventId = 10, Level = LogLevel.Error,
		Message = "Session: {Operation} FAILED on client#{ClientHash} after {ElapsedMs}ms")]
	private partial void LogSessionCreateFailed(Exception ex, int clientHash, long elapsedMs, string operation);

	[LoggerMessage(EventId = 11, Level = LogLevel.Warning,
		Message = "Session '{SessionId}': fault-broker probe threw — sibling sessions may not be faulted")]
	private partial void LogFaultBrokerProbeThrew(Exception ex, string sessionId);

	[LoggerMessage(EventId = 12, Level = LogLevel.Error,
		Message = "Session: skipped on client#{ClientHash} — broker latched unhealthy by session '{TriggeringSessionId}'. Reason: {Reason}")]
	private partial void LogSessionSkippedClientUnhealthy(int clientHash, string triggeringSessionId, string reason);

	// EventId 13-15: SendAsync brackets (Phase 3.4). Today the success path is silent;
	// these give a timing/visibility pair so a long-running step's progress through the
	// SDK SendAsync call is captured in the host log alongside the per-turn accumulator
	// logs emitted by CopilotSessionHandler.
	[LoggerMessage(EventId = 13, Level = LogLevel.Information,
		Message = "Session '{SessionId}': SendAsync starting (model={Model}, promptChars={PromptChars}, attachments={AttachmentCount})")]
	private partial void LogSessionSendStarting(string sessionId, string model, int promptChars, int attachmentCount);

	[LoggerMessage(EventId = 14, Level = LogLevel.Information,
		Message = "Session '{SessionId}': SendAsync completed (model={Model}, elapsed={ElapsedMs}ms)")]
	private partial void LogSessionSendCompleted(string sessionId, string model, long elapsedMs);

	[LoggerMessage(EventId = 15, Level = LogLevel.Error,
		Message = "Session '{SessionId}': SendAsync FAILED (model={Model})")]
	private partial void LogSessionSendFailed(Exception ex, string sessionId, string model);

	// ── CLI-swap / session-resume recovery logs (EventIds 16–20) ──
	//
	// These logs are the operator-visible signal that the agent recovered (or tried to
	// recover) from a CLI-class failure mid-step. Information level by design — a swap
	// is a notable event that should appear in default-verbosity host logs alongside the
	// existing session-creation pair so a post-mortem can read the recovery sequence
	// without enabling Debug.

	[LoggerMessage(EventId = 16, Level = LogLevel.Warning,
		Message = "CLI swap triggered (priorSessionId={PriorSessionId}, attempt={SwapAttempt}/{SwapBudget}, reason={Reason}, mode={Mode})")]
	private partial void LogSwapTriggered(string priorSessionId, int swapAttempt, int swapBudget, string reason, SwapMode mode);

	[LoggerMessage(EventId = 17, Level = LogLevel.Error,
		Message = "CLI swap budget exhausted (priorSessionId={PriorSessionId}, attempts={SwapAttempt}/{SwapBudget}, lastReason={Reason}); failing the step")]
	private partial void LogSwapBudgetExhausted(string priorSessionId, int swapAttempt, int swapBudget, string reason);

	[LoggerMessage(EventId = 18, Level = LogLevel.Warning,
		Message = "Resume session '{SessionId}' remained AlreadyInUse past {GraceWindow}; falling back to cold restart")]
	private partial void LogResumeAlreadyInUseFallback(string sessionId, TimeSpan graceWindow);

	[LoggerMessage(EventId = 19, Level = LogLevel.Information,
		Message = "Resume session '{SessionId}' AlreadyInUse cleared inside the grace window; continuing with the resumed session")]
	private partial void LogResumeAlreadyInUseCleared(string sessionId);

	[LoggerMessage(EventId = 20, Level = LogLevel.Warning,
		Message = "Best-effort DeleteSessionAsync failed for session '{SessionId}' after resume_locked fallback")]
	private partial void LogResumeDeleteFailed(Exception ex, string sessionId);

	[LoggerMessage(EventId = 21, Level = LogLevel.Warning,
		Message = "ResumeSessionAsync reported the prior session '{PriorSessionId}' is missing on the CLI ({SdkMessage}); falling back to cold restart of the step")]
	private partial void LogResumeSessionMissingFallback(string priorSessionId, string sdkMessage);

	// EventId 22: SDK 1.0.0 added SessionId to every hook input (PR #1306). Logging the
	// SDK-reported session id alongside the source ("startup" / "resume" / "new") and the
	// CLI-resolved working directory gives operators a one-line confirmation that the
	// hook fired against the session id they expect — useful when sub-agents run in
	// nested sessions and the parent session's hook fires on the child's id.
	[LoggerMessage(EventId = 22, Level = LogLevel.Debug,
		Message = "OnSessionStart hook fired (sessionId={SessionId}, source={Source}, workingDirectory={WorkingDirectory})")]
	private partial void LogHookSessionStart(string sessionId, string source, string workingDirectory);

	// EventId 23: SDK 1.0.0 added OnPostToolUseFailure (PR #1013) so the host learns about
	// tool faults without having to filter post-tool-use entries by ToolSuccess=false.
	// Warning level by design — a tool fault is noteworthy enough to surface in default
	// host logs alongside the existing per-attempt swap/retry logs, but not loud enough
	// to bypass log shippers' rate limits during a flaky-MCP outage. The error message
	// is included verbatim so operators can grep for upstream-specific failure patterns.
	[LoggerMessage(EventId = 23, Level = LogLevel.Warning,
		Message = "OnPostToolUseFailure hook fired (sessionId={SessionId}, tool={ToolName}, error={Error})")]
	private partial void LogHookPostToolUseFailure(string sessionId, string toolName, string error);

	#endregion
}
