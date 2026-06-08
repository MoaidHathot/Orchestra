using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Orchestra.Engine;

public partial class OrchestrationExecutor
{
	private readonly IScheduler _scheduler;
	private readonly AgentBuilder _agentBuilder;
	private readonly IOrchestrationReporter _reporter;
	private readonly IPromptFormatter _promptFormatter;
	private readonly ILoggerFactory _loggerFactory;
	private readonly ILogger<OrchestrationExecutor> _logger;
	private readonly IRunStore _runStore;
	private readonly ICheckpointStore _checkpointStore;
	private readonly IPendingInputStore _pendingInputStore;
	private readonly IHumanInputWaiter _humanInputWaiter;
	private readonly StepExecutorRegistry _stepExecutorRegistry;
	private readonly EngineToolRegistry _engineToolRegistry;
	private readonly string? _dataPath;
	private readonly string? _serverUrl;
	private readonly HookDefinition[] _globalHooks;

	public OrchestrationExecutor(
		IScheduler scheduler,
		AgentBuilder agentBuilder,
		IOrchestrationReporter reporter,
		ILoggerFactory loggerFactory,
		IPromptFormatter? promptFormatter = null,
		IRunStore? runStore = null,
		ICheckpointStore? checkpointStore = null,
		StepExecutorRegistry? stepExecutorRegistry = null,
		EngineToolRegistry? engineToolRegistry = null,
		IMcpResolver? mcpResolver = null,
		IChildOrchestrationLauncher? childLauncher = null,
		HookDefinition[]? globalHooks = null,
		string? dataPath = null,
		string? serverUrl = null,
		IPendingInputStore? pendingInputStore = null,
		IHumanInputWaiter? humanInputWaiter = null)
	{
		_scheduler = scheduler;
		_agentBuilder = agentBuilder;
		_reporter = reporter;
		_promptFormatter = promptFormatter ?? DefaultPromptFormatter.Instance;
		_loggerFactory = loggerFactory;
		_logger = loggerFactory.CreateLogger<OrchestrationExecutor>();
			_runStore = runStore ?? NullRunStore.Instance;
			_checkpointStore = checkpointStore ?? NullCheckpointStore.Instance;
			_pendingInputStore = pendingInputStore ?? NullPendingInputStore.Instance;
			_humanInputWaiter = humanInputWaiter ?? NullHumanInputWaiter.Instance;
			_globalHooks = MarkHookSources(globalHooks ?? [], HookSource.Global);
			_dataPath = dataPath;
			_serverUrl = serverUrl;
			_engineToolRegistry = engineToolRegistry ?? EngineToolRegistry.CreateDefault();

		// If no registry is provided, create a default one with all built-in step types
		if (stepExecutorRegistry is not null)
		{
			_stepExecutorRegistry = stepExecutorRegistry;
		}
		else
		{
			var promptExecutor = new PromptExecutor(agentBuilder, reporter, _promptFormatter, loggerFactory.CreateLogger<PromptExecutor>(), _engineToolRegistry, mcpResolver,
				pendingInputStore: _pendingInputStore,
				humanInputWaiter: _humanInputWaiter,
				serverUrl: _serverUrl);
			_stepExecutorRegistry = new StepExecutorRegistry()
				.Register(new PromptStepExecutor(promptExecutor))
				.Register(new HttpStepExecutor(new System.Net.Http.HttpClient(), reporter, loggerFactory.CreateLogger<HttpStepExecutor>()))
				.Register(new TransformStepExecutor(loggerFactory.CreateLogger<TransformStepExecutor>(), reporter))
				.Register(new CommandStepExecutor(reporter, loggerFactory.CreateLogger<CommandStepExecutor>()))
				.Register(new ScriptStepExecutor(reporter, loggerFactory.CreateLogger<ScriptStepExecutor>()))
				.Register(new ApprovalStepExecutor(_pendingInputStore, _humanInputWaiter, reporter, loggerFactory.CreateLogger<ApprovalStepExecutor>()));

			// Only register the Orchestration step executor when a launcher is supplied;
			// without one there is no way to invoke child orchestrations.
			if (childLauncher is not null)
			{
				_stepExecutorRegistry.Register(new OrchestrationStepExecutor(
					childLauncher,
					agentBuilder,
					reporter,
					loggerFactory.CreateLogger<OrchestrationStepExecutor>()));
			}
		}
	}

