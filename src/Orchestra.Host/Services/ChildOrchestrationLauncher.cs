using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Orchestra.Engine;
using Orchestra.Host.Api;
using Orchestra.Host.Hosting;
using Orchestra.Host.Mcp;
using Orchestra.Host.McpServer;
using Orchestra.Host.Persistence;
using Orchestra.Host.Registry;
using Orchestra.Host.Triggers;

namespace Orchestra.Host.Services;

/// <summary>
/// Centralized in-process child orchestration launcher.
/// </summary>
/// <remarks>
/// This class encapsulates the responsibilities that previously lived (duplicated) in
/// <c>DataPlaneTools.InvokeOrchestration</c>, <c>TriggerManager.ExecuteOrchestrationCoreAsync</c>,
/// and the manual SSE <c>/api/orchestrations/{id}/run</c> endpoint:
/// <list type="bullet">
///   <item>Registry lookup and orchestration parsing.</item>
///   <item>Maximum nesting depth enforcement.</item>
///   <item>Execution ID generation.</item>
///   <item>Reporter creation and progress wiring.</item>
///   <item>Cancellation linking to a parent execution.</item>
///   <item><see cref="ActiveExecutionInfo"/> registration with <see cref="ExecutionMetadata"/>.</item>
///   <item>Running the engine executor and surfacing terminal SSE events.</item>
///   <item>Cleanup (delayed removal from the active dictionaries; CTS disposal).</item>
/// </list>
/// Caller-specific responsibilities (custom reporters, dashboard broadcasts, history
/// persistence side-effects, trigger-state bookkeeping) remain in the calling sites and
/// happen around the launcher boundary.
/// </remarks>
public sealed partial class ChildOrchestrationLauncher : IChildOrchestrationLauncher
{
	private readonly OrchestrationRegistry _registry;
	private readonly AgentBuilder _agentBuilder;
	private readonly IScheduler _scheduler;
	private readonly ILoggerFactory _loggerFactory;
	private readonly ILogger<ChildOrchestrationLauncher> _logger;
	private readonly FileSystemRunStore _runStore;
	private readonly OrchestrationHostOptions _hostOptions;
	private readonly EngineToolRegistry _engineToolRegistry;
	private readonly McpServerOptions _mcpOptions;
	private readonly IOrchestrationReporterFactory _reporterFactory;
	private readonly McpManager _mcpManager;
	private readonly IPendingInputStore _pendingInputStore;
	private readonly IHumanInputWaiter _humanInputWaiter;
	private readonly ConcurrentDictionary<string, CancellationTokenSource> _activeExecutions;
	private readonly ConcurrentDictionary<string, ActiveExecutionInfo> _activeExecutionInfos;

	/// <summary>
	/// Time the launcher keeps a completed execution's <see cref="ActiveExecutionInfo"/>
	/// in the active dictionaries before removal. Allows status-poll clients to retrieve
	/// terminal status briefly without scanning history.
	/// </summary>
	internal TimeSpan PostCompletionRetention { get; set; } = TimeSpan.FromSeconds(30);

	public ChildOrchestrationLauncher(
		OrchestrationRegistry registry,
		AgentBuilder agentBuilder,
		IScheduler scheduler,
		ILoggerFactory loggerFactory,
		FileSystemRunStore runStore,
		OrchestrationHostOptions hostOptions,
		EngineToolRegistry engineToolRegistry,
		McpServerOptions mcpOptions,
		IOrchestrationReporterFactory reporterFactory,
		McpManager mcpManager,
		ConcurrentDictionary<string, CancellationTokenSource> activeExecutions,
		ConcurrentDictionary<string, ActiveExecutionInfo> activeExecutionInfos,
		IPendingInputStore? pendingInputStore = null,
		IHumanInputWaiter? humanInputWaiter = null)
	{
		_registry = registry;
		_agentBuilder = agentBuilder;
		_scheduler = scheduler;
		_loggerFactory = loggerFactory;
		_logger = loggerFactory.CreateLogger<ChildOrchestrationLauncher>();
		_runStore = runStore;
		_hostOptions = hostOptions;
		_engineToolRegistry = engineToolRegistry;
		_mcpOptions = mcpOptions;
		_pendingInputStore = pendingInputStore ?? NullPendingInputStore.Instance;
		_humanInputWaiter = humanInputWaiter ?? NullHumanInputWaiter.Instance;
		_reporterFactory = reporterFactory;
		_mcpManager = mcpManager;
		_activeExecutions = activeExecutions;
		_activeExecutionInfos = activeExecutionInfos;
	}

