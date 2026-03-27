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
	private readonly StepExecutorRegistry _stepExecutorRegistry;

	public OrchestrationExecutor(
		IScheduler scheduler,
		AgentBuilder agentBuilder,
		IOrchestrationReporter reporter,
		ILoggerFactory loggerFactory,
		IPromptFormatter? promptFormatter = null,
		IRunStore? runStore = null,
		ICheckpointStore? checkpointStore = null,
		StepExecutorRegistry? stepExecutorRegistry = null)
	{
		_scheduler = scheduler;
		_agentBuilder = agentBuilder;
		_reporter = reporter;
		_promptFormatter = promptFormatter ?? DefaultPromptFormatter.Instance;
		_loggerFactory = loggerFactory;
		_logger = loggerFactory.CreateLogger<OrchestrationExecutor>();
		_runStore = runStore ?? NullRunStore.Instance;
		_checkpointStore = checkpointStore ?? NullCheckpointStore.Instance;

		// If no registry is provided, create a default one with all built-in step types
		if (stepExecutorRegistry is not null)
		{
			_stepExecutorRegistry = stepExecutorRegistry;
		}
		else
		{
			var promptExecutor = new PromptExecutor(agentBuilder, reporter, _promptFormatter, loggerFactory.CreateLogger<PromptExecutor>());
			_stepExecutorRegistry = new StepExecutorRegistry()
				.Register(new PromptStepExecutor(promptExecutor))
				.Register(new HttpStepExecutor(new System.Net.Http.HttpClient(), reporter, loggerFactory.CreateLogger<HttpStepExecutor>()))
				.Register(new TransformStepExecutor(loggerFactory.CreateLogger<TransformStepExecutor>()))
				.Register(new CommandStepExecutor(reporter, loggerFactory.CreateLogger<CommandStepExecutor>()));
		}
	}

	public async Task<OrchestrationResult> ExecuteAsync(
		Orchestration orchestration,
		Dictionary<string, string>? parameters = null,
		string? triggerId = null,
		CancellationToken cancellationToken = default)
	{
		LogStartingOrchestration(orchestration.Name);

		ValidateParameters(orchestration, parameters);

		// Scheduler validates the DAG (detects cycles, missing deps)
		_ = _scheduler.Schedule(orchestration);

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
			return await ExecuteCoreAsync(orchestration, parameters, triggerId, effectiveCancellationToken, cancellationToken);
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
		CheckpointData? checkpoint = null)
	{
		var runId = checkpoint?.RunId ?? Guid.NewGuid().ToString("N")[..12];
		var runStartedAt = checkpoint?.StartedAt ?? DateTimeOffset.UtcNow;
		var effectiveParams = parameters ?? [];

		var context = new OrchestrationExecutionContext
		{
			Parameters = effectiveParams,
			DefaultSystemPromptMode = orchestration.DefaultSystemPromptMode,
			DefaultRetryPolicy = orchestration.DefaultRetryPolicy,
		};
		var stepResults = new ConcurrentDictionary<string, ExecutionResult>();
		var stepRecords = new ConcurrentDictionary<string, StepRunRecord>();
		var allStepRecords = new ConcurrentDictionary<string, StepRunRecord>();

		// Build step lookup and dependency graph
		var allSteps = orchestration.Steps
			.ToDictionary(s => s.Name, s => s);

		// Restore completed steps from checkpoint if resuming
		if (checkpoint is not null)
		{
			LogResumingFromCheckpoint(orchestration.Name, runId, checkpoint.CompletedSteps.Count);

			foreach (var (stepName, stepCheckpoint) in checkpoint.CompletedSteps)
			{
				var result = stepCheckpoint.ToExecutionResult();
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
					};
					stepRecords[stepName] = record;
					allStepRecords[stepName] = record;
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

		// Launch a step when all its dependencies are complete
		void TryLaunchStep(string stepName)
		{
			// Skip steps that were already completed from checkpoint
			if (stepResults.ContainsKey(stepName))
				return;

			_ = Task.Run(async () =>
			{
				var step = allSteps[stepName];
				var stepExecutor = _stepExecutorRegistry.Resolve(step.Type);
				var stepStartedAt = DateTimeOffset.UtcNow;

				try
				{
					var result = await ExecuteWithRetryAsync(step, stepExecutor, context, stepResults, cancellationToken);

					context.AddResult(step.Name, result);
					stepResults[step.Name] = result;

					var record = BuildStepRecord(step, result, effectiveParams, stepStartedAt);
					stepRecords[step.Name] = record;
					allStepRecords[step.Name] = record;

					// Handle loop if configured (loop is a Prompt-only feature)
					if (step is PromptOrchestrationStep promptStep && promptStep.Loop is not null && result.Status == ExecutionStatus.Succeeded)
					{
						await HandleLoopAsync(promptStep, allSteps, context, stepResults, stepRecords, allStepRecords, effectiveParams, cancellationToken);
					}

					// Save checkpoint before signaling completion so the checkpoint
					// is durable before dependents or WhenAll observers proceed.
					if (result.Status == ExecutionStatus.Succeeded)
					{
						await SaveCheckpointAfterStepAsync(runId, orchestration, runStartedAt, effectiveParams, triggerId, stepResults, step.Name, totalSteps, externalCancellationToken);
					}

					completionSources[step.Name].TrySetResult(stepResults[step.Name]);
				}
				catch (OperationCanceledException)
				{
					var cancelled = ExecutionResult.Failed("Cancelled");
					context.AddResult(step.Name, cancelled);
					stepResults[step.Name] = cancelled;
					var record = BuildStepRecord(step, cancelled, effectiveParams, stepStartedAt);
					stepRecords[step.Name] = record;
					allStepRecords[step.Name] = record;
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
					completionSources[step.Name].TrySetResult(failed);
				}

				// After this step completes, check all dependents — launch any that are now ready
				foreach (var dependent in dependents[stepName])
				{
					var allDepsComplete = allSteps[dependent].DependsOn
						.All(dep => stepResults.ContainsKey(dep));

					if (allDepsComplete)
					{
						TryLaunchStep(dependent);
					}
				}
			}); // Don't pass cancellationToken to Task.Run - let step handle cancellation internally
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

		var orchestrationResult = OrchestrationResult.From(orchestration, stepResults);

		if (orchestrationResult.Status == ExecutionStatus.Succeeded)
		{
			LogOrchestrationSucceeded(orchestration.Name);
		}
		else
		{
			LogOrchestrationFailed(orchestration.Name);
		}

		// Build and persist the run record (use external token so save isn't blocked by orchestration timeout)
		var runCompletedAt = DateTimeOffset.UtcNow;
		var finalContent = BuildFinalContent(orchestrationResult);

		var runRecord = new OrchestrationRunRecord
		{
			RunId = runId,
			OrchestrationName = orchestration.Name,
			StartedAt = runStartedAt,
			CompletedAt = runCompletedAt,
			Status = orchestrationResult.Status,
			Parameters = effectiveParams,
			TriggerId = triggerId,
			StepRecords = stepRecords,
			AllStepRecords = allStepRecords,
			FinalContent = finalContent,
		};

		try
		{
			await _runStore.SaveRunAsync(runRecord, externalCancellationToken);
		}
		catch (Exception ex)
		{
			LogRunStoreSaveFailed(ex, orchestration.Name, runId);
		}

		// Clean up checkpoint now that execution is complete
		try
		{
			await _checkpointStore.DeleteCheckpointAsync(orchestration.Name, runId, externalCancellationToken);
		}
		catch (Exception ex)
		{
			LogCheckpointDeleteFailed(ex, orchestration.Name, runId);
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
		CancellationToken cancellationToken = default)
	{
		LogResumingOrchestration(orchestration.Name, checkpoint.RunId);

		ValidateParameters(orchestration, checkpoint.Parameters.Count > 0 ? checkpoint.Parameters : null);

		// Scheduler validates the DAG (detects cycles, missing deps)
		_ = _scheduler.Schedule(orchestration);

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
				checkpoint);
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

	private static void ValidateParameters(Orchestration orchestration, Dictionary<string, string>? parameters)
	{
		var requiredByStep = orchestration.Steps
			.SelectMany(step => step.Parameters.Select(param => (param, step.Name)))
			.GroupBy(x => x.param)
			.ToDictionary(g => g.Key, g => g.Select(x => x.Name).ToArray());

		if (requiredByStep.Count == 0)
			return;

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

		// Check if the failure was a timeout and retryOnTimeout is disabled
		var isTimeout = result.ErrorMessage?.Contains("timed out", StringComparison.OrdinalIgnoreCase) == true;
		if (isTimeout && !retryPolicy.RetryOnTimeout)
			return result;

		// Retry loop
		for (var attempt = 1; attempt <= retryPolicy.MaxRetries; attempt++)
		{
			cancellationToken.ThrowIfCancellationRequested();

			var delay = retryPolicy.GetDelay(attempt);
			LogStepRetry(step.Name, attempt, retryPolicy.MaxRetries, result.ErrorMessage ?? "Unknown error", delay.TotalSeconds);
			_reporter.ReportStepRetry(step.Name, attempt, retryPolicy.MaxRetries, result.ErrorMessage ?? "Unknown error", delay);

			await Task.Delay(delay, cancellationToken);

			result = await ExecuteOrSkipStepAsync(step, executor, context, stepResults, cancellationToken);

			if (result.Status != ExecutionStatus.Failed)
				return result;

			// Check timeout condition for subsequent retries
			isTimeout = result.ErrorMessage?.Contains("timed out", StringComparison.OrdinalIgnoreCase) == true;
			if (isTimeout && !retryPolicy.RetryOnTimeout)
			{
				LogStepRetryAbortedTimeout(step.Name, attempt);
				return result;
			}
		}

		LogStepRetryExhausted(step.Name, retryPolicy.MaxRetries);
		return result;
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
			_reporter.ReportStepSkipped(step.Name, "Cancelled");
			return ExecutionResult.Failed("Cancelled");
		}

		// Check if any dependency failed or was skipped
		var shouldSkip = context.HasAnyDependencyFailed(step.DependsOn);
		var failedDeps = shouldSkip
			? step.DependsOn
				.Where(dep => stepResults.TryGetValue(dep, out var r) &&
					r.Status is ExecutionStatus.Failed or ExecutionStatus.Skipped)
				.ToArray()
			: [];

		if (shouldSkip)
		{
			var reason = $"Skipped because dependencies failed or were skipped: [{string.Join(", ", failedDeps)}]";
			LogSkippingStep(step.Name, reason);
			_reporter.ReportStepSkipped(step.Name, reason);
			return ExecutionResult.Skipped(reason);
		}

		LogRunningStep(step.Name);
		_reporter.ReportStepStarted(step.Name);

		// Apply per-step timeout if configured
		CancellationTokenSource? timeoutCts = null;
		var effectiveToken = cancellationToken;

		if (step.TimeoutSeconds is > 0)
		{
			timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			timeoutCts.CancelAfter(TimeSpan.FromSeconds(step.TimeoutSeconds.Value));
			effectiveToken = timeoutCts.Token;
			LogStepTimeout(step.Name, step.TimeoutSeconds.Value);
		}

		try
		{
			var result = await executor.ExecuteAsync(step, context, effectiveToken);

			if (result.Status == ExecutionStatus.Succeeded)
			{
				LogStepSucceeded(step.Name);
			}
			else
			{
				LogStepFailed(step.Name, result.ErrorMessage);
			}

			return result;
		}
		catch (OperationCanceledException) when (timeoutCts is not null && timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
		{
			var message = $"Step timed out after {step.TimeoutSeconds} seconds.";
			LogStepTimedOut(step.Name, step.TimeoutSeconds!.Value);
			_reporter.ReportStepError(step.Name, message);
			return ExecutionResult.Failed(message);
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

			context.AddResult(loop.Target, targetResult);
			stepResults[loop.Target] = targetResult;

			var targetRecord = BuildStepRecord(targetStep, targetResult, effectiveParams, targetStartedAt, iteration);
			stepRecords[loop.Target] = targetRecord;
			allStepRecords[$"{loop.Target}:iteration-{iteration}"] = targetRecord;

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

			context.AddResult(checkerStep.Name, newCheckerResult);
			stepResults[checkerStep.Name] = newCheckerResult;

			var checkerRecord = BuildStepRecord(checkerStep, newCheckerResult, effectiveParams, checkerStartedAt, iteration);
			stepRecords[checkerStep.Name] = checkerRecord;
			allStepRecords[$"{checkerStep.Name}:iteration-{iteration}"] = checkerRecord;

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
			Usage = result.Usage,
			Trace = result.Trace,
		};
	}

	private static string BuildFinalContent(OrchestrationResult orchestrationResult)
	{
		if (orchestrationResult.Results.Count == 1)
		{
			return orchestrationResult.Results.Values.First().Content;
		}

		return string.Join("\n\n---\n\n",
			orchestrationResult.Results
				.Where(kv => kv.Value.Status == ExecutionStatus.Succeeded)
				.Select(kv => $"## {kv.Key}\n{kv.Value.Content}"));
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

	[LoggerMessage(Level = LogLevel.Error, Message = "Failed to save run record for orchestration '{Name}', run '{RunId}'.")]
	private partial void LogRunStoreSaveFailed(Exception ex, string name, string runId);

	[LoggerMessage(Level = LogLevel.Warning, Message = "Step '{StepName}' cancelled before starting.")]
	private partial void LogStepCancelledBeforeStart(string stepName);

	[LoggerMessage(Level = LogLevel.Warning, Message = "Skipping step '{StepName}': {Reason}")]
	private partial void LogSkippingStep(string stepName, string reason);

	[LoggerMessage(Level = LogLevel.Information, Message = "Running step '{StepName}'...")]
	private partial void LogRunningStep(string stepName);

	[LoggerMessage(Level = LogLevel.Information, Message = "Step '{StepName}' completed successfully.")]
	private partial void LogStepSucceeded(string stepName);

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

	[LoggerMessage(Level = LogLevel.Information, Message = "Checkpoint saved for orchestration '{Name}', run '{RunId}' after step '{StepName}' ({CompletedSteps}/{TotalSteps}).")]
	private partial void LogCheckpointSaved(string name, string runId, string stepName, int completedSteps, int totalSteps);

	[LoggerMessage(Level = LogLevel.Warning, Message = "Failed to save checkpoint for orchestration '{Name}', run '{RunId}' after step '{StepName}'.")]
	private partial void LogCheckpointSaveFailed(Exception ex, string name, string runId, string stepName);

	[LoggerMessage(Level = LogLevel.Warning, Message = "Failed to delete checkpoint for orchestration '{Name}', run '{RunId}'.")]
	private partial void LogCheckpointDeleteFailed(Exception ex, string name, string runId);

	[LoggerMessage(Level = LogLevel.Information, Message = "Resuming orchestration '{Name}' from checkpoint, run '{RunId}'.")]
	private partial void LogResumingOrchestration(string name, string runId);

	[LoggerMessage(Level = LogLevel.Information, Message = "Resuming orchestration '{Name}', run '{RunId}', restoring {CompletedSteps} completed step(s) from checkpoint.")]
	private partial void LogResumingFromCheckpoint(string name, string runId, int completedSteps);

	#endregion
}