	public async Task<OrchestrationResult> ExecuteAsync(
		Orchestration orchestration,
		Dictionary<string, string>? parameters = null,
		string? triggerId = null,
		Func<CancellationToken, Task<Dictionary<string, string>?>>? preExecutionParameterTransform = null,
		RetryMetadata? retryMetadata = null,
		ParentExecutionContext? parentContext = null,
		string? executionIdOverride = null,
		ResolveCancellationCauseDelegate? resolveExternalCancellationCause = null,
		string? triggeredBy = null,
		CancellationToken cancellationToken = default)
	{
		LogStartingOrchestration(orchestration.Name);

		parameters = ValidateAndApplyDefaults(orchestration, parameters);

		// Scheduler validates the DAG (detects cycles, missing deps)
		_ = _scheduler.Schedule(orchestration);

		// Validate all template expressions before execution
		var parseValidation = TemplateExpressionValidator.ValidateOrchestration(orchestration);
		if (!parseValidation.IsValid)
			throw new InvalidOperationException(parseValidation.FormatErrors());

		// Apply orchestration-level timeout if configured
		CancellationTokenSource? orchestrationTimeoutCts = null;
		var effectiveCancellationToken = cancellationToken;

		if (orchestration.TimeoutSeconds is > 0)
		{
			orchestrationTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			orchestrationTimeoutCts.CancelAfter(TimeSpan.FromSeconds(orchestration.TimeoutSeconds.Value));
			effectiveCancellationToken = orchestrationTimeoutCts.Token;
			LogOrchestrationTimeout(orchestration.Name, orchestration.TimeoutSeconds.Value);
		}

		try
		{
			return await ExecuteCoreAsync(orchestration, parameters, triggerId, effectiveCancellationToken, cancellationToken, preExecutionParameterTransform: preExecutionParameterTransform, retryMetadata: retryMetadata, parentContext: parentContext, executionIdOverride: executionIdOverride, orchestrationTimeoutCts: orchestrationTimeoutCts, resolveExternalCancellationCause: resolveExternalCancellationCause, triggeredBy: triggeredBy);
		}
		catch (OperationCanceledException) when (orchestrationTimeoutCts is not null && orchestrationTimeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
		{
			LogOrchestrationTimedOut(orchestration.Name, orchestration.TimeoutSeconds!.Value);
			throw new TimeoutException($"Orchestration '{orchestration.Name}' timed out after {orchestration.TimeoutSeconds} seconds.");
		}
		finally
		{
			orchestrationTimeoutCts?.Dispose();
		}
	}

	private async Task<OrchestrationResult> ExecuteCoreAsync(
		Orchestration orchestration,
		Dictionary<string, string>? parameters,
		string? triggerId,
		CancellationToken cancellationToken,
		CancellationToken externalCancellationToken,
		CheckpointData? checkpoint = null,
		Func<CancellationToken, Task<Dictionary<string, string>?>>? preExecutionParameterTransform = null,
		RetryMetadata? retryMetadata = null,
		ParentExecutionContext? parentContext = null,
		string? executionIdOverride = null,
		CancellationTokenSource? orchestrationTimeoutCts = null,
		ResolveCancellationCauseDelegate? resolveExternalCancellationCause = null,
		string? triggeredBy = null)
	{
		var runId = retryMetadata?.OverrideRunId ?? checkpoint?.RunId ?? executionIdOverride ?? Guid.NewGuid().ToString("N")[..12];
		var runStartedAt = checkpoint?.StartedAt ?? DateTimeOffset.UtcNow;

		// Create a run-scoped client for isolation: each orchestration run gets its own
		// CLI process. All steps within this run share the client (each gets its own session).
		// The client is disposed when the run ends, preventing stale connections across runs.
		LogRunScopeAboutToCreate(runId, Environment.CurrentManagedThreadId);
		await using var runScope = await _agentBuilder.CreateRunScopeAsync(orchestration.AgentPool, cancellationToken).ConfigureAwait(false);
		LogRunScopeReady(runId, Environment.CurrentManagedThreadId);

		// Pre-execution parameter transform (e.g. trigger InputHandlerPrompt) runs INSIDE the
		// run scope so it shares the orchestration's CLI process — it gets its own session,
		// not its own CLI subprocess. The runtime validation of template expressions runs
		// AFTER the transform so transformed parameters are validated against the orchestration.
		if (preExecutionParameterTransform is not null)
		{
			var transformed = await preExecutionParameterTransform(cancellationToken).ConfigureAwait(false);
			if (transformed is not null)
			{
				parameters = transformed;
			}
		}

		var effectiveParams = parameters ?? [];

		// Validate template expressions against final parameters (post-transform).
		var runtimeValidation = TemplateExpressionValidator.ValidateRuntime(orchestration, effectiveParams);
		if (!runtimeValidation.IsValid)
			throw new InvalidOperationException(runtimeValidation.FormatErrors());

		// Create temp file store if a data path is configured
		OrchestrationTempFileStore? tempFileStore = null;
		if (_dataPath is not null)
		{
			tempFileStore = new OrchestrationTempFileStore(_dataPath, orchestration.Name, runId);
		}

		var hookRuntime = new HookRuntime(_loggerFactory, _serverUrl, _reporter);
		var hooks = CombineHooks(_globalHooks, orchestration.Hooks);
		var hookExecutions = new ConcurrentQueue<HookExecutionRecord>();
		var stepResults = new ConcurrentDictionary<string, ExecutionResult>();
		var stepRecords = new ConcurrentDictionary<string, StepRunRecord>();
		var allStepRecords = new ConcurrentDictionary<string, StepRunRecord>();

		// Build the lookup once so the awaiting-input callback can find the step entry.
		var allSteps = orchestration.Steps.ToDictionary(s => s.Name, s => s);

		// Wire clock-pause: when a step begins waiting we record the start; when it ends
		// we re-arm the orchestration timeout CTS to compensate for the waited duration.
		var clockPause = orchestration.PauseTimeoutDuringWait
			&& orchestrationTimeoutCts is not null
			&& orchestration.TimeoutSeconds is > 0
			? new ClockPauseTracker(orchestrationTimeoutCts, orchestration.TimeoutSeconds.Value, runStartedAt)
			: null;

		var context = new OrchestrationExecutionContext
		{
			Parameters = effectiveParams,
			OrchestrationInfo = new OrchestrationInfo(
				orchestration.Name,
				orchestration.Version,
				runId,
				runStartedAt,
				orchestration.SourcePath,
				orchestration.SourceDirectory),
			RootExecutionId = parentContext is null
				? runId
				: (parentContext.RootExecutionId ?? parentContext.ParentExecutionId),
			Variables = orchestration.Variables,
			DefaultSystemPromptMode = orchestration.DefaultSystemPromptMode,
			DefaultRetryPolicy = orchestration.DefaultRetryPolicy,
			DefaultModel = orchestration.DefaultModel,
			DefaultStepTimeoutSeconds = orchestration.DefaultStepTimeoutSeconds,
			DefaultEnableTools = orchestration.DefaultEnableTools,
			DefaultFailOnToolError = orchestration.DefaultFailOnToolError,
			PauseTimeoutDuringWait = orchestration.PauseTimeoutDuringWait,
			TempFileStore = tempFileStore,
			ServerUrl = _serverUrl,
			OnAwaitingInput = hooks.Length == 0 && clockPause is null ? null : record =>
			{
				clockPause?.BeginWait();
				if (hooks.Length > 0)
				{
					_ = FireAwaitingInputHookSafeAsync(
						hookRuntime, hooks, orchestration, runId, runStartedAt, triggerId,
						stepRecords, allSteps, record, hookExecutions);
				}
			},
			OnInputResolved = clockPause is null ? null : (_, _) => clockPause.EndWait(),
		};

		// CancellationTokenSource for orchestration-complete signals.
		// When a step calls orchestra_complete, this CTS is triggered to cancel all remaining steps.
		using var orchestrationCompleteCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		var effectiveStepToken = orchestrationCompleteCts.Token;

		// Track orchestration-complete signal details (set by the step that triggers it)
		ExecutionStatus? orchestrationCompleteStatus = null;
		string? orchestrationCompleteReason = null;
		string? orchestrationCompleteStepName = null;

		// Step lookup is already built above so the hook callback can find the step entry.

		if (checkpoint is null)
		{
			await SaveInitialCheckpointAsync(runId, orchestration, runStartedAt, effectiveParams, triggerId, CancellationToken.None);
		}

		// Restore completed steps from checkpoint if resuming
		if (checkpoint is not null)
		{
			LogResumingFromCheckpoint(orchestration.Name, runId, checkpoint.CompletedSteps.Count);

			foreach (var (stepName, stepCheckpoint) in checkpoint.CompletedSteps)
			{
				var result = stepCheckpoint.ToExecutionResult();
				foreach (var savedFile in result.SavedFiles)
				{
					tempFileStore?.RegisterFileForStep(stepName, savedFile, includeInAllFiles: true);
				}
				context.AddResult(stepName, result);
				stepResults[stepName] = result;

				// Build a step record for the restored step
				if (allSteps.TryGetValue(stepName, out var step))
				{
					var record = new StepRunRecord
					{
						StepName = stepName,
						Status = result.Status,
						StartedAt = checkpoint.StartedAt,
						CompletedAt = checkpoint.CheckpointedAt,
						Content = result.Content,
						RawContent = result.RawContent,
						ErrorMessage = result.ErrorMessage,
						Parameters = step.Parameters
							.Where(p => effectiveParams.ContainsKey(p))
							.ToDictionary(p => p, p => effectiveParams[p]),
						RawDependencyOutputs = result.RawDependencyOutputs,
						PromptSent = result.PromptSent,
						ActualModel = result.ActualModel,
						RequestedModelInfo = result.RequestedModelInfo,
						SelectedModelInfo = result.SelectedModelInfo,
						ActualModelInfo = result.ActualModelInfo,
						SavedFiles = result.SavedFiles,
						// Preserve child-orchestration lineage through restore so the
						// retry's own run.json carries the pointer triple — symmetric
						// with how OrchestrationStepExecutor populates these on fresh
						// runs.
						ChildExecutionId = stepCheckpoint.ChildExecutionId,
						ChildOrchestrationName = stepCheckpoint.ChildOrchestrationName,
						ChildStatus = stepCheckpoint.ChildStatus,
					};
					stepRecords[stepName] = record;
					allStepRecords[stepName] = record;
					// Publish restored checkpoint records so resumed runs surface their
					// completed steps via mid-run drill-in too (not just freshly-executed ones).
					_reporter.PublishStepRecord(stepName, record);
					_reporter.ReportStepOutput(stepName, result.Content);
				}
			}

			// Rehydrate ChildOrchestrationInfo for any restored Orchestration step.
			//
			// The checkpoint persists ONLY the pointer triple (child executionId / name /
			// status) — not the child's full per-step content — to keep checkpoints small
			// even for deeply-nested orchestration trees. Here we load each child's own
			// run.json via IRunStore and reconstruct the ChildOrchestrationInfo so that
			// downstream steps in this retry can resolve template bindings like
			// {{stepName.steps.<childStep>.output}} and {{stepName.executionId}}, exactly
			// as they would in a fresh run.
			//
			// Failure modes degrade gracefully:
			//   - No IRunStore wired                  → skip; bindings stay unresolved (today's behavior).
			//   - Child's run.json deleted / missing  → skip that step; other steps unaffected.
			//   - Pointer triple null (legacy ckpt)   → skip that step.
			//
			// One disk read per Orchestration step in the parent's lineage — typically
			// a handful per parent, well below 100ms total even for deeply nested cases.
			if (_runStore is not NullRunStore)
			{
				foreach (var (stepName, stepCheckpoint) in checkpoint.CompletedSteps)
				{
					if (stepCheckpoint.ChildExecutionId is null
						|| stepCheckpoint.ChildOrchestrationName is null)
					{
						continue;
					}

					OrchestrationRunRecord? childRecord;
					try
					{
						childRecord = await _runStore.GetRunAsync(
							stepCheckpoint.ChildOrchestrationName,
							stepCheckpoint.ChildExecutionId,
							cancellationToken).ConfigureAwait(false);
					}
					catch (Exception ex)
					{
						LogChildRehydrationFailed(stepName, stepCheckpoint.ChildExecutionId, ex);
						continue;
					}

					if (childRecord is null)
					{
						LogChildRehydrationMissing(stepName, stepCheckpoint.ChildExecutionId);
						continue;
					}

					var rehydrated = ProjectRehydratedChildInfo(stepCheckpoint, childRecord);

					// Overwrite the previously-restored result with one that carries the
					// rehydrated ChildOrchestrationInfo. The execution shape is otherwise
					// unchanged — only the child-info side-channel is populated.
					if (!stepResults.TryGetValue(stepName, out var restored))
					{
						continue;
					}
					var enriched = CloneResultWithChildInfo(restored, rehydrated);
					context.AddResult(stepName, enriched);  // overwrites
					stepResults[stepName] = enriched;
				}
			}
		}

		// Track completion via TaskCompletionSource per step
		var completionSources = new Dictionary<string, TaskCompletionSource<ExecutionResult>>();
		foreach (var step in orchestration.Steps)
		{
			completionSources[step.Name] = new TaskCompletionSource<ExecutionResult>(TaskCreationOptions.RunContinuationsAsynchronously);

			// For resumed steps, immediately complete the TCS
			if (stepResults.ContainsKey(step.Name))
			{
				completionSources[step.Name].TrySetResult(stepResults[step.Name]);
			}
		}

		// Build reverse dependency map: step -> list of steps that depend on it
		var dependents = new Dictionary<string, List<string>>();
		foreach (var step in orchestration.Steps)
		{
			dependents[step.Name] = [];
		}
		foreach (var step in orchestration.Steps)
		{
			foreach (var dep in step.DependsOn)
			{
				dependents[dep].Add(step.Name);
			}
		}

		var totalSteps = orchestration.Steps.Length;

		// Guards against launching the same step twice when multiple
		// dependencies complete simultaneously and both try to launch it.
		var launchedSteps = new ConcurrentDictionary<string, byte>();

		// Launch a step when all its dependencies are complete
		void TryLaunchStep(string stepName)
		{
			// Skip steps that were already completed from checkpoint
			if (stepResults.ContainsKey(stepName))
				return;

			// Atomically claim the launch — only the first caller proceeds
			if (!launchedSteps.TryAdd(stepName, 0))
				return;

			LogStepLaunchScheduled(stepName, Environment.CurrentManagedThreadId);
			LogAsyncLocalDiagnostic("before-StartNew", _agentBuilder.GetRunScopedClientDiagnostic() ?? "null", Environment.CurrentManagedThreadId);

			_ = Task.Factory.StartNew(async () =>
			{
				LogStepTaskStarted(stepName, Environment.CurrentManagedThreadId);
				LogAsyncLocalDiagnostic("inside-StartNew", _agentBuilder.GetRunScopedClientDiagnostic() ?? "null", Environment.CurrentManagedThreadId);
				var step = allSteps[stepName];
				var stepExecutor = _stepExecutorRegistry.Resolve(step.Type);
				var stepStartedAt = DateTimeOffset.UtcNow;

				try
				{
		var result = await ExecuteWithRetryAsync(step, stepExecutor, context, stepResults, effectiveStepToken);

				context.AddResult(step.Name, result);
				stepResults[step.Name] = result;

				var record = BuildStepRecord(step, result, effectiveParams, stepStartedAt);
				stepRecords[step.Name] = record;
				allStepRecords[step.Name] = record;
				// Publish so data-plane MCP tools (get_orchestration_step) can serve this
				// step's content mid-run, before the orchestration finalizes its run.json.
				_reporter.PublishStepRecord(step.Name, record);
				await ExecuteStepHooksAsync(hookRuntime, hooks, orchestration, context, runId, runStartedAt, triggerId, stepRecords, allSteps, record, hookExecutions, finalContent: null, cancellationToken: CancellationToken.None).ConfigureAwait(false);

				// Report full step output as soon as the step completes so non-streaming steps
				// (especially Command) are viewable while downstream steps are still running.
				if (result.Status == ExecutionStatus.Succeeded)
				{
					_reporter.ReportStepOutput(step.Name, result.Content);
				}

				// Report step completed/failed/no-action to the reporter so the UI
				// can update step status immediately (not just at orchestration-done).
				// This centralised call ensures all step types (Prompt, Command, HTTP, Transform)
				// emit step-completed events with their step type for correct UI display.
				if (result.Status == ExecutionStatus.Succeeded || result.Status == ExecutionStatus.NoAction)
				{
					_reporter.ReportStepCompleted(step.Name, new AgentResult
					{
						Content = result.Content,
						ActualModel = result.ActualModel,
						SelectedModel = result.SelectedModel,
						RequestedModelInfo = result.RequestedModelInfo,
						SelectedModelInfo = result.SelectedModelInfo,
						ActualModelInfo = result.ActualModelInfo,
					}, step.Type);
				}

					// Handle loop if configured (loop is a Prompt-only feature)
					if (step is PromptOrchestrationStep promptStep && promptStep.Loop is not null && result.Status == ExecutionStatus.Succeeded)
					{
						await HandleLoopAsync(promptStep, allSteps, context, stepResults, stepRecords, allStepRecords, effectiveParams, effectiveStepToken);
					}

					// Save checkpoint before signaling completion so the checkpoint
					// is durable before dependents or WhenAll observers proceed.
					if (result.Status == ExecutionStatus.Succeeded)
					{
						await SaveCheckpointAfterStepAsync(runId, orchestration, runStartedAt, effectiveParams, triggerId, stepResults, step.Name, totalSteps, externalCancellationToken);
					}

					// Check if this step requested orchestration completion
					if (result.OrchestrationCompleteRequested)
					{
						orchestrationCompleteStatus = result.OrchestrationCompleteStatus;
						orchestrationCompleteReason = result.OrchestrationCompleteReason;
						orchestrationCompleteStepName = step.Name;
						LogOrchestrationCompleteRequested(step.Name, orchestrationCompleteReason ?? "No reason provided");

						// Cancel all remaining steps by triggering the linked CTS.
						// Steps already running will observe the cancellation token;
						// steps not yet started will be cancelled when TryLaunchStep checks the token.
						try { orchestrationCompleteCts.Cancel(); } catch (ObjectDisposedException) { }

						// Complete all pending step TCSs with Cancelled results
						foreach (var (name, tcs) in completionSources)
						{
							if (!stepResults.ContainsKey(name))
							{
								var cancelledResult = ExecutionResult.Cancelled($"Orchestration completed early: {orchestrationCompleteReason ?? "no reason"}");
								context.AddResult(name, cancelledResult);
								stepResults[name] = cancelledResult;

								var cancelRecord = BuildStepRecord(allSteps[name], cancelledResult, effectiveParams, DateTimeOffset.UtcNow);
								stepRecords[name] = cancelRecord;
								allStepRecords[name] = cancelRecord;
								_reporter.PublishStepRecord(name, cancelRecord);

								_reporter.ReportStepCancelled(name);
								tcs.TrySetResult(cancelledResult);
							}
						}
					}

					completionSources[step.Name].TrySetResult(stepResults[step.Name]);
				}
				catch (OperationCanceledException)
				{
					var cancelled = ExecutionResult.Cancelled();
					context.AddResult(step.Name, cancelled);
					stepResults[step.Name] = cancelled;
					var record = BuildStepRecord(step, cancelled, effectiveParams, stepStartedAt);
					stepRecords[step.Name] = record;
					allStepRecords[step.Name] = record;
					_reporter.PublishStepRecord(step.Name, record);
					await ExecuteStepHooksAsync(hookRuntime, hooks, orchestration, context, runId, runStartedAt, triggerId, stepRecords, allSteps, record, hookExecutions, finalContent: null, cancellationToken: CancellationToken.None).ConfigureAwait(false);
					_reporter.ReportStepCancelled(step.Name);
					completionSources[step.Name].TrySetResult(cancelled);
				}
				catch (Exception ex)
				{
					var failed = ExecutionResult.Failed(ex.Message);
					context.AddResult(step.Name, failed);
					stepResults[step.Name] = failed;
					var record = BuildStepRecord(step, failed, effectiveParams, stepStartedAt);
					stepRecords[step.Name] = record;
					allStepRecords[step.Name] = record;
					_reporter.PublishStepRecord(step.Name, record);
					await ExecuteStepHooksAsync(hookRuntime, hooks, orchestration, context, runId, runStartedAt, triggerId, stepRecords, allSteps, record, hookExecutions, finalContent: null, cancellationToken: CancellationToken.None).ConfigureAwait(false);
					_reporter.ReportStepError(step.Name, ex.Message);
					completionSources[step.Name].TrySetResult(failed);
				}

				// After this step completes, check all dependents — launch any that are now ready
				// (but only if orchestration hasn't been completed early or cancelled externally)
				if (!orchestrationCompleteCts.IsCancellationRequested)
				{
					foreach (var dependent in dependents[stepName])
					{
						var allDepsComplete = allSteps[dependent].DependsOn
							.All(dep => stepResults.ContainsKey(dep));

						if (allDepsComplete)
						{
							TryLaunchStep(dependent);
						}
					}
				}
				else
				{
					// Cancellation was requested (external cancel or orchestration-complete).
					// Resolve all pending step TCSs so that Task.WhenAll doesn't hang.
					// The orchestra_complete path already resolves TCSs inline (above),
					// but external cancellation does not — handle it here.
					foreach (var (name, tcs) in completionSources)
					{
						if (!stepResults.ContainsKey(name))
						{
							var cancelledResult = ExecutionResult.Cancelled();
							context.AddResult(name, cancelledResult);
							stepResults[name] = cancelledResult;

							var cancelRecord = BuildStepRecord(allSteps[name], cancelledResult, effectiveParams, DateTimeOffset.UtcNow);
							stepRecords[name] = cancelRecord;
							allStepRecords[name] = cancelRecord;
							_reporter.PublishStepRecord(name, cancelRecord);

						_reporter.ReportStepCancelled(name);
						tcs.TrySetResult(cancelledResult);
					}
				}
			}
			// Use LongRunning so step threads don't compete with the thread pool
			// that ASP.NET Core needs for incoming HTTP requests (e.g., self-referential
			// MCP data-plane calls). Unwrap() converts Task<Task> to Task.
			}, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
		}

		// Start all steps that have zero dependencies (or whose deps are all already complete from checkpoint)
		foreach (var step in orchestration.Steps)
		{
			if (stepResults.ContainsKey(step.Name))
				continue; // Already restored from checkpoint

			var allDepsComplete = step.DependsOn.Length == 0 ||
				step.DependsOn.All(dep => stepResults.ContainsKey(dep));

			if (allDepsComplete)
			{
				LogLaunchingStep(step.Name);
				TryLaunchStep(step.Name);
			}
		}

		// Wait for all steps to complete
		await Task.WhenAll(completionSources.Values.Select(tcs => tcs.Task));

		// Log warnings for any unresolved template expressions
		foreach (var unresolved in context.ResolutionTracker.UnresolvedExpressions)
		{
			LogUnresolvedTemplateExpression(unresolved.StepName, unresolved.Expression);
			_reporter.ReportSessionWarning("unresolved-template",
				$"Step '{unresolved.StepName}' has unresolved template expression: {unresolved.Expression}");
		}

		// Determine the cancellation cause (if any) so it can be persisted on the run
		// record and used to enrich step error messages. Resolution order:
		//   1) The orchestration's own timeoutSeconds fired (we own that CTS).
		//   2) The orchestra_complete tool was invoked by a step.
		//   3) An external token cancelled. Ask the wrapper-supplied probe (e.g. the
		//      sync-invoke timeout owner) before falling back to a generic External cause.
		// If no cancellation occurred (e.g. the run completed normally) cancellationDetails stays null.
		CancellationDetails? cancellationDetails = null;
		var anyStepCancelled = stepResults.Values.Any(r => r.Status == ExecutionStatus.Cancelled);

		if (anyStepCancelled)
		{
			if (orchestrationTimeoutCts is not null
				&& orchestrationTimeoutCts.IsCancellationRequested
				&& !externalCancellationToken.IsCancellationRequested)
			{
				cancellationDetails = CancellationDetails.OrchestrationTimeout(orchestration.TimeoutSeconds!.Value);
			}
			else if (orchestrationCompleteStatus is not null
				&& !externalCancellationToken.IsCancellationRequested
				&& (orchestrationTimeoutCts is null || !orchestrationTimeoutCts.IsCancellationRequested))
			{
				cancellationDetails = CancellationDetails.OrchestrationComplete(
					orchestrationCompleteReason,
					orchestrationCompleteStepName);
			}
			else if (cancellationToken.IsCancellationRequested || externalCancellationToken.IsCancellationRequested)
			{
				cancellationDetails = resolveExternalCancellationCause?.Invoke()
					?? CancellationDetails.External();
			}

			// Compute a progress summary at the moment of cancellation so diagnostics show
			// how far along the run got without forcing consumers to scan per-step records.
			if (cancellationDetails is not null)
			{
				cancellationDetails = AttachProgressSummary(cancellationDetails, orchestration, stepResults, stepRecords);
			}
		}

		// Enrich each Cancelled step's ErrorMessage with the determined cause so
		// the on-disk *-result.json files and run.json carry the precise reason.
		// We only rewrite messages that are still the default "Cancelled" so that
		// any step-specific reason already supplied (e.g. by orchestra_complete)
		// is preserved verbatim.
		if (cancellationDetails is not null)
		{
			var enrichedMessage = $"Cancelled: {cancellationDetails.Reason}";

			foreach (var name in stepResults.Keys.ToArray())
			{
				if (stepResults.TryGetValue(name, out var existing)
					&& existing.Status == ExecutionStatus.Cancelled
					&& IsDefaultCancelledMessage(existing.ErrorMessage))
				{
					var replaced = ExecutionResult.Cancelled(enrichedMessage, existing.SavedFiles);
					stepResults[name] = replaced;
					context.AddResult(name, replaced);

					if (stepRecords.TryGetValue(name, out var record))
					{
						var enriched = CloneRecordWithError(record, enrichedMessage);
						stepRecords[name] = enriched;
						// Republish so any in-flight readers see the enriched error message
						// (the prior publish carried the bare "Cancelled" string).
						_reporter.PublishStepRecord(name, enriched);
					}
					if (allStepRecords.TryGetValue(name, out var allRecord))
					{
						var enrichedAll = CloneRecordWithError(allRecord, enrichedMessage);
						allStepRecords[name] = enrichedAll;
						_reporter.PublishStepRecord(name, enrichedAll);
					}
				}
			}
		}

		var orchestrationResult = OrchestrationResult.From(
			orchestration,
			stepResults,
			orchestrationCompleteStatus,
			orchestrationCompleteReason,
			orchestrationCompleteStepName,
			cancellationDetails,
			tempFileStore?.GetAllFiles() ?? []);

		if (orchestrationResult.Status == ExecutionStatus.Succeeded)
		{
			LogOrchestrationSucceeded(orchestration.Name);
		}
		else if (orchestrationResult.Status == ExecutionStatus.Cancelled)
		{
			LogOrchestrationCancelled(orchestration.Name);
		}
		else
		{
			LogOrchestrationFailed(orchestration.Name);
		}

		// Build and persist the run record.
		// Use CancellationToken.None so the save always completes, even when the
		// orchestration was cancelled — the run record must be persisted to history.
		var runCompletedAt = DateTimeOffset.UtcNow;
		var finalContent = BuildFinalContent(orchestrationResult);
		await ExecuteOrchestrationHooksAsync(hookRuntime, hooks, orchestration, context, runId, runStartedAt, runCompletedAt, triggerId, stepRecords, hookExecutions, finalContent, orchestrationResult.Status, CancellationToken.None).ConfigureAwait(false);

		// Determine the run's TriggeredBy. Resolution order:
		//   1. retryMetadata.TriggeredBy — explicit retry path always wins.
		//   2. triggeredBy parameter — supplied by the caller (e.g. ChildOrchestrationLauncher
		//      forwarding ChildLaunchRequest.TriggeredBy such as "orchestration:<parent>" or "mcp").
		//   3. "manual" — final fallback for direct ExecuteAsync calls without context.
		// Without (2), runs invoked via the data-plane MCP or as child orchestrations were
		// previously persisted as "manual", losing the parent/lineage information that was
		// available on the in-memory ChildLaunchRequest.
		var resolvedTriggeredBy = retryMetadata?.TriggeredBy ?? triggeredBy ?? "manual";

		var runRecord = new OrchestrationRunRecord
		{
			RunId = runId,
			OrchestrationName = orchestration.Name,
			StartedAt = runStartedAt,
			CompletedAt = runCompletedAt,
			Status = orchestrationResult.Status,
			Parameters = effectiveParams,
			TriggerId = triggerId,
			TriggeredBy = resolvedTriggeredBy,
			RetriedFromRunId = retryMetadata?.RetriedFromRunId,
			RetryMode = retryMetadata?.RetryMode,
			StepRecords = stepRecords,
			AllStepRecords = allStepRecords,
			FinalContent = finalContent,
			SavedFiles = orchestrationResult.SavedFiles,
			CompletionReason = orchestrationResult.CompletionReason,
			CompletedByStep = orchestrationResult.CompletedByStep,
			IsIncomplete = orchestrationResult.IsIncomplete,
			Cancellation = orchestrationResult.Cancellation,
			TotalUsage = AggregateTokenUsage(stepRecords.Values),
			HookExecutions = hookExecutions.OrderBy(h => h.StartedAt).ToArray(),
			ParentExecutionId = parentContext?.ParentExecutionId,
			ParentStepName = parentContext?.ParentStepName,
			RootExecutionId = parentContext is null
				? runId
				: (parentContext.RootExecutionId ?? parentContext.ParentExecutionId),
			NestingDepth = parentContext is null ? 0 : parentContext.Depth + 1,
			Context = new RunContext
			{
				RunId = runId,
				OrchestrationName = orchestration.Name,
				OrchestrationVersion = orchestration.Version,
				StartedAt = runStartedAt,
				TriggeredBy = retryMetadata?.TriggeredBy ?? triggeredBy ?? (triggerId is not null ? "trigger" : "manual"),
				TriggerId = triggerId,
				Parameters = effectiveParams,
				Variables = orchestration.Variables,
				ResolvedVariables = new Dictionary<string, string>(context.ResolutionTracker.ResolvedVariables),
				AccessedEnvironmentVariables = new Dictionary<string, string?>(context.ResolutionTracker.AccessedEnvironmentVariables),
			},
		};

		// Report context for live viewers (SSE) before persisting
		if (runRecord.Context is not null)
		{
			_reporter.ReportRunContext(runRecord.Context);
		}

		if (orchestrationResult.Cancellation?.Kind != CancellationCauseKind.HostShutdown)
		{
			try
			{
				await _runStore.SaveRunAsync(runRecord, CancellationToken.None);
			}
			catch (Exception ex)
			{
				LogRunStoreSaveFailed(ex, orchestration.Name, runId);
			}
		}
		else
		{
			LogRunStoreSkippedForHostShutdown(orchestration.Name, runId);
		}

		// Clean up checkpoint now that execution is complete. Host-shutdown cancellation is
		// process-wide interruption, so keep the last durable checkpoint for startup recovery.
		if (orchestrationResult.Cancellation?.Kind != CancellationCauseKind.HostShutdown)
		{
			try
			{
				await _checkpointStore.DeleteCheckpointAsync(orchestration.Name, runId, CancellationToken.None);
			}
			catch (Exception ex)
			{
				LogCheckpointDeleteFailed(ex, orchestration.Name, runId);
			}
		}
		else
		{
			LogCheckpointPreservedForResume(orchestration.Name, runId);
		}

		return orchestrationResult;
	}

	/// <summary>
	/// Resumes a previously interrupted orchestration execution from a checkpoint.
	/// Steps that completed before the interruption are restored from the checkpoint
	/// and not re-executed. Remaining steps execute normally.
	/// </summary>
	public async Task<OrchestrationResult> ResumeAsync(
		Orchestration orchestration,
		CheckpointData checkpoint,
		RetryMetadata? retryMetadata = null,
		ResolveCancellationCauseDelegate? resolveExternalCancellationCause = null,
		CancellationToken cancellationToken = default)
	{
		LogResumingOrchestration(orchestration.Name, checkpoint.RunId);

		_ = ValidateAndApplyDefaults(orchestration, checkpoint.Parameters.Count > 0 ? checkpoint.Parameters : null);

		// Scheduler validates the DAG (detects cycles, missing deps)
		_ = _scheduler.Schedule(orchestration);

		// Validate all template expressions before execution. The runtime template-expression
		// validation against parameters now runs inside ExecuteCoreAsync (post-transform), so
		// only the static parse-time validation happens here.
		var parseValidation = TemplateExpressionValidator.ValidateOrchestration(orchestration);
		if (!parseValidation.IsValid)
			throw new InvalidOperationException(parseValidation.FormatErrors());

		// Apply orchestration-level timeout if configured
		CancellationTokenSource? orchestrationTimeoutCts = null;
		var effectiveCancellationToken = cancellationToken;

		if (orchestration.TimeoutSeconds is > 0)
		{
			orchestrationTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			orchestrationTimeoutCts.CancelAfter(TimeSpan.FromSeconds(orchestration.TimeoutSeconds.Value));
			effectiveCancellationToken = orchestrationTimeoutCts.Token;
			LogOrchestrationTimeout(orchestration.Name, orchestration.TimeoutSeconds.Value);
		}

		try
		{
			return await ExecuteCoreAsync(
				orchestration,
				checkpoint.Parameters.Count > 0 ? checkpoint.Parameters : null,
				checkpoint.TriggerId,
				effectiveCancellationToken,
				cancellationToken,
				checkpoint,
				retryMetadata: retryMetadata,
				orchestrationTimeoutCts: orchestrationTimeoutCts,
				resolveExternalCancellationCause: resolveExternalCancellationCause,
				triggeredBy: retryMetadata?.TriggeredBy ?? "resume");
		}
		catch (OperationCanceledException) when (orchestrationTimeoutCts is not null && orchestrationTimeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
		{
			LogOrchestrationTimedOut(orchestration.Name, orchestration.TimeoutSeconds!.Value);
			throw new TimeoutException($"Orchestration '{orchestration.Name}' timed out after {orchestration.TimeoutSeconds} seconds.");
		}
		finally
		{
			orchestrationTimeoutCts?.Dispose();
		}
	}

	/// <summary>
	/// Saves an initial checkpoint before any step starts so a process failure during
	/// the first running step can restart the run from the beginning.
	/// </summary>
	private async Task SaveInitialCheckpointAsync(
		string runId,
		Orchestration orchestration,
		DateTimeOffset runStartedAt,
		Dictionary<string, string> parameters,
		string? triggerId,
		CancellationToken cancellationToken)
	{
		try
		{
			var checkpointData = new CheckpointData
			{
				RunId = runId,
				OrchestrationName = orchestration.Name,
				StartedAt = runStartedAt,
				CheckpointedAt = DateTimeOffset.UtcNow,
				Parameters = parameters,
				TriggerId = triggerId,
				CompletedSteps = [],
			};

			await _checkpointStore.SaveCheckpointAsync(checkpointData, cancellationToken);
			LogInitialCheckpointSaved(orchestration.Name, runId);
		}
		catch (Exception ex)
		{
			LogCheckpointSaveFailed(ex, orchestration.Name, runId, "<initial>");
		}
	}

	/// <summary>
	/// Saves a checkpoint after a step completes successfully.
	/// </summary>
	private async Task SaveCheckpointAfterStepAsync(
		string runId,
		Orchestration orchestration,
		DateTimeOffset runStartedAt,
		Dictionary<string, string> parameters,
		string? triggerId,
		ConcurrentDictionary<string, ExecutionResult> stepResults,
		string completedStepName,
		int totalSteps,
		CancellationToken cancellationToken)
	{
		try
		{
			var completedSteps = new Dictionary<string, CheckpointStepResult>();
			foreach (var (name, result) in stepResults)
			{
				// Only checkpoint succeeded steps — failed/skipped steps will be re-evaluated on resume
				if (result.Status == ExecutionStatus.Succeeded)
				{
					completedSteps[name] = CheckpointStepResult.FromExecutionResult(result);
				}
			}

			var checkpointData = new CheckpointData
			{
				RunId = runId,
				OrchestrationName = orchestration.Name,
				StartedAt = runStartedAt,
				CheckpointedAt = DateTimeOffset.UtcNow,
				Parameters = parameters,
				TriggerId = triggerId,
				CompletedSteps = completedSteps,
			};

			await _checkpointStore.SaveCheckpointAsync(checkpointData, cancellationToken);

			LogCheckpointSaved(orchestration.Name, runId, completedStepName, completedSteps.Count, totalSteps);
			_reporter.ReportCheckpointSaved(runId, completedStepName, completedSteps.Count, totalSteps);
		}
		catch (Exception ex)
		{
			LogCheckpointSaveFailed(ex, orchestration.Name, runId, completedStepName);
		}
	}

	/// <summary>
	/// Validates orchestration parameters and applies defaults from the input schema.
	/// When <see cref="Orchestration.Inputs"/> is defined, validates types, enum constraints,
	/// and applies defaults for optional inputs. When not defined, falls back to legacy
	/// behavior of collecting required parameter names from step-level <c>Parameters</c> arrays.
	/// </summary>
	/// <returns>The effective parameters dictionary with defaults applied.</returns>
	private static Dictionary<string, string>? ValidateAndApplyDefaults(Orchestration orchestration, Dictionary<string, string>? parameters)
	{
		if (orchestration.Inputs is not null)
			return ValidateWithInputSchema(orchestration, parameters);

		return ValidateLegacyParameters(orchestration, parameters);
	}

	/// <summary>
	/// Legacy parameter validation: collects required parameter names from step-level
	/// <c>Parameters</c> arrays and ensures all are provided. No type checking or defaults.
	/// </summary>
	private static Dictionary<string, string>? ValidateLegacyParameters(Orchestration orchestration, Dictionary<string, string>? parameters)
	{
		var requiredByStep = orchestration.Steps
			.SelectMany(step => step.Parameters.Select(param => (param, step.Name)))
			.GroupBy(x => x.param)
			.ToDictionary(g => g.Key, g => g.Select(x => x.Name).ToArray());

		if (requiredByStep.Count == 0)
			return parameters;

		var missing = parameters is null
			? requiredByStep.Keys.ToArray()
			: requiredByStep.Keys.Except(parameters.Keys).ToArray();

		if (missing.Length > 0)
		{
			var details = string.Join("; ", missing.Select(p =>
				$"'{p}' (required by: {string.Join(", ", requiredByStep[p])})"));

			throw new InvalidOperationException(
				$"Missing required parameters: {details}. " +
				$"Provide them via -param key=value.");
		}

		return parameters;
	}

	/// <summary>
	/// Validates parameters against the orchestration's typed input schema.
	/// Checks required inputs, applies defaults, validates types, and enforces enum constraints.
	/// </summary>
	private static Dictionary<string, string>? ValidateWithInputSchema(Orchestration orchestration, Dictionary<string, string>? parameters)
	{
		var inputs = orchestration.Inputs!;

		if (inputs.Count == 0)
			return parameters;

		var effective = parameters is not null
			? new Dictionary<string, string>(parameters)
			: new Dictionary<string, string>();

		var errors = new List<string>();

		foreach (var (name, definition) in inputs)
		{
			if (effective.TryGetValue(name, out var value) && value.Length > 0)
			{
				// Validate type
				var typeError = ValidateInputType(name, value, definition.Type);
				if (typeError is not null)
					errors.Add(typeError);

				// Validate enum constraint
				if (definition.Enum.Length > 0 &&
					!definition.Enum.Contains(value, StringComparer.OrdinalIgnoreCase))
				{
					errors.Add($"Input '{name}' value '{value}' is not one of the allowed values: {string.Join(", ", definition.Enum)}.");
				}
			}
			else if (definition.Required)
			{
				var desc = definition.Description is not null ? $" ({definition.Description})" : "";
				errors.Add($"Missing required input '{name}'{desc}.");
			}
			else if (definition.Default is not null)
			{
				// Apply default value for optional inputs
				effective[name] = definition.Default;
			}
		}

		if (errors.Count > 0)
		{
			throw new InvalidOperationException(
				$"Input validation failed: {string.Join(" ", errors)} " +
				$"Provide them via -param key=value.");
		}

		return effective.Count > 0 ? effective : null;
	}

	/// <summary>
	/// Validates that a parameter value matches the expected <see cref="InputType"/>.
	/// </summary>
	private static string? ValidateInputType(string name, string value, InputType type)
	{
		return type switch
		{
			InputType.Boolean when !bool.TryParse(value, out _) =>
				$"Input '{name}' expects a boolean value ('true' or 'false'), got '{value}'.",
			InputType.Number when !double.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out _) =>
				$"Input '{name}' expects a numeric value, got '{value}'.",
			_ => null,
		};
	}

	/// <summary>
	/// Wraps <see cref="ExecuteOrSkipStepAsync"/> with retry logic.
	/// Uses the step's own <see cref="OrchestrationStep.Retry"/> policy if defined,
	/// otherwise falls back to the context's <see cref="OrchestrationExecutionContext.DefaultRetryPolicy"/>.
	/// </summary>
	private async Task<ExecutionResult> ExecuteWithRetryAsync(
		OrchestrationStep step,
		IStepExecutor executor,
		OrchestrationExecutionContext context,
		ConcurrentDictionary<string, ExecutionResult> stepResults,
		CancellationToken cancellationToken)
	{
		var retryPolicy = step.Retry ?? context.DefaultRetryPolicy;

		// No retry policy — execute once
		if (retryPolicy is null || retryPolicy.MaxRetries <= 0)
		{
			return await ExecuteOrSkipStepAsync(step, executor, context, stepResults, cancellationToken);
		}

		// First attempt
		var result = await ExecuteOrSkipStepAsync(step, executor, context, stepResults, cancellationToken);

		// Only retry on failures (not skips or successes)
		if (result.Status != ExecutionStatus.Failed)
			return result;

		// Skip retries when the underlying agent client is unhealthy — retrying on a
		// dead client is guaranteed to fail and just wastes the configured retry budget.
		if (result.ErrorCategory == StepErrorCategory.ClientUnhealthy)
		{
			LogStepRetrySkippedClientUnhealthy(step.Name, result.ErrorMessage ?? "(no message)");
			return result;
		}

		// Check if the failure was a timeout and retryOnTimeout is disabled
		var isTimeout = result.ErrorMessage?.Contains("timed out", StringComparison.OrdinalIgnoreCase) == true;
		if (isTimeout && !retryPolicy.RetryOnTimeout)
			return result;

		// Collect retry history
		var retryHistory = new List<RetryAttemptRecord>();

		// Retry loop
		for (var attempt = 1; attempt <= retryPolicy.MaxRetries; attempt++)
		{
			cancellationToken.ThrowIfCancellationRequested();

			var delay = retryPolicy.GetDelay(attempt);
			LogStepRetry(step.Name, attempt, retryPolicy.MaxRetries, result.ErrorMessage ?? "Unknown error", delay.TotalSeconds);
			_reporter.ReportStepRetry(step.Name, attempt, retryPolicy.MaxRetries, result.ErrorMessage ?? "Unknown error", delay);

			// Record this retry attempt
			retryHistory.Add(new RetryAttemptRecord
			{
				Attempt = attempt,
				Error = result.ErrorMessage ?? "Unknown error",
				AttemptedAt = DateTimeOffset.UtcNow,
				DelaySeconds = delay.TotalSeconds,
				ErrorCategory = result.ErrorCategory,
			});

			await Task.Delay(delay, cancellationToken);

			result = await ExecuteOrSkipStepAsync(step, executor, context, stepResults, cancellationToken);

			if (result.Status != ExecutionStatus.Failed)
			{
				// Succeeded after retries — attach retry history to the result
				return new ExecutionResult
				{
					Content = result.Content,
					Status = result.Status,
					ErrorMessage = result.ErrorMessage,
					RawContent = result.RawContent,
					RawDependencyOutputs = result.RawDependencyOutputs,
					PromptSent = result.PromptSent,
					ActualModel = result.ActualModel,
					SelectedModel = result.SelectedModel,
					RequestedModelInfo = result.RequestedModelInfo,
					SelectedModelInfo = result.SelectedModelInfo,
					ActualModelInfo = result.ActualModelInfo,
					Usage = result.Usage,
					Trace = result.Trace,
					SavedFiles = result.SavedFiles,
					RetryHistory = retryHistory,
					ErrorCategory = result.ErrorCategory,
					OrchestrationCompleteRequested = result.OrchestrationCompleteRequested,
					OrchestrationCompleteStatus = result.OrchestrationCompleteStatus,
					OrchestrationCompleteReason = result.OrchestrationCompleteReason,
					OrchestrationCompleteStepName = result.OrchestrationCompleteStepName,
				};
			}

			// Check timeout condition for subsequent retries
			isTimeout = result.ErrorMessage?.Contains("timed out", StringComparison.OrdinalIgnoreCase) == true;
			if (isTimeout && !retryPolicy.RetryOnTimeout)
			{
				LogStepRetryAbortedTimeout(step.Name, attempt);
				break;
			}

			// Stop retrying if the agent client became unhealthy mid-loop.
			if (result.ErrorCategory == StepErrorCategory.ClientUnhealthy)
			{
				LogStepRetrySkippedClientUnhealthy(step.Name, result.ErrorMessage ?? "(no message)");
				break;
			}
		}

		LogStepRetryExhausted(step.Name, retryPolicy.MaxRetries);

		// Attach retry history to the final failed result
		return new ExecutionResult
		{
			Content = result.Content,
			Status = result.Status,
			ErrorMessage = result.ErrorMessage,
			RawContent = result.RawContent,
			RawDependencyOutputs = result.RawDependencyOutputs,
			PromptSent = result.PromptSent,
			ActualModel = result.ActualModel,
			Usage = result.Usage,
			Trace = result.Trace,
			SavedFiles = result.SavedFiles,
			RetryHistory = retryHistory,
			ErrorCategory = result.ErrorCategory,
		};
	}

	private async Task<ExecutionResult> ExecuteOrSkipStepAsync(
		OrchestrationStep step,
		IStepExecutor executor,
		OrchestrationExecutionContext context,
		ConcurrentDictionary<string, ExecutionResult> stepResults,
		CancellationToken cancellationToken)
	{
		// Check for cancellation before starting
		if (cancellationToken.IsCancellationRequested)
		{
			LogStepCancelledBeforeStart(step.Name);
			_reporter.ReportStepCancelled(step.Name);
			return ExecutionResult.Cancelled();
		}

		// Check if this step is disabled
		if (!step.Enabled)
		{
			LogStepDisabled(step.Name);
			_reporter.ReportStepSkipped(step.Name, "Step is disabled (enabled: false)");
			return ExecutionResult.Succeeded(string.Empty);
		}

		// Check if any dependency failed or was skipped
		var shouldSkip = context.HasAnyDependencyFailed(step.DependsOn);
		var failedDeps = shouldSkip
			? step.DependsOn
				.Where(dep => stepResults.TryGetValue(dep, out var r) &&
					r.Status is ExecutionStatus.Failed or ExecutionStatus.Skipped or ExecutionStatus.Cancelled or ExecutionStatus.NoAction)
				.ToArray()
			: [];

		if (shouldSkip)
		{
			// Distinguish between NoAction-based skips and failure-based skips.
			//
			// A dependency contributes a "no-action" cause when either:
			//   (a) its own status is NoAction (the direct case), or
			//   (b) its status is Skipped AND its skip reason was itself driven entirely
			//       by NoAction roots (the transitive case, e.g. a gate set no_action and
			//       multiple layers of dependents got skipped because of it).
			//
			// We treat the entire run as a "nothing to do" skip only when ALL failed deps
			// trace back to NoAction roots — otherwise the skip is rooted in a real
			// failure/cancellation and we preserve the generic message.
			var noActionRoots = new HashSet<string>(StringComparer.Ordinal);
			var allDepsAreNoActionRooted = failedDeps.Length > 0;
			foreach (var dep in failedDeps)
			{
				if (!TryCollectNoActionRoots(dep, stepResults, noActionRoots, visited: []))
				{
					allDepsAreNoActionRooted = false;
				}
			}

			string reason;
			if (allDepsAreNoActionRooted && noActionRoots.Count > 0)
			{
				// Stable, deterministic ordering for diagnostics and tests.
				var orderedRoots = noActionRoots.OrderBy(n => n, StringComparer.Ordinal).ToArray();
				reason = $"{NoActionSkipReasonPrefix} [{string.Join(", ", orderedRoots)}]";
				// Benign cascade: a gate upstream called set_status no_action and the
				// engine is propagating "nothing to do" through the DAG. Log at
				// Information so operators are not paged for the expected case; the
				// dedicated LogStepSkippedDueToNoActionRoots emitter (also Information)
				// already carries the structured root list for filtering.
				LogStepSkippedDueToNoActionRoots(step.Name, string.Join(", ", orderedRoots));
				LogSkippingStepBenign(step.Name, reason);
			}
			else
			{
				reason = $"Skipped because dependencies failed, were cancelled, or were skipped: [{string.Join(", ", failedDeps)}]";
				// Non-benign cascade: an upstream step failed, was cancelled, or was
				// itself skipped for a non-NoAction reason. Keep at Warning so this
				// surfaces in normal log filters.
				LogSkippingStep(step.Name, reason);
			}

			_reporter.ReportStepSkipped(step.Name, reason);
			return ExecutionResult.Skipped(reason);
		}

		LogRunningStep(step.Name);
		_reporter.ReportStepStarted(step.Name);

		// Apply per-step timeout if configured (step-level overrides orchestration default)
		CancellationTokenSource? timeoutCts = null;
		var effectiveToken = cancellationToken;
		var effectiveStepTimeout = step.TimeoutSeconds ?? context.DefaultStepTimeoutSeconds;

		if (effectiveStepTimeout is > 0)
		{
			timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			timeoutCts.CancelAfter(TimeSpan.FromSeconds(effectiveStepTimeout.Value));
			effectiveToken = timeoutCts.Token;
			LogStepTimeout(step.Name, effectiveStepTimeout.Value);
		}

		try
		{
			var result = await executor.ExecuteAsync(step, context, effectiveToken);
			result = EnrichResultTrace(step, context, result);

			if (result.Status == ExecutionStatus.Succeeded)
			{
				LogStepSucceeded(step.Name);
			}
			else if (result.Status == ExecutionStatus.NoAction)
			{
				LogStepNoAction(step.Name, result.Content);
			}
			else
			{
				LogStepFailed(step.Name, result.ErrorMessage);
			}

			return result;
		}
		catch (StepExecutionCanceledException ex) when (timeoutCts is not null && timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
		{
			var message = $"Step timed out after {effectiveStepTimeout} seconds.";
			LogStepTimedOut(step.Name, effectiveStepTimeout!.Value);
			_reporter.ReportStepError(step.Name, message);
			return EnrichResultTrace(step, context, BuildTimeoutResult(message, ex.PartialResult));
		}
		catch (OperationCanceledException) when (timeoutCts is not null && timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
		{
			var message = $"Step timed out after {effectiveStepTimeout} seconds.";
			LogStepTimedOut(step.Name, effectiveStepTimeout!.Value);
			_reporter.ReportStepError(step.Name, message);
			return EnrichResultTrace(step, context, BuildTimeoutResult(message, partialResult: null));
		}
		finally
		{
			timeoutCts?.Dispose();
		}
	}

	private async Task HandleLoopAsync(
		PromptOrchestrationStep checkerStep,
		Dictionary<string, OrchestrationStep> allStepsByName,
		OrchestrationExecutionContext context,
		ConcurrentDictionary<string, ExecutionResult> stepResults,
		ConcurrentDictionary<string, StepRunRecord> stepRecords,
		ConcurrentDictionary<string, StepRunRecord> allStepRecords,
		Dictionary<string, string> effectiveParams,
		CancellationToken cancellationToken)
	{
		var loop = checkerStep.Loop!;

		if (!allStepsByName.TryGetValue(loop.Target, out var targetStepBase))
		{
			LogLoopTargetNotFound(loop.Target, checkerStep.Name);
			return;
		}

		if (targetStepBase is not PromptOrchestrationStep targetStep)
		{
			LogLoopTargetNotFound(loop.Target, checkerStep.Name);
			return;
		}

		var targetExecutor = _stepExecutorRegistry.Resolve(targetStep.Type);
		var checkerExecutor = _stepExecutorRegistry.Resolve(checkerStep.Type);

		for (var iteration = 1; iteration <= loop.MaxIterations; iteration++)
		{
			// Check exit condition on the checker's current result
			var checkerResult = context.GetResult(checkerStep.Name);

			if (checkerResult.Content.Contains(loop.ExitPattern, StringComparison.OrdinalIgnoreCase))
			{
				LogLoopExitConditionMet(checkerStep.Name, iteration - 1);
				return;
			}

			LogLoopIteration(checkerStep.Name, iteration, loop.MaxIterations, loop.Target);
			_reporter.ReportLoopIteration(checkerStep.Name, loop.Target, iteration, loop.MaxIterations);

			// Inject checker's feedback into the target step's context
			context.SetLoopFeedback(loop.Target, checkerResult.Content);
			context.ClearResult(loop.Target);

			// Re-execute the target step
			var targetStartedAt = DateTimeOffset.UtcNow;
			_reporter.ReportStepStarted(loop.Target);
			var targetResult = await targetExecutor.ExecuteAsync(targetStep, context, cancellationToken);
			targetResult = EnrichResultTrace(targetStep, context, targetResult);

			context.AddResult(loop.Target, targetResult);
			stepResults[loop.Target] = targetResult;

			var targetRecord = BuildStepRecord(targetStep, targetResult, effectiveParams, targetStartedAt, iteration);
			stepRecords[loop.Target] = targetRecord;
			allStepRecords[$"{loop.Target}:iteration-{iteration}"] = targetRecord;
			// Publish under BOTH keys so callers can drill into either the canonical
			// (latest-iteration) record or a specific iteration by its iteration suffix.
			_reporter.PublishStepRecord(loop.Target, targetRecord);
			_reporter.PublishStepRecord($"{loop.Target}:iteration-{iteration}", targetRecord);

			if (targetResult.Status != ExecutionStatus.Succeeded)
			{
				LogLoopTargetFailed(loop.Target, iteration);
				return;
			}

			// Re-execute the checker step
			context.ClearResult(checkerStep.Name);

			var checkerStartedAt = DateTimeOffset.UtcNow;
			_reporter.ReportStepStarted(checkerStep.Name);
			var newCheckerResult = await checkerExecutor.ExecuteAsync(checkerStep, context, cancellationToken);
			newCheckerResult = EnrichResultTrace(checkerStep, context, newCheckerResult);

			context.AddResult(checkerStep.Name, newCheckerResult);
			stepResults[checkerStep.Name] = newCheckerResult;

			var checkerRecord = BuildStepRecord(checkerStep, newCheckerResult, effectiveParams, checkerStartedAt, iteration);
			stepRecords[checkerStep.Name] = checkerRecord;
			allStepRecords[$"{checkerStep.Name}:iteration-{iteration}"] = checkerRecord;
			_reporter.PublishStepRecord(checkerStep.Name, checkerRecord);
			_reporter.PublishStepRecord($"{checkerStep.Name}:iteration-{iteration}", checkerRecord);

			if (newCheckerResult.Status != ExecutionStatus.Succeeded)
			{
				LogLoopCheckerFailed(checkerStep.Name, iteration);
				return;
			}
		}

		// Check exit condition one final time after exhausting all iterations
		var finalResult = context.GetResult(checkerStep.Name);

		if (finalResult.Content.Contains(loop.ExitPattern, StringComparison.OrdinalIgnoreCase))
		{
			LogLoopExitConditionMet(checkerStep.Name, loop.MaxIterations);
		}
		else
		{
			LogLoopExhausted(checkerStep.Name, loop.MaxIterations);
		}
	}

	private static ExecutionResult BuildTimeoutResult(string message, ExecutionResult? partialResult) => new()
	{
		Content = string.Empty,
		Status = ExecutionStatus.Failed,
		ErrorMessage = message,
		RawContent = partialResult?.RawContent,
		RawDependencyOutputs = partialResult?.RawDependencyOutputs ?? new Dictionary<string, string>(),
		PromptSent = partialResult?.PromptSent,
		ActualModel = partialResult?.ActualModel,
		SelectedModel = partialResult?.SelectedModel,
		RequestedModelInfo = partialResult?.RequestedModelInfo,
		SelectedModelInfo = partialResult?.SelectedModelInfo,
		ActualModelInfo = partialResult?.ActualModelInfo,
		Usage = partialResult?.Usage,
		Trace = partialResult?.Trace,
		SavedFiles = partialResult?.SavedFiles ?? [],
		RetryHistory = partialResult?.RetryHistory,
		ErrorCategory = StepErrorCategory.Timeout,
	};

	private ExecutionResult EnrichResultTrace(OrchestrationStep step, OrchestrationExecutionContext context, ExecutionResult result)
	{
		var savedFiles = context.TempFileStore?.GetFilesForStep(step.Name) ?? result.SavedFiles;
		var trace = result.Trace?.WithContext(context, step, savedFiles);
		if (trace is not null)
		{
			_reporter.ReportStepTrace(step.Name, trace);
		}

		return new ExecutionResult
		{
			Content = result.Content,
			Status = result.Status,
			ErrorMessage = result.ErrorMessage,
			RawContent = result.RawContent,
			RawDependencyOutputs = result.RawDependencyOutputs,
			PromptSent = result.PromptSent,
			ActualModel = result.ActualModel,
			SelectedModel = result.SelectedModel,
			RequestedModelInfo = result.RequestedModelInfo,
			SelectedModelInfo = result.SelectedModelInfo,
			ActualModelInfo = result.ActualModelInfo,
			Usage = result.Usage,
			Trace = trace,
			SavedFiles = savedFiles,
			RetryHistory = result.RetryHistory,
			ErrorCategory = result.ErrorCategory,
			OrchestrationCompleteRequested = result.OrchestrationCompleteRequested,
			OrchestrationCompleteStatus = result.OrchestrationCompleteStatus,
			OrchestrationCompleteStepName = result.OrchestrationCompleteStepName,
			OrchestrationCompleteReason = result.OrchestrationCompleteReason,
		};
	}

	private static StepRunRecord BuildStepRecord(
		OrchestrationStep step,
		ExecutionResult result,
		Dictionary<string, string> allParams,
		DateTimeOffset startedAt,
		int? loopIteration = null)
	{
		// Extract only the parameters relevant to this step
		var stepParams = new Dictionary<string, string>();
		foreach (var paramName in step.Parameters)
		{
			if (allParams.TryGetValue(paramName, out var value))
			{
				stepParams[paramName] = value;
			}
		}

		return new StepRunRecord
		{
			StepName = step.Name,
			Status = result.Status,
			StartedAt = startedAt,
			CompletedAt = DateTimeOffset.UtcNow,
			Content = result.Content,
			RawContent = result.RawContent,
			ErrorMessage = result.ErrorMessage,
			Parameters = stepParams,
			LoopIteration = loopIteration,
			RawDependencyOutputs = result.RawDependencyOutputs,
			PromptSent = result.PromptSent,
			ActualModel = result.ActualModel,
			SelectedModel = result.SelectedModel,
			RequestedModelInfo = result.RequestedModelInfo,
			SelectedModelInfo = result.SelectedModelInfo,
			ActualModelInfo = result.ActualModelInfo,
			Usage = result.Usage,
			Trace = result.Trace,
			SavedFiles = result.SavedFiles,
			RetryHistory = result.RetryHistory,
			ErrorCategory = result.ErrorCategory,
			ErrorDetails = result.ErrorDetails,
			// Persist only the minimal child reference; the child's full per-step content
			// lives on its own run.json (avoid bloating the parent's record).
			ChildExecutionId = result.ChildOrchestrationInfo?.ExecutionId,
			ChildOrchestrationName = result.ChildOrchestrationInfo?.OrchestrationName,
			ChildStatus = result.ChildOrchestrationInfo?.Status,
		};
	}

	/// <summary>
	/// Projects a child run's persisted <see cref="OrchestrationRunRecord"/> into the
	/// <see cref="ChildOrchestrationInfo"/> shape consumed by parent-step template bindings.
	/// Mirrors the projection in <c>OrchestrationStepExecutor.BuildChildOrchestrationInfo</c>
	/// but reads from <see cref="StepRunRecord"/> entries (persisted shape) rather than
	/// <c>ChildOrchestrationResult</c> (in-process shape).
	/// </summary>
	/// <param name="checkpoint">The parent step's checkpoint, used as the canonical
	/// source for the executionId / orchestrationName / status pointer triple. The child's
	/// run record may report a different (more recent) terminal status if the child was
	/// retried independently — we prefer the parent's snapshot for consistency with what
	/// downstream templates saw at original-run time.</param>
	/// <param name="childRecord">The child run's persisted record as loaded from
	/// <see cref="IRunStore.GetRunAsync"/>.</param>
	private static ChildOrchestrationInfo ProjectRehydratedChildInfo(
		CheckpointStepResult checkpoint,
		OrchestrationRunRecord childRecord)
	{
		var stepResults = childRecord.StepRecords.ToDictionary(
			kvp => kvp.Key,
			kvp => new ChildStepInfo
			{
				Status = kvp.Value.Status,
				Content = kvp.Value.Content,
				RawContent = kvp.Value.RawContent,
				ErrorMessage = kvp.Value.ErrorMessage,
				SavedFiles = kvp.Value.SavedFiles ?? [],
			},
			StringComparer.OrdinalIgnoreCase);

		return new ChildOrchestrationInfo
		{
			ExecutionId = checkpoint.ChildExecutionId ?? childRecord.RunId,
			OrchestrationName = checkpoint.ChildOrchestrationName ?? childRecord.OrchestrationName,
			Status = checkpoint.ChildStatus ?? childRecord.Status,
			ErrorMessage = null,                                 // Top-level error is captured on the parent step's ErrorMessage.
			FinalContent = childRecord.FinalContent,
			CompletionReason = childRecord.CompletionReason,
			Cancellation = childRecord.Cancellation,
			StepResults = stepResults,
			StartedAt = childRecord.StartedAt,
			CompletedAt = childRecord.CompletedAt,
		};
	}

	/// <summary>
	/// Returns a copy of <paramref name="source"/> with its <see cref="ExecutionResult.ChildOrchestrationInfo"/>
	/// replaced by <paramref name="childInfo"/>. Used by the checkpoint-restore path to
	/// attach a rehydrated <see cref="ChildOrchestrationInfo"/> onto the restored result
	/// after <see cref="CheckpointStepResult.ToExecutionResult"/> produced one without it.
	/// </summary>
	private static ExecutionResult CloneResultWithChildInfo(
		ExecutionResult source,
		ChildOrchestrationInfo childInfo)
	{
		return new ExecutionResult
		{
			Status = source.Status,
			Content = source.Content,
			RawContent = source.RawContent,
			ErrorMessage = source.ErrorMessage,
			RawDependencyOutputs = source.RawDependencyOutputs,
			PromptSent = source.PromptSent,
			ActualModel = source.ActualModel,
			SelectedModel = source.SelectedModel,
			RequestedModelInfo = source.RequestedModelInfo,
			SelectedModelInfo = source.SelectedModelInfo,
			ActualModelInfo = source.ActualModelInfo,
			Usage = source.Usage,
			Trace = source.Trace,
			SavedFiles = source.SavedFiles,
			RetryHistory = source.RetryHistory,
			ErrorCategory = source.ErrorCategory,
			OrchestrationCompleteRequested = source.OrchestrationCompleteRequested,
			OrchestrationCompleteStatus = source.OrchestrationCompleteStatus,
			OrchestrationCompleteStepName = source.OrchestrationCompleteStepName,
			OrchestrationCompleteReason = source.OrchestrationCompleteReason,
			ChildOrchestrationInfo = childInfo,
		};
	}

	/// <summary>
	/// Returns true when <paramref name="errorMessage"/> is the bare default emitted by
	/// <see cref="ExecutionResult.Cancelled(string?)"/> with no caller-supplied detail.
	/// Used to decide whether the post-run cancellation-cause enricher may rewrite it.
	/// </summary>
	private static bool IsDefaultCancelledMessage(string? errorMessage) =>
		errorMessage is null || errorMessage == "Cancelled";

	/// <summary>
	/// Prefix used when a step is skipped because <em>all</em> of its dependency failures
	/// trace back to one or more <see cref="ExecutionStatus.NoAction"/> roots. Kept as a
	/// constant so both the producer (the skip-reason builder) and the consumer
	/// (<see cref="TryCollectNoActionRoots"/>) agree on the marker.
	/// </summary>
	private const string NoActionSkipReasonPrefix = "Skipped because dependencies required no action:";

	/// <summary>
	/// Walks back through <paramref name="depName"/>'s ancestry in <paramref name="stepResults"/>
	/// and accumulates the original <see cref="ExecutionStatus.NoAction"/> step names that
	/// caused the cascade.
	/// </summary>
	/// <param name="depName">A direct dependency name of the step being evaluated.</param>
	/// <param name="stepResults">Map of step name → execution result for steps that have completed.</param>
	/// <param name="roots">Accumulator that receives the originating NoAction step names.</param>
	/// <param name="visited">Cycle guard for the recursion.</param>
	/// <returns>
	/// <c>true</c> if <paramref name="depName"/> is rooted (directly or transitively) in a
	/// NoAction step — in which case at least one entry is added to <paramref name="roots"/>.
	/// <c>false</c> if the dep failed, was cancelled, or was skipped for any non-NoAction
	/// reason (or simply is not present in <paramref name="stepResults"/>).
	/// </returns>
	private static bool TryCollectNoActionRoots(
		string depName,
		IReadOnlyDictionary<string, ExecutionResult> stepResults,
		HashSet<string> roots,
		HashSet<string> visited)
	{
		if (!visited.Add(depName))
		{
			// Cycle guard. DAGs shouldn't loop, but be defensive: a previously visited
			// node already contributed (or failed to contribute) its roots; don't recount.
			return true;
		}

		if (!stepResults.TryGetValue(depName, out var result))
		{
			return false;
		}

		if (result.Status == ExecutionStatus.NoAction)
		{
			roots.Add(depName);
			return true;
		}

		if (result.Status == ExecutionStatus.Skipped
			&& result.ErrorMessage is { } msg
			&& msg.StartsWith(NoActionSkipReasonPrefix, StringComparison.Ordinal))
		{
			// Parse out the names from the bracketed list "...: [a, b, c]". Each name
			// must itself be present in stepResults so we can recurse and collect its
			// own NoAction roots (which will normalize the chain to true originators).
			var open = msg.IndexOf('[', StringComparison.Ordinal);
			var close = msg.LastIndexOf(']');
			if (open >= 0 && close > open)
			{
				var inner = msg.Substring(open + 1, close - open - 1);
				var anyResolved = false;
				foreach (var raw in inner.Split(','))
				{
					var name = raw.Trim();
					if (name.Length == 0)
					{
						continue;
					}
					if (TryCollectNoActionRoots(name, stepResults, roots, visited))
					{
						anyResolved = true;
					}
				}
				if (anyResolved)
				{
					return true;
				}
			}
		}

		return false;
	}

	/// <summary>
	/// Returns a new <see cref="CancellationDetails"/> with <see cref="CancellationDetails.Progress"/>
	/// populated from <paramref name="stepResults"/> and <paramref name="stepRecords"/>. The summary
	/// is computed against the steps DECLARED on the orchestration. The semantics are:
	///   <list type="bullet">
	///     <item>StepsCompleted: ended <c>Succeeded</c>.</item>
	///     <item>StepsCancelled: ended <c>Cancelled</c> (covers both interrupted-in-flight and cascade-cancelled).</item>
	///     <item>StepsFailed: ended <c>Failed</c>.</item>
	///     <item>StepsSkippedOrNoAction: ended <c>Skipped</c> or <c>NoAction</c>.</item>
	///     <item>StepsNotStarted: no record at all (computed as a residual).</item>
	///     <item>CancelledSteps: names of steps that ended <c>Cancelled</c>, in declaration order.</item>
	///   </list>
	/// </summary>
	private static CancellationDetails AttachProgressSummary(
		CancellationDetails details,
		Orchestration orchestration,
		IReadOnlyDictionary<string, ExecutionResult> stepResults,
		IReadOnlyDictionary<string, StepRunRecord> stepRecords)
	{
		var totalSteps = orchestration.Steps.Length;
		var stepsCompleted = 0;
		var stepsCancelled = 0;
		var stepsFailed = 0;
		var stepsSkippedOrNoAction = 0;
		var cancelledSteps = new List<string>();

		foreach (var step in orchestration.Steps)
		{
			if (!stepResults.TryGetValue(step.Name, out var result))
			{
				// No execution record at all — step never reached. Counted as not-started below.
				continue;
			}

			switch (result.Status)
			{
				case ExecutionStatus.Succeeded:
					stepsCompleted++;
					break;
				case ExecutionStatus.Cancelled:
					stepsCancelled++;
					cancelledSteps.Add(step.Name);
					break;
				case ExecutionStatus.Failed:
					stepsFailed++;
					break;
				case ExecutionStatus.Skipped:
				case ExecutionStatus.NoAction:
					stepsSkippedOrNoAction++;
					break;
			}
		}

		var stepsNotStarted = Math.Max(
			0,
			totalSteps - (stepsCompleted + stepsCancelled + stepsFailed + stepsSkippedOrNoAction));

		// Find the most-recently-completed step by looking at successful step records' CompletedAt.
		string? lastCompletedStep = null;
		DateTimeOffset? lastCompletedAt = null;
		foreach (var (name, record) in stepRecords)
		{
			if (record.Status != ExecutionStatus.Succeeded)
				continue;

			if (lastCompletedAt is null || record.CompletedAt > lastCompletedAt)
			{
				lastCompletedAt = record.CompletedAt;
				lastCompletedStep = name;
			}
		}

		var progress = new CancellationProgressSummary
		{
			TotalSteps = totalSteps,
			StepsCompleted = stepsCompleted,
			StepsCancelled = stepsCancelled,
			StepsFailed = stepsFailed,
			StepsSkippedOrNoAction = stepsSkippedOrNoAction,
			StepsNotStarted = stepsNotStarted,
			LastCompletedStep = lastCompletedStep,
			LastCompletedAt = lastCompletedAt,
			CancelledSteps = cancelledSteps,
		};

		return new CancellationDetails
		{
			Kind = details.Kind,
			TimeoutSeconds = details.TimeoutSeconds,
			Source = details.Source,
			Detail = details.Detail,
			RequestedAt = details.RequestedAt,
			Progress = progress,
			CallerReason = details.CallerReason,
			CallerSource = details.CallerSource,
			CallerIdentity = details.CallerIdentity,
			CallerAddress = details.CallerAddress,
			CallerUserAgent = details.CallerUserAgent,
		};
	}

	/// <summary>
	/// Returns a copy of <paramref name="record"/> with only its <see cref="StepRunRecord.ErrorMessage"/> replaced.
	/// Used to enrich Cancelled step records with the determined cancellation cause without
	/// losing any of the other fields the original record carried.
	/// </summary>
	private static StepRunRecord CloneRecordWithError(StepRunRecord record, string errorMessage) => new()
	{
		StepName = record.StepName,
		Status = record.Status,
		StartedAt = record.StartedAt,
		CompletedAt = record.CompletedAt,
		Content = record.Content,
		RawContent = record.RawContent,
		ErrorMessage = errorMessage,
		Parameters = record.Parameters,
		LoopIteration = record.LoopIteration,
		RawDependencyOutputs = record.RawDependencyOutputs,
		PromptSent = record.PromptSent,
		ActualModel = record.ActualModel,
		SelectedModel = record.SelectedModel,
		RequestedModelInfo = record.RequestedModelInfo,
		SelectedModelInfo = record.SelectedModelInfo,
		ActualModelInfo = record.ActualModelInfo,
		Usage = record.Usage,
		Trace = record.Trace,
		SavedFiles = record.SavedFiles,
		RetryHistory = record.RetryHistory,
		ErrorCategory = record.ErrorCategory,
	};

	private static string BuildFinalContent(OrchestrationResult orchestrationResult)
	{
		if (orchestrationResult.Status is ExecutionStatus.Cancelled or ExecutionStatus.Failed)
		{
			var summary = new System.Text.StringBuilder();

			if (orchestrationResult.CompletionReason is not null)
			{
				summary.AppendLine($"Orchestration completed early: {orchestrationResult.CompletionReason}");
			}
			else if (orchestrationResult.Cancellation is { } cancel)
			{
				// Surface the structured cancellation cause (timeout vs caller-cancel vs orchestra_complete)
				// directly in the human-readable summary so users do not have to back it out from timestamps.
				summary.AppendLine($"Orchestration was cancelled: {cancel.Reason}.");
			}
			else
			{
				summary.AppendLine(orchestrationResult.Status == ExecutionStatus.Cancelled
					? "Orchestration was cancelled."
					: "Orchestration failed.");
			}

			var succeeded = orchestrationResult.StepResults
				.Where(kv => kv.Value.Status == ExecutionStatus.Succeeded)
				.Select(kv => kv.Key).ToList();
			var failed = orchestrationResult.StepResults
				.Where(kv => kv.Value.Status == ExecutionStatus.Failed)
				.Select(kv => kv.Key).ToList();
			var cancelled = orchestrationResult.StepResults
				.Where(kv => kv.Value.Status == ExecutionStatus.Cancelled)
				.Select(kv => kv.Key).ToList();
			var skipped = orchestrationResult.StepResults
				.Where(kv => kv.Value.Status == ExecutionStatus.Skipped)
				.Select(kv => kv.Key).ToList();
			var noAction = orchestrationResult.StepResults
				.Where(kv => kv.Value.Status == ExecutionStatus.NoAction)
				.Select(kv => kv.Key).ToList();

			if (succeeded.Count > 0)
				summary.AppendLine($"Completed steps: {string.Join(", ", succeeded)}");
			if (noAction.Count > 0)
				summary.AppendLine($"No action steps: {string.Join(", ", noAction)}");
			if (failed.Count > 0)
			{
				summary.AppendLine($"Failed steps: {string.Join(", ", failed)}");
				foreach (var stepName in failed)
				{
					var errorMessage = orchestrationResult.StepResults[stepName].ErrorMessage;
					if (!string.IsNullOrEmpty(errorMessage))
						summary.AppendLine($"  {stepName}: {errorMessage}");
				}
			}
			if (cancelled.Count > 0)
				summary.AppendLine($"Cancelled steps: {string.Join(", ", cancelled)}");
			if (skipped.Count > 0)
				summary.AppendLine($"Skipped steps: {string.Join(", ", skipped)}");

			return summary.ToString();
		}

		// For successful completions, include completion reason if available
		if (orchestrationResult.CompletionReason is not null)
		{
			return $"Orchestration completed: {orchestrationResult.CompletionReason}";
		}

		if (orchestrationResult.Results.Count == 1)
		{
			return orchestrationResult.Results.Values.First().Content;
		}

		return string.Join("\n\n---\n\n",
			orchestrationResult.Results
				.Where(kv => kv.Value.Status == ExecutionStatus.Succeeded)
				.Select(kv => $"## {kv.Key}\n{kv.Value.Content}"));
	}

	private async Task ExecuteStepHooksAsync(
		HookRuntime hookRuntime,
		HookDefinition[] hooks,
		Orchestration orchestration,
		OrchestrationExecutionContext executionContext,
		string runId,
		DateTimeOffset runStartedAt,
		string? triggerId,
		IReadOnlyDictionary<string, StepRunRecord> stepRecords,
		IReadOnlyDictionary<string, OrchestrationStep> allSteps,
		StepRunRecord currentRecord,
		ConcurrentQueue<HookExecutionRecord> hookExecutions,
		string? finalContent,
		CancellationToken cancellationToken)
	{
		if (hooks.Length == 0)
			return;

		var terminalStepNames = GetTerminalStepNames(allSteps);
		var context = new HookExecutionContext
		{
			Orchestration = orchestration,
			ExecutionContext = executionContext,
			RunId = runId,
			RunStartedAt = runStartedAt,
			TriggerId = triggerId,
			StepRecords = stepRecords,
			TerminalStepNames = terminalStepNames,
			CurrentStepRecord = currentRecord,
			FinalContent = finalContent,
		};

		if (currentRecord.Status == ExecutionStatus.Failed)
		{
			EnqueueHookExecutions(hookExecutions, await hookRuntime.ExecuteAsync(hooks, HookEventType.StepFailure, context, cancellationToken).ConfigureAwait(false));
		}
		else if (currentRecord.Status == ExecutionStatus.Succeeded)
		{
			EnqueueHookExecutions(hookExecutions, await hookRuntime.ExecuteAsync(hooks, HookEventType.StepSuccess, context, cancellationToken).ConfigureAwait(false));
		}

		EnqueueHookExecutions(hookExecutions, await hookRuntime.ExecuteAsync(hooks, HookEventType.StepAfter, context, cancellationToken).ConfigureAwait(false));
	}

	private async Task ExecuteOrchestrationHooksAsync(
		HookRuntime hookRuntime,
		HookDefinition[] hooks,
		Orchestration orchestration,
		OrchestrationExecutionContext executionContext,
		string runId,
		DateTimeOffset runStartedAt,
		DateTimeOffset runCompletedAt,
		string? triggerId,
		IReadOnlyDictionary<string, StepRunRecord> stepRecords,
		ConcurrentQueue<HookExecutionRecord> hookExecutions,
		string finalContent,
		ExecutionStatus orchestrationStatus,
		CancellationToken cancellationToken)
	{
		if (hooks.Length == 0)
			return;

		var allSteps = orchestration.Steps.ToDictionary(s => s.Name, s => s);
		var context = new HookExecutionContext
		{
			Orchestration = orchestration,
			ExecutionContext = executionContext,
			RunId = runId,
			RunStartedAt = runStartedAt,
			RunCompletedAt = runCompletedAt,
			TriggerId = triggerId,
			OrchestrationStatus = orchestrationStatus,
			StepRecords = stepRecords,
			TerminalStepNames = GetTerminalStepNames(allSteps),
			FinalContent = finalContent,
		};

		if (orchestrationStatus == ExecutionStatus.Failed)
		{
			EnqueueHookExecutions(hookExecutions, await hookRuntime.ExecuteAsync(hooks, HookEventType.OrchestrationFailure, context, cancellationToken).ConfigureAwait(false));
		}
		else if (orchestrationStatus == ExecutionStatus.Succeeded)
		{
			EnqueueHookExecutions(hookExecutions, await hookRuntime.ExecuteAsync(hooks, HookEventType.OrchestrationSuccess, context, cancellationToken).ConfigureAwait(false));
		}

		EnqueueHookExecutions(hookExecutions, await hookRuntime.ExecuteAsync(hooks, HookEventType.OrchestrationAfter, context, cancellationToken).ConfigureAwait(false));
	}

	private static string[] GetTerminalStepNames(IReadOnlyDictionary<string, OrchestrationStep> allSteps)
	{
		var dependedOn = new HashSet<string>(
			allSteps.Values.SelectMany(s => s.DependsOn),
			StringComparer.OrdinalIgnoreCase);

		return allSteps.Keys
			.Where(name => !dependedOn.Contains(name))
			.ToArray();
	}

	/// <summary>
	/// Fires the <c>step.awaitingInput</c> hook event for a step that has begun waiting
	/// for human input. Wired into <see cref="OrchestrationExecutionContext.OnAwaitingInput"/>
	/// so step executors and engine tools can trigger the notification path. Failures are
	/// logged and swallowed so they don't propagate into the wait.
	/// </summary>
	private async Task FireAwaitingInputHookSafeAsync(
		HookRuntime hookRuntime,
		HookDefinition[] hooks,
		Orchestration orchestration,
		string runId,
		DateTimeOffset runStartedAt,
		string? triggerId,
		IReadOnlyDictionary<string, StepRunRecord> stepRecords,
		IReadOnlyDictionary<string, OrchestrationStep> allSteps,
		PendingInputRecord pending,
		ConcurrentQueue<HookExecutionRecord> hookExecutions)
	{
		try
		{
			// Synthesize a StepRunRecord for the in-progress await so the hook payload's
			// "current step" is meaningful. Status is AwaitingInput.
			var awaitingRecord = new StepRunRecord
			{
				StepName = pending.StepName,
				Status = ExecutionStatus.AwaitingInput,
				StartedAt = pending.CreatedAt,
				CompletedAt = pending.CreatedAt,
				Content = pending.Prompt,
				Parameters = new Dictionary<string, string>(),
				RawDependencyOutputs = new Dictionary<string, string>(),
			};

			// Build a synthetic context. We don't have an OrchestrationExecutionContext at
			// hand here, so construct a minimal one for hook payload formatting.
			var dummyContext = new OrchestrationExecutionContext
			{
				OrchestrationInfo = new OrchestrationInfo(
					orchestration.Name,
					orchestration.Version,
					runId,
					runStartedAt,
					orchestration.SourcePath,
					orchestration.SourceDirectory),
			};

			var ctx = new HookExecutionContext
			{
				Orchestration = orchestration,
				ExecutionContext = dummyContext,
				RunId = runId,
				RunStartedAt = runStartedAt,
				TriggerId = triggerId,
				StepRecords = stepRecords,
				TerminalStepNames = GetTerminalStepNames(allSteps),
				CurrentStepRecord = awaitingRecord,
				FinalContent = pending.Prompt,
			};

			var executions = await hookRuntime.ExecuteAsync(hooks, HookEventType.StepAwaitingInput, ctx, CancellationToken.None).ConfigureAwait(false);
			EnqueueHookExecutions(hookExecutions, executions);
		}
		catch (Exception ex)
		{
			LogAwaitingInputHookFailed(pending.StepName, ex);
		}
	}

	private static HookDefinition[] CombineHooks(HookDefinition[] globalHooks, HookDefinition[] orchestrationHooks)
	{
		var markedOrchestrationHooks = MarkHookSources(orchestrationHooks, HookSource.Orchestration);

		if (globalHooks.Length == 0)
			return markedOrchestrationHooks;
		if (markedOrchestrationHooks.Length == 0)
			return globalHooks;

		return [.. globalHooks, .. markedOrchestrationHooks];
	}

	private static HookDefinition[] MarkHookSources(HookDefinition[] hooks, HookSource source)
	{
		foreach (var hook in hooks)
		{
			hook.Source = source;
		}

		return hooks;
	}

	private static void EnqueueHookExecutions(ConcurrentQueue<HookExecutionRecord> queue, IReadOnlyList<HookExecutionRecord> executions)
	{
		foreach (var execution in executions)
		{
			queue.Enqueue(execution);
		}
	}

	/// <summary>
	/// Aggregates token usage across all step records that have usage data.
	/// Returns null if no steps have token usage.
	/// </summary>
	private static TokenUsage? AggregateTokenUsage(IEnumerable<StepRunRecord> stepRecords)
	{
		var totalInput = 0;
		var totalOutput = 0;
		var totalCacheRead = 0;
		var totalCacheWrite = 0;
		double? totalCost = null;
		double? totalDuration = null;
		var hasUsage = false;

		foreach (var record in stepRecords)
		{
			if (record.Usage is null) continue;
			hasUsage = true;

			totalInput += record.Usage.InputTokens;
			totalOutput += record.Usage.OutputTokens;
			totalCacheRead += record.Usage.CacheReadTokens;
			totalCacheWrite += record.Usage.CacheWriteTokens;

			if (record.Usage.Cost is not null)
				totalCost = (totalCost ?? 0) + record.Usage.Cost.Value;
			if (record.Usage.Duration is not null)
				totalDuration = (totalDuration ?? 0) + record.Usage.Duration.Value;
		}

		if (!hasUsage) return null;

		return new TokenUsage
		{
			InputTokens = totalInput,
			OutputTokens = totalOutput,
			CacheReadTokens = totalCacheRead,
			CacheWriteTokens = totalCacheWrite,
			Cost = totalCost,
			Duration = totalDuration,
		};
	}

	#region Source-Generated Logging

	[LoggerMessage(Level = LogLevel.Information, Message = "Starting orchestration '{Name}'...")]
	private partial void LogStartingOrchestration(string name);

	[LoggerMessage(Level = LogLevel.Information, Message = "Launching step '{StepName}' (no dependencies)")]
	private partial void LogLaunchingStep(string stepName);

	[LoggerMessage(Level = LogLevel.Information, Message = "Orchestration '{Name}' completed successfully.")]
	private partial void LogOrchestrationSucceeded(string name);

	[LoggerMessage(Level = LogLevel.Warning, Message = "Orchestration '{Name}' completed with failures.")]
	private partial void LogOrchestrationFailed(string name);

	[LoggerMessage(Level = LogLevel.Warning, Message = "Orchestration '{Name}' was cancelled.")]
	private partial void LogOrchestrationCancelled(string name);

	[LoggerMessage(Level = LogLevel.Error, Message = "Failed to save run record for orchestration '{Name}', run '{RunId}'.")]
	private partial void LogRunStoreSaveFailed(Exception ex, string name, string runId);

	[LoggerMessage(Level = LogLevel.Information, Message = "Run record for orchestration '{Name}', run '{RunId}' was not saved because the host is shutting down; checkpoint remains resumable.")]
	private partial void LogRunStoreSkippedForHostShutdown(string name, string runId);

	[LoggerMessage(Level = LogLevel.Warning, Message = "Step '{StepName}' cancelled before starting.")]
	private partial void LogStepCancelledBeforeStart(string stepName);

	[LoggerMessage(Level = LogLevel.Warning, Message = "Skipping step '{StepName}': {Reason}")]
	private partial void LogSkippingStep(string stepName, string reason);

	[LoggerMessage(Level = LogLevel.Information, Message = "Skipping step '{StepName}': {Reason}")]
	private partial void LogSkippingStepBenign(string stepName, string reason);

	[LoggerMessage(Level = LogLevel.Information, Message = "Step '{StepName}' skipped because all dependency failures trace back to NoAction root(s): {Roots}")]
	private partial void LogStepSkippedDueToNoActionRoots(string stepName, string roots);

	[LoggerMessage(Level = LogLevel.Information, Message = "Running step '{StepName}'...")]
	private partial void LogRunningStep(string stepName);

	[LoggerMessage(Level = LogLevel.Information, Message = "Step '{StepName}' completed successfully.")]
	private partial void LogStepSucceeded(string stepName);

	[LoggerMessage(Level = LogLevel.Information, Message = "Step '{StepName}' completed with no action: {Reason}")]
	private partial void LogStepNoAction(string stepName, string reason);

	[LoggerMessage(Level = LogLevel.Error, Message = "Step '{StepName}' failed: {Error}")]
	private partial void LogStepFailed(string stepName, string? error);

	[LoggerMessage(Level = LogLevel.Warning, Message = "Loop target '{Target}' not found for checker '{Checker}', skipping loop.")]
	private partial void LogLoopTargetNotFound(string target, string checker);

	[LoggerMessage(Level = LogLevel.Information, Message = "[{Checker}] Loop exit condition met after {Iterations} iteration(s).")]
	private partial void LogLoopExitConditionMet(string checker, int iterations);

	[LoggerMessage(Level = LogLevel.Information, Message = "[{Checker}] Loop iteration {Iteration}/{MaxIterations} — re-running '{Target}' with feedback.")]
	private partial void LogLoopIteration(string checker, int iteration, int maxIterations, string target);

	[LoggerMessage(Level = LogLevel.Warning, Message = "[{Target}] Failed during loop iteration {Iteration}, stopping loop.")]
	private partial void LogLoopTargetFailed(string target, int iteration);

	[LoggerMessage(Level = LogLevel.Warning, Message = "[{Checker}] Failed during loop iteration {Iteration}, stopping loop.")]
	private partial void LogLoopCheckerFailed(string checker, int iteration);

	[LoggerMessage(Level = LogLevel.Warning, Message = "[{Checker}] Loop exhausted {MaxIterations} iterations without meeting exit condition. Using last result.")]
	private partial void LogLoopExhausted(string checker, int maxIterations);

	[LoggerMessage(Level = LogLevel.Information, Message = "Step '{StepName}' has a timeout of {TimeoutSeconds} seconds.")]
	private partial void LogStepTimeout(string stepName, int timeoutSeconds);

	[LoggerMessage(Level = LogLevel.Warning, Message = "Step '{StepName}' timed out after {TimeoutSeconds} seconds.")]
	private partial void LogStepTimedOut(string stepName, int timeoutSeconds);

	[LoggerMessage(Level = LogLevel.Information, Message = "Orchestration '{Name}' has a timeout of {TimeoutSeconds} seconds.")]
	private partial void LogOrchestrationTimeout(string name, int timeoutSeconds);

	[LoggerMessage(Level = LogLevel.Error, Message = "Orchestration '{Name}' timed out after {TimeoutSeconds} seconds.")]
	private partial void LogOrchestrationTimedOut(string name, int timeoutSeconds);

	[LoggerMessage(Level = LogLevel.Warning, Message = "Step '{StepName}' failed, retrying ({Attempt}/{MaxRetries}): {Error}. Waiting {DelaySeconds}s...")]
	private partial void LogStepRetry(string stepName, int attempt, int maxRetries, string error, double delaySeconds);

	[LoggerMessage(Level = LogLevel.Warning, Message = "Step '{StepName}' retry aborted: step timed out and retryOnTimeout is disabled (attempt {Attempt}).")]
	private partial void LogStepRetryAbortedTimeout(string stepName, int attempt);

	[LoggerMessage(Level = LogLevel.Error, Message = "Step '{StepName}' failed after exhausting all {MaxRetries} retry attempts.")]
	private partial void LogStepRetryExhausted(string stepName, int maxRetries);

	[LoggerMessage(Level = LogLevel.Error, Message = "Step '{StepName}' not retried — agent client is unhealthy. Error: {Error}")]
	private partial void LogStepRetrySkippedClientUnhealthy(string stepName, string error);

	[LoggerMessage(Level = LogLevel.Information, Message = "Checkpoint saved for orchestration '{Name}', run '{RunId}' after step '{StepName}' ({CompletedSteps}/{TotalSteps}).")]
	private partial void LogCheckpointSaved(string name, string runId, string stepName, int completedSteps, int totalSteps);

	[LoggerMessage(Level = LogLevel.Information, Message = "Initial checkpoint saved for orchestration '{Name}', run '{RunId}'.")]
	private partial void LogInitialCheckpointSaved(string name, string runId);

	[LoggerMessage(Level = LogLevel.Warning, Message = "Failed to save checkpoint for orchestration '{Name}', run '{RunId}' after step '{StepName}'.")]
	private partial void LogCheckpointSaveFailed(Exception ex, string name, string runId, string stepName);

	[LoggerMessage(Level = LogLevel.Warning, Message = "Failed to delete checkpoint for orchestration '{Name}', run '{RunId}'.")]
	private partial void LogCheckpointDeleteFailed(Exception ex, string name, string runId);

	[LoggerMessage(Level = LogLevel.Information, Message = "Checkpoint preserved for orchestration '{Name}', run '{RunId}' because the host is shutting down.")]
	private partial void LogCheckpointPreservedForResume(string name, string runId);

	[LoggerMessage(Level = LogLevel.Information, Message = "Resuming orchestration '{Name}' from checkpoint, run '{RunId}'.")]
	private partial void LogResumingOrchestration(string name, string runId);

	[LoggerMessage(Level = LogLevel.Information, Message = "Resuming orchestration '{Name}', run '{RunId}', restoring {CompletedSteps} completed step(s) from checkpoint.")]
	private partial void LogResumingFromCheckpoint(string name, string runId, int completedSteps);

	[LoggerMessage(Level = LogLevel.Warning, Message = "Child orchestration rehydration failed for restored step '{StepName}' (child executionId '{ChildExecutionId}'); downstream templates may not resolve {{stepName.steps.*}} bindings.")]
	private partial void LogChildRehydrationFailed(string stepName, string childExecutionId, Exception exception);

	[LoggerMessage(Level = LogLevel.Information, Message = "Child orchestration run.json missing for restored step '{StepName}' (child executionId '{ChildExecutionId}'); skipping ChildOrchestrationInfo rehydration. Downstream templates {{stepName.steps.*}} will not resolve in this retry.")]
	private partial void LogChildRehydrationMissing(string stepName, string childExecutionId);

	[LoggerMessage(Level = LogLevel.Information, Message = "Step '{StepName}' requested orchestration completion: {Reason}")]
	private partial void LogOrchestrationCompleteRequested(string stepName, string reason);

	[LoggerMessage(Level = LogLevel.Information, Message = "Step '{StepName}' is disabled (enabled: false), skipping with empty result.")]
	private partial void LogStepDisabled(string stepName);

	[LoggerMessage(Level = LogLevel.Warning, Message = "Step '{StepName}' has unresolved template expression: {Expression}")]
	private partial void LogUnresolvedTemplateExpression(string stepName, string expression);

	[LoggerMessage(Level = LogLevel.Debug, Message = "Step '{StepName}': scheduling launch on thread {ThreadId}")]
	private partial void LogStepLaunchScheduled(string stepName, int threadId);

	[LoggerMessage(Level = LogLevel.Debug, Message = "Step '{StepName}': task started on thread {ThreadId}")]
	private partial void LogStepTaskStarted(string stepName, int threadId);

	[LoggerMessage(Level = LogLevel.Debug, Message = "Run '{RunId}': about to create run scope on thread {ThreadId}")]
	private partial void LogRunScopeAboutToCreate(string runId, int threadId);

	[LoggerMessage(Level = LogLevel.Debug, Message = "Run '{RunId}': run scope ready on thread {ThreadId}")]
	private partial void LogRunScopeReady(string runId, int threadId);

	[LoggerMessage(Level = LogLevel.Debug, Message = "AsyncLocal diagnostic [{Where}]: runScopedClient={ClientHash} on thread {ThreadId}")]
	private partial void LogAsyncLocalDiagnostic(string where, string clientHash, int threadId);

	[LoggerMessage(Level = LogLevel.Warning, Message = "Awaiting-input hook for step '{StepName}' failed.")]
	private partial void LogAwaitingInputHookFailed(string stepName, Exception ex);

	#endregion
}