	public Task<ChildOrchestrationHandle> LaunchAsync(
		ChildLaunchRequest request,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(request);

		// 1. Resolve orchestration source: explicit path override (used by triggers) wins
		// over registry lookup (used by external MCP callers and step-based invocations).
		// For registry-based lookups, accept either the registry ID or the orchestration's
		// declared name — YAML authors and external MCP callers typically use the name,
		// while internal callers (TriggerManager) use the ID.
		string entryPath;
		string? entrySourcePath;
		string resolvedOrchestrationId; // The actual registry ID — what UIs/APIs index by.
		if (!string.IsNullOrWhiteSpace(request.OrchestrationPath))
		{
			entryPath = request.OrchestrationPath;
			entrySourcePath = request.OrchestrationSourcePath;
			resolvedOrchestrationId = request.OrchestrationId;
		}
		else
		{
			var entry = _registry.GetByIdOrName(request.OrchestrationId);
			if (entry is null)
			{
				throw new ChildOrchestrationLaunchException(
					ChildOrchestrationLaunchException.OrchestrationNotFound,
					$"Orchestration '{request.OrchestrationId}' not found.");
			}
			entryPath = entry.Path;
			entrySourcePath = entry.SourcePath;
			resolvedOrchestrationId = entry.Id;
		}

		// 2. Parse orchestration file (with global MCPs)
		Orchestration orchestration;
		try
		{
			orchestration = OrchestrationParser.ParseOrchestrationFile(entryPath, entrySourcePath, _registry.GlobalMcps);
		}
		catch (Exception ex)
		{
			throw new ChildOrchestrationLaunchException(
				ChildOrchestrationLaunchException.ParseFailed,
				$"Failed to parse orchestration '{request.OrchestrationId}': {ex.Message}",
				ex);
		}

		// 3. Compute nesting depth and enforce limit
		var (childDepth, rootExecutionId) = ComputeNesting(request.ParentContext);
		if (childDepth > _mcpOptions.MaxNestingDepth)
		{
			throw new ChildOrchestrationLaunchException(
				ChildOrchestrationLaunchException.MaxNestingDepthExceeded,
				$"Maximum nesting depth ({_mcpOptions.MaxNestingDepth}) exceeded. " +
				$"This orchestration would be at depth {childDepth}. " +
				$"Root execution: {rootExecutionId ?? "(unknown)"}.");
		}

		// 4. Generate execution ID and reporter
		var executionId = Guid.NewGuid().ToString("N")[..12];
		// Use the rootExecutionId computed from the parent if any, otherwise this run is its own root.
		rootExecutionId ??= executionId;
		var reporter = request.Reporter ?? _reporterFactory.Create();
		var startedAt = DateTimeOffset.UtcNow;

		// 5. Create cancellation token source (linked to parent's CTS when nested).
		//    We capture the underlying source tokens separately so step 7b can register
		//    pre-fire callbacks that record WHICH source actually triggered the linked CTS.
		CancellationTokenSource cts;
		CancellationToken parentCtsToken = default;
		var hasParentCts = false;
		if (request.ParentContext is not null &&
			_activeExecutions.TryGetValue(request.ParentContext.ParentExecutionId, out var parentCts))
		{
			cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, parentCts.Token);
			parentCtsToken = parentCts.Token;
			hasParentCts = true;
		}
		else
		{
			cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		}

		// 6. Build nesting metadata and active execution info
		var nestingMetadata = new ExecutionMetadata
		{
			ParentExecutionId = request.ParentContext?.ParentExecutionId,
			ParentStepName = request.ParentContext?.ParentStepName,
			RootExecutionId = rootExecutionId,
			Depth = childDepth,
			UserMetadata = request.UserMetadata ?? [],
		};

		var executionInfo = new ActiveExecutionInfo
		{
			ExecutionId = executionId,
			OrchestrationId = resolvedOrchestrationId,
			OrchestrationName = orchestration.Name,
			StartedAt = startedAt,
			TriggeredBy = request.TriggeredBy,
			CancellationTokenSource = cts,
			Reporter = reporter,
			Parameters = request.Parameters,
			TotalSteps = orchestration.Steps.Length,
			NestingMetadata = nestingMetadata,
		};

