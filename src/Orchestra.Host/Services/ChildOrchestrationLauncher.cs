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
		ConcurrentDictionary<string, ActiveExecutionInfo> activeExecutionInfos)
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
		if (!string.IsNullOrWhiteSpace(request.OrchestrationPath))
		{
			entryPath = request.OrchestrationPath;
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
		}

		// 2. Parse orchestration file (with global MCPs)
		Orchestration orchestration;
		try
		{
			orchestration = OrchestrationParser.ParseOrchestrationFile(entryPath, _registry.GlobalMcps);
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

		// 5. Create cancellation token source (linked to parent's CTS when nested)
		CancellationTokenSource cts;
		if (request.ParentContext is not null &&
			_activeExecutions.TryGetValue(request.ParentContext.ParentExecutionId, out var parentCts))
		{
			cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, parentCts.Token);
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
			OrchestrationId = request.OrchestrationId,
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
			serverUrl: _hostOptions.HostBaseUrl);

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
			startedAt);

		var handle = new ChildOrchestrationHandle
		{
			ExecutionId = executionId,
			OrchestrationId = request.OrchestrationId,
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
	}

	private async Task<ChildOrchestrationResult> RunCompletionAsync(
		OrchestrationExecutor executor,
		Orchestration orchestration,
		ChildLaunchRequest request,
		ActiveExecutionInfo executionInfo,
		IOrchestrationReporter reporter,
		CancellationTokenSource cts,
		Func<CancellationToken, Task<Dictionary<string, string>?>>? preExecutionParameterTransform,
		DateTimeOffset startedAt)
	{
		// In sync mode, apply an optional caller-specified hard timeout. Async mode honors
		// only the orchestration's own timeoutSeconds (handled inside the executor) and the
		// linked parent CTS (if any).
		CancellationTokenSource? syncTimeoutCts = null;
		var executorToken = cts.Token;
		var timedOut = false;

		try
		{
			if (request.Mode == ChildLaunchMode.Sync && request.TimeoutSeconds is > 0)
			{
				syncTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
				syncTimeoutCts.CancelAfter(TimeSpan.FromSeconds(request.TimeoutSeconds.Value));
				executorToken = syncTimeoutCts.Token;
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
					foreach (var (stepName, stepResult) in orchResult.StepResults)
					{
						if (stepResult.Status == ExecutionStatus.Succeeded)
							sseDone.ReportStepOutput(stepName, stepResult.Content);
					}
					sseDone.ReportOrchestrationDone(orchResult);
				}
			}

			executionInfo.Status = orchResult.Status switch
			{
				ExecutionStatus.Succeeded => HostExecutionStatus.Completed,
				ExecutionStatus.Cancelled => HostExecutionStatus.Cancelled,
				_ => HostExecutionStatus.Failed,
			};

			var finalContent = BuildFinalContent(orchResult);
			string? errorMessage = orchResult.Status == ExecutionStatus.Succeeded
				? null
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
			OrchestrationId = request.OrchestrationId,
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