		_activeExecutions[executionId] = cts;
		_activeExecutionInfos[executionId] = executionInfo;

		// 7a. Pre-fire callbacks on the SOURCE tokens that feed the linked CTS, so the
		//     engine's cancellation-cause probe can later distinguish which source actually
		//     triggered the cancel. First writer wins (??=) — if a host layer like
		//     TriggerManager already set an explicit HostShutdown override before calling
		//     Cancel(), we don't clobber it.
		//
		//     Parent CTS firing ⇒ propagated-from-parent (record External with lineage).
		//     External token firing ⇒ the upstream caller's CancellationToken was triggered.
		//       For sync MCP-tool calls that token IS the MCP request's cancellation token,
		//       and its firing here means the MCP transport aborted the request — record
		//       McpRequestAborted. For other launch paths (e.g. retries with a caller token)
		//       the same token may not represent an MCP transport; we still record it as
		//       External with the best-available detail so the run record is never anonymous.
		var parentExecutionIdForCb = request.ParentContext?.ParentExecutionId;
		var parentStepNameForCb = request.ParentContext?.ParentStepName;
		var isMcpTriggered = request.TriggeredBy is { Length: > 0 } tb
			&& (tb.StartsWith("orchestration:", StringComparison.OrdinalIgnoreCase)
				|| tb.Equals("mcp", StringComparison.OrdinalIgnoreCase));
		var capturedExecutionInfo = executionInfo;

		var externalTokenRegistration = cancellationToken.Register(() =>
		{
			if (capturedExecutionInfo.CancellationCauseOverride is not null)
				return;
			capturedExecutionInfo.CancellationCauseOverride = isMcpTriggered
				? CancellationDetails.McpRequestAborted(
					transportTimeoutSeconds: null,
					source: "mcp-transport",
					detail: parentExecutionIdForCb is null
						? "upstream MCP client closed the request"
						: $"upstream MCP client closed the request (parent: {parentExecutionIdForCb}{(parentStepNameForCb is null ? "" : $", step: {parentStepNameForCb}")})")
				: CancellationDetails.External(detail: "external token");
		});

		var parentTokenRegistration = default(CancellationTokenRegistration);
		if (hasParentCts)
		{
			parentTokenRegistration = parentCtsToken.Register(() =>
			{
				if (capturedExecutionInfo.CancellationCauseOverride is not null)
					return;
				capturedExecutionInfo.CancellationCauseOverride = CancellationDetails.External(
					detail: $"propagated from parent {parentExecutionIdForCb}{(parentStepNameForCb is null ? "" : $" (step: {parentStepNameForCb})")}");
			});
		}

		// 7. Wire progress callbacks if reporter is an SseReporter (the host-default).
		// External callers may have already wired their own callbacks; we only set ours
		// when the slot is empty.
		WireProgressCallbacks(reporter, executionInfo);

		// 8. Build executor (host-supplied configuration)
		var executor = new OrchestrationExecutor(
			_scheduler,
			_agentBuilder,
			reporter,
			_loggerFactory,
			runStore: _runStore,
			engineToolRegistry: _engineToolRegistry,
			mcpResolver: _mcpManager,
			childLauncher: this, // Allow nested Orchestration steps to launch their own children
			globalHooks: _hostOptions.Hooks,
			dataPath: _hostOptions.DataPath,
			serverUrl: _hostOptions.HostBaseUrl,
			pendingInputStore: _pendingInputStore,
			humanInputWaiter: _humanInputWaiter);

		// 9. Wrap pre-execution param transform so executionInfo.Parameters reflects the
		// post-transform values (otherwise the UI keeps showing the pre-transform input).
		Func<CancellationToken, Task<Dictionary<string, string>?>>? wrappedTransform = null;
		if (request.PreExecutionParameterTransform is not null)
		{
			var captured = request.PreExecutionParameterTransform;
			wrappedTransform = async ct =>
			{
				var transformed = await captured(ct).ConfigureAwait(false);
				if (transformed is not null)
				{
					executionInfo.Parameters = transformed;
				}
				return transformed;
			};
		}

		// 10. Build the completion task — runs the orchestration end-to-end and cleans up.
		var completionTask = RunCompletionAsync(
			executor,
			orchestration,
			request,
			executionInfo,
			reporter,
			cts,
			wrappedTransform,
			startedAt,
			externalTokenRegistration,
			parentTokenRegistration);

		var handle = new ChildOrchestrationHandle
		{
			ExecutionId = executionId,
			OrchestrationId = resolvedOrchestrationId,
			OrchestrationName = orchestration.Name,
			Reporter = reporter,
			StartedAt = startedAt,
			Completion = completionTask,
		};

		LogChildLaunched(executionId, orchestration.Name, request.TriggeredBy, childDepth);

		return Task.FromResult(handle);
	}

	private (int Depth, string? RootExecutionId) ComputeNesting(ParentExecutionContext? parent)
	{
		if (parent is null)
			return (0, null);

		// Prefer authoritative live data from the active dictionaries when the parent is
		// still tracked; fall back to the values supplied on the request otherwise.
		if (_activeExecutionInfos.TryGetValue(parent.ParentExecutionId, out var parentInfo))
		{
			var parentDepth = parentInfo.NestingMetadata?.Depth ?? parent.Depth;
			var parentRoot = parentInfo.NestingMetadata?.RootExecutionId
				?? parent.RootExecutionId
				?? parent.ParentExecutionId;
			return (parentDepth + 1, parentRoot);
		}

		var fallbackRoot = parent.RootExecutionId ?? parent.ParentExecutionId;
		return (parent.Depth + 1, fallbackRoot);
	}

	private static void WireProgressCallbacks(IOrchestrationReporter reporter, ActiveExecutionInfo info)
	{
		if (reporter is not SseReporter sse) return;

		// Only wire defaults when no callback has been set externally; otherwise we'd
		// chain or shadow the caller's wiring unintentionally.
		if (sse.OnStepStarted is null)
		{
			sse.OnStepStarted = stepName => info.CurrentStep = stepName;
		}
		if (sse.OnStepCompleted is null)
		{
			sse.OnStepCompleted = _ =>
			{
				info.IncrementCompletedSteps();
				info.CurrentStep = null;
			};
		}
		// Publish the engine's completed step records into the live-active map so
		// data-plane MCP tools can serve mid-run step content (get_orchestration_step
		// reads from PartialStepRecords first, falling back to the persisted run.json
		// only after the run terminates and is removed from activeExecutionInfos).
		if (sse.OnStepRecorded is null)
		{
			sse.OnStepRecorded = (key, record) => info.PublishStepRecord(key, record);
		}
	}

	private async Task<ChildOrchestrationResult> RunCompletionAsync(
		OrchestrationExecutor executor,
		Orchestration orchestration,
		ChildLaunchRequest request,
		ActiveExecutionInfo executionInfo,
		IOrchestrationReporter reporter,
		CancellationTokenSource cts,
		Func<CancellationToken, Task<Dictionary<string, string>?>>? preExecutionParameterTransform,
		DateTimeOffset startedAt,
		CancellationTokenRegistration externalTokenRegistration,
		CancellationTokenRegistration parentTokenRegistration)
	{
		// In sync mode, apply an optional caller-specified hard timeout. Async mode honors
		// only the orchestration's own timeoutSeconds (handled inside the executor) and the
		// linked parent CTS (if any).
		CancellationTokenSource? syncTimeoutCts = null;
		var executorToken = cts.Token;
		var timedOut = false;

		// Probe the engine consults if it observes external cancellation. Returns a
		// SyncInvokeTimeout cause when our wrapper-owned syncTimeoutCts is the trigger,
		// allowing the engine to record a precise CancellationDetails on the run record
		// instead of a generic "External" entry.
		ResolveCancellationCauseDelegate? cancellationCauseProbe = () => executionInfo.CancellationCauseOverride;

		try
		{
			if (request.Mode == ChildLaunchMode.Sync && request.TimeoutSeconds is > 0)
			{
				syncTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
				syncTimeoutCts.CancelAfter(TimeSpan.FromSeconds(request.TimeoutSeconds.Value));
				executorToken = syncTimeoutCts.Token;

				var configuredTimeout = request.TimeoutSeconds.Value;
				var capturedSyncCts = syncTimeoutCts;
				var capturedParentCts = cts;
				cancellationCauseProbe = () =>
					capturedSyncCts.IsCancellationRequested && !capturedParentCts.IsCancellationRequested
						? CancellationDetails.SyncInvokeTimeout(configuredTimeout)
						: executionInfo.CancellationCauseOverride;
			}

			OrchestrationResult? orchResult;
			try
			{
				// Build a ParentExecutionContext to forward to the engine for run-record lineage.
				// Use the depth as recorded on the active execution info (which the launcher set
				// based on the live parent metadata). That ensures the engine writes the correct
				// depth into the OrchestrationRunRecord.
				var engineParentContext = request.ParentContext is null
					? null
					: new ParentExecutionContext
					{
						ParentExecutionId = request.ParentContext.ParentExecutionId,
						ParentStepName = request.ParentContext.ParentStepName,
						RootExecutionId = executionInfo.NestingMetadata?.RootExecutionId,
						// The engine adds 1 to Depth, so we pass the parent's depth (= child depth - 1).
						Depth = Math.Max(0, (executionInfo.NestingMetadata?.Depth ?? 0) - 1),
					};

				orchResult = await executor.ExecuteAsync(
					orchestration,
					request.Parameters,
					triggerId: request.TriggerId,
					preExecutionParameterTransform: preExecutionParameterTransform,
					parentContext: engineParentContext,
					executionIdOverride: executionInfo.ExecutionId,
					resolveExternalCancellationCause: cancellationCauseProbe,
					triggeredBy: request.TriggeredBy,
					cancellationToken: executorToken).ConfigureAwait(false);
			}
			catch (OperationCanceledException) when (
				syncTimeoutCts is not null
				&& syncTimeoutCts.IsCancellationRequested
				&& !cts.IsCancellationRequested)
			{
				// Caller-specified sync timeout fired without parent cancellation
				timedOut = true;
				LogSyncTimeout(executionInfo.ExecutionId, request.TimeoutSeconds!.Value);
				if (reporter is SseReporter sseTimeout)
					sseTimeout.ReportOrchestrationError(
						$"Orchestration timed out after {request.TimeoutSeconds} seconds.");
				executionInfo.Status = HostExecutionStatus.Failed;
				return BuildResult(
					request,
					orchestration,
					executionInfo,
					ExecutionStatus.Cancelled,
					orchResult: null,
					errorMessage: $"Orchestration did not complete within {request.TimeoutSeconds} seconds.",
					finalContent: null,
					startedAt: startedAt,
					timedOut: true);
			}
			catch (OperationCanceledException)
			{
				// Cancellation from parent or external token
				LogChildCancelled(executionInfo.ExecutionId, executionInfo.OrchestrationName);
				if (reporter is SseReporter sseCancel)
					sseCancel.ReportOrchestrationCancelled();
				executionInfo.Status = HostExecutionStatus.Cancelled;
				return BuildResult(
					request,
					orchestration,
					executionInfo,
					ExecutionStatus.Cancelled,
					orchResult: null,
					errorMessage: "Orchestration was cancelled.",
					finalContent: null,
					startedAt: startedAt,
					timedOut: false);
			}
			catch (Exception ex)
			{
				LogChildExecutionFailed(executionInfo.ExecutionId, executionInfo.OrchestrationName, ex);
				if (reporter is SseReporter sseError)
				{
					sseError.ReportStepError("orchestration", ex.Message);
					sseError.ReportOrchestrationError(ex.Message);
				}
				executionInfo.Status = HostExecutionStatus.Failed;
				return BuildResult(
					request,
					orchestration,
					executionInfo,
					ExecutionStatus.Failed,
					orchResult: null,
					errorMessage: ex.Message,
					finalContent: null,
					startedAt: startedAt,
					timedOut: false);
			}

			// 11. Successful or terminal-but-not-thrown path: emit terminal SSE events
			if (reporter is SseReporter sseDone)
			{
				if (orchResult.Status == ExecutionStatus.Cancelled)
				{
					sseDone.ReportOrchestrationCancelled();
				}
				else
				{
					sseDone.ReportOrchestrationDone(orchResult);
				}
			}

			executionInfo.Status = orchResult.Status switch
			{
				ExecutionStatus.Succeeded => HostExecutionStatus.Completed,
				ExecutionStatus.Cancelled => HostExecutionStatus.Cancelled,
				_ => HostExecutionStatus.Failed,
			};

			// If the engine returned a Cancelled result and our sync-invoke wrapper owned
			// the cancellation, surface that to MCP callers as a timeout (status="timeout")
			// even though the engine cleaned up gracefully without throwing.
			if (!timedOut
				&& orchResult.Status == ExecutionStatus.Cancelled
				&& orchResult.Cancellation?.Kind == CancellationCauseKind.SyncInvokeTimeout)
			{
				timedOut = true;
				LogSyncTimeout(executionInfo.ExecutionId, request.TimeoutSeconds!.Value);
			}

			var finalContent = BuildFinalContent(orchResult);
			string? errorMessage = orchResult.Status == ExecutionStatus.Succeeded
				? null
				: orchResult.Cancellation is { } cancel
					? $"Child orchestration ended with status '{orchResult.Status}': {cancel.Reason}."
					: $"Child orchestration ended with status '{orchResult.Status}'.";

			return BuildResult(
				request,
				orchestration,
				executionInfo,
				orchResult.Status,
				orchResult,
				errorMessage,
				finalContent,
				startedAt,
				timedOut);
		}
		finally
		{
			// Always complete reporter and schedule cleanup so cancellation does not leak resources
			if (reporter is SseReporter sseFinal)
			{
				try { sseFinal.Complete(); } catch { /* best-effort */ }
			}

			// Dispose source-token registrations to detach them from the source CTSs. This
			// is important when the parent's CTS outlives this child (which it normally
			// does for sync invokes): otherwise the parent CTS would hold a closure over
			// our executionInfo for the parent's lifetime.
			try { externalTokenRegistration.Dispose(); } catch { /* best-effort */ }
			try { parentTokenRegistration.Dispose(); } catch { /* best-effort */ }

			syncTimeoutCts?.Dispose();

			ScheduleCleanup(executionInfo.ExecutionId, cts);
		}
	}

	private static ChildOrchestrationResult BuildResult(
		ChildLaunchRequest request,
		Orchestration orchestration,
		ActiveExecutionInfo info,
		ExecutionStatus status,
		OrchestrationResult? orchResult,
		string? errorMessage,
		string? finalContent,
		DateTimeOffset startedAt,
		bool timedOut)
	{
		return new ChildOrchestrationResult
		{
			ExecutionId = info.ExecutionId,
			OrchestrationId = info.OrchestrationId,
			OrchestrationName = orchestration.Name,
			Status = status,
			OrchestrationResult = orchResult,
			ErrorMessage = errorMessage,
			FinalContent = finalContent,
			StartedAt = startedAt,
			CompletedAt = DateTimeOffset.UtcNow,
			TimedOut = timedOut,
		};
	}

	private static string? BuildFinalContent(OrchestrationResult result)
	{
		// Concatenate terminal step contents to produce a single summary string,
		// matching the historical shape returned by InvokeOrchestration.
		var terminal = result.Results
			.Where(kvp => kvp.Value.Status == ExecutionStatus.Succeeded)
			.Select(kvp => $"[{kvp.Key}]\n{kvp.Value.Content}")
			.ToArray();
		if (terminal.Length == 0) return null;
		return string.Join("\n---\n", terminal);
	}

	private void ScheduleCleanup(string executionId, CancellationTokenSource cts)
	{
		// Detach to a background task so the launcher's completion task can return promptly.
		_ = Task.Run(async () =>
		{
			try
			{
				await Task.Delay(PostCompletionRetention).ConfigureAwait(false);
			}
			catch (TaskCanceledException) { /* shutdown */ }
			finally
			{
				_activeExecutions.TryRemove(executionId, out _);
				_activeExecutionInfos.TryRemove(executionId, out _);
				try { cts.Dispose(); } catch (ObjectDisposedException) { }
			}
		});
	}

	[LoggerMessage(Level = LogLevel.Information,
		Message = "Child orchestration launched: executionId={ExecutionId}, name={OrchestrationName}, triggeredBy={TriggeredBy}, depth={Depth}")]
	private partial void LogChildLaunched(string executionId, string orchestrationName, string triggeredBy, int depth);

	[LoggerMessage(Level = LogLevel.Warning,
		Message = "Child orchestration {ExecutionId} hit caller-supplied sync timeout ({TimeoutSeconds}s).")]
	private partial void LogSyncTimeout(string executionId, int timeoutSeconds);

	[LoggerMessage(Level = LogLevel.Information,
		Message = "Child orchestration {ExecutionId} ({OrchestrationName}) was cancelled.")]
	private partial void LogChildCancelled(string executionId, string orchestrationName);

	[LoggerMessage(Level = LogLevel.Error,
		Message = "Child orchestration {ExecutionId} ({OrchestrationName}) failed with an unhandled exception.")]
	private partial void LogChildExecutionFailed(string executionId, string orchestrationName, Exception ex);
}
