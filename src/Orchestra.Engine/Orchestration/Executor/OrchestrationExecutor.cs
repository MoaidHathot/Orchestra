using Microsoft.Extensions.Logging;

namespace Orchestra.Engine;

public class OrchestrationExecutor
{
	private readonly IScheduler _scheduler;
	private readonly AgentBuilder _agentBuilder;
	private readonly IOrchestrationReporter _reporter;
	private readonly ILogger<OrchestrationExecutor> _logger;
	private readonly IRunStore _runStore;

	public OrchestrationExecutor(
		IScheduler scheduler,
		AgentBuilder agentBuilder,
		IOrchestrationReporter reporter,
		ILogger<OrchestrationExecutor> logger,
		IRunStore? runStore = null)
	{
		_scheduler = scheduler;
		_agentBuilder = agentBuilder;
		_reporter = reporter;
		_logger = logger;
		_runStore = runStore ?? NullRunStore.Instance;
	}

	public async Task<OrchestrationResult> ExecuteAsync(
		Orchestration orchestration,
		Dictionary<string, string>? parameters = null,
		string? triggerId = null,
		CancellationToken cancellationToken = default)
	{
		_logger.LogInformation("Starting orchestration '{Name}'...", orchestration.Name);

		ValidateParameters(orchestration, parameters);

		// Scheduler validates the DAG (detects cycles, missing deps)
		_ = _scheduler.Schedule(orchestration);

		var runId = Guid.NewGuid().ToString("N")[..12];
		var runStartedAt = DateTimeOffset.UtcNow;
		var effectiveParams = parameters ?? [];

		var context = new OrchestrationExecutionContext
		{
			Parameters = effectiveParams,
		};
		var executor = new PromptExecutor(_agentBuilder, _reporter);
		var stepResults = new Dictionary<string, ExecutionResult>();
		var stepRecords = new Dictionary<string, StepRunRecord>();
		var allStepRecords = new Dictionary<string, StepRunRecord>();
		var gate = new object();

		// Build step lookup and dependency graph
		var allSteps = orchestration.Steps
			.ToDictionary(s => s.Name, s => (PromptOrchestrationStep)s);

		// Track completion via TaskCompletionSource per step
		var completionSources = new Dictionary<string, TaskCompletionSource<ExecutionResult>>();
		foreach (var step in orchestration.Steps)
		{
			completionSources[step.Name] = new TaskCompletionSource<ExecutionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
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

		// Launch a step when all its dependencies are complete
		void TryLaunchStep(string stepName)
		{
			_ = Task.Run(async () =>
			{
				var step = allSteps[stepName];
				var stepStartedAt = DateTimeOffset.UtcNow;

				try
				{
					var result = await ExecuteOrSkipStepAsync(step, executor, context, stepResults, gate, cancellationToken);

					lock (gate)
					{
						context.AddResult(step.Name, result);
						stepResults[step.Name] = result;

						var record = BuildStepRecord(step, result, effectiveParams, stepStartedAt);
						stepRecords[step.Name] = record;
						allStepRecords[step.Name] = record;
					}

					// Handle loop if configured
					if (step.Loop is not null && result.Status == ExecutionStatus.Succeeded)
					{
						await HandleLoopAsync(step, allSteps, executor, context, stepResults, stepRecords, allStepRecords, effectiveParams, gate, cancellationToken);
					}

					completionSources[step.Name].TrySetResult(stepResults[step.Name]);
				}
				catch (OperationCanceledException)
				{
					var cancelled = ExecutionResult.Failed("Cancelled");
					lock (gate)
					{
						context.AddResult(step.Name, cancelled);
						stepResults[step.Name] = cancelled;
						var record = BuildStepRecord(step, cancelled, effectiveParams, stepStartedAt);
						stepRecords[step.Name] = record;
						allStepRecords[step.Name] = record;
					}
					_reporter.ReportStepCancelled(step.Name);
					completionSources[step.Name].TrySetResult(cancelled);
				}
				catch (Exception ex)
				{
					var failed = ExecutionResult.Failed(ex.Message);
					lock (gate)
					{
						context.AddResult(step.Name, failed);
						stepResults[step.Name] = failed;
						var record = BuildStepRecord(step, failed, effectiveParams, stepStartedAt);
						stepRecords[step.Name] = record;
						allStepRecords[step.Name] = record;
					}
					completionSources[step.Name].TrySetResult(failed);
				}

				// After this step completes, check all dependents — launch any that are now ready
				foreach (var dependent in dependents[stepName])
				{
					bool allDepsComplete;
					lock (gate)
					{
						allDepsComplete = allSteps[dependent].DependsOn
							.All(dep => stepResults.ContainsKey(dep));
					}

					if (allDepsComplete)
					{
						TryLaunchStep(dependent);
					}
				}
			}); // Don't pass cancellationToken to Task.Run - let step handle cancellation internally
		}

		// Start all steps that have zero dependencies
		foreach (var step in orchestration.Steps)
		{
			if (step.DependsOn.Length == 0)
			{
				_logger.LogInformation("Launching step '{StepName}' (no dependencies)", step.Name);
				TryLaunchStep(step.Name);
			}
		}

		// Wait for all steps to complete
		await Task.WhenAll(completionSources.Values.Select(tcs => tcs.Task));

		var orchestrationResult = OrchestrationResult.From(orchestration, stepResults);

		if (orchestrationResult.Status == ExecutionStatus.Succeeded)
		{
			_logger.LogInformation("Orchestration '{Name}' completed successfully.", orchestration.Name);
		}
		else
		{
			_logger.LogWarning("Orchestration '{Name}' completed with failures.", orchestration.Name);
		}

		// Build and persist the run record
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
			await _runStore.SaveRunAsync(runRecord, cancellationToken);
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Failed to save run record for orchestration '{Name}', run '{RunId}'.", orchestration.Name, runId);
		}

		return orchestrationResult;
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

	private async Task<ExecutionResult> ExecuteOrSkipStepAsync(
		PromptOrchestrationStep step,
		PromptExecutor executor,
		OrchestrationExecutionContext context,
		Dictionary<string, ExecutionResult> stepResults,
		object gate,
		CancellationToken cancellationToken)
	{
		// Check for cancellation before starting
		if (cancellationToken.IsCancellationRequested)
		{
			_logger.LogWarning("  Step '{StepName}' cancelled before starting.", step.Name);
			_reporter.ReportStepSkipped(step.Name, "Cancelled");
			return ExecutionResult.Failed("Cancelled");
		}

		// Check if any dependency failed or was skipped
		bool shouldSkip;
		string[] failedDeps;

		lock (gate)
		{
			shouldSkip = context.HasAnyDependencyFailed(step.DependsOn);
			failedDeps = shouldSkip
				? step.DependsOn
					.Where(dep => stepResults.TryGetValue(dep, out var r) &&
						r.Status is ExecutionStatus.Failed or ExecutionStatus.Skipped)
					.ToArray()
				: [];
		}

		if (shouldSkip)
		{
			var reason = $"Skipped because dependencies failed or were skipped: [{string.Join(", ", failedDeps)}]";
			_logger.LogWarning("  Skipping step '{StepName}': {Reason}", step.Name, reason);
			_reporter.ReportStepSkipped(step.Name, reason);
			return ExecutionResult.Skipped(reason);
		}

		_logger.LogInformation("  Running step '{StepName}'...", step.Name);
		_reporter.ReportStepStarted(step.Name);
		var result = await executor.ExecuteAsync(step, context, cancellationToken);

		if (result.Status == ExecutionStatus.Succeeded)
		{
			_logger.LogInformation("  Step '{StepName}' completed successfully.", step.Name);
		}
		else
		{
			_logger.LogError("  Step '{StepName}' failed: {Error}", step.Name, result.ErrorMessage);
		}

		return result;
	}

	private async Task HandleLoopAsync(
		PromptOrchestrationStep checkerStep,
		Dictionary<string, PromptOrchestrationStep> allStepsByName,
		PromptExecutor executor,
		OrchestrationExecutionContext context,
		Dictionary<string, ExecutionResult> stepResults,
		Dictionary<string, StepRunRecord> stepRecords,
		Dictionary<string, StepRunRecord> allStepRecords,
		Dictionary<string, string> effectiveParams,
		object gate,
		CancellationToken cancellationToken)
	{
		var loop = checkerStep.Loop!;

		if (!allStepsByName.TryGetValue(loop.Target, out var targetStep))
		{
			_logger.LogWarning("Loop target '{Target}' not found for checker '{Checker}', skipping loop.",
				loop.Target, checkerStep.Name);
			return;
		}

		for (var iteration = 1; iteration <= loop.MaxIterations; iteration++)
		{
			// Check exit condition on the checker's current result
			ExecutionResult checkerResult;
			lock (gate)
			{
				checkerResult = context.GetResult(checkerStep.Name);
			}

			if (checkerResult.Content.Contains(loop.ExitPattern, StringComparison.OrdinalIgnoreCase))
			{
				_logger.LogInformation("  [{Checker}] Loop exit condition met after {Iterations} iteration(s).",
					checkerStep.Name, iteration - 1);
				return;
			}

			_logger.LogInformation("  [{Checker}] Loop iteration {Iteration}/{Max} — re-running '{Target}' with feedback.",
				checkerStep.Name, iteration, loop.MaxIterations, loop.Target);
			_reporter.ReportLoopIteration(checkerStep.Name, loop.Target, iteration, loop.MaxIterations);

			// Inject checker's feedback into the target step's context
			lock (gate)
			{
				context.SetLoopFeedback(loop.Target, checkerResult.Content);
				context.ClearResult(loop.Target);
			}

			// Re-execute the target step
			var targetStartedAt = DateTimeOffset.UtcNow;
			_reporter.ReportStepStarted(loop.Target);
			var targetResult = await executor.ExecuteAsync(targetStep, context, cancellationToken);

			lock (gate)
			{
				context.AddResult(loop.Target, targetResult);
				stepResults[loop.Target] = targetResult;

				var targetRecord = BuildStepRecord(targetStep, targetResult, effectiveParams, targetStartedAt, iteration);
				stepRecords[loop.Target] = targetRecord;
				allStepRecords[$"{loop.Target}:iteration-{iteration}"] = targetRecord;
			}

			if (targetResult.Status != ExecutionStatus.Succeeded)
			{
				_logger.LogWarning("  [{Target}] Failed during loop iteration {Iteration}, stopping loop.",
					loop.Target, iteration);
				return;
			}

			// Re-execute the checker step
			lock (gate)
			{
				context.ClearResult(checkerStep.Name);
			}

			var checkerStartedAt = DateTimeOffset.UtcNow;
			_reporter.ReportStepStarted(checkerStep.Name);
			var newCheckerResult = await executor.ExecuteAsync(checkerStep, context, cancellationToken);

			lock (gate)
			{
				context.AddResult(checkerStep.Name, newCheckerResult);
				stepResults[checkerStep.Name] = newCheckerResult;

				var checkerRecord = BuildStepRecord(checkerStep, newCheckerResult, effectiveParams, checkerStartedAt, iteration);
				stepRecords[checkerStep.Name] = checkerRecord;
				allStepRecords[$"{checkerStep.Name}:iteration-{iteration}"] = checkerRecord;
			}

			if (newCheckerResult.Status != ExecutionStatus.Succeeded)
			{
				_logger.LogWarning("  [{Checker}] Failed during loop iteration {Iteration}, stopping loop.",
					checkerStep.Name, iteration);
				return;
			}
		}

		// Check exit condition one final time after exhausting all iterations
		ExecutionResult finalResult;
		lock (gate)
		{
			finalResult = context.GetResult(checkerStep.Name);
		}

		if (finalResult.Content.Contains(loop.ExitPattern, StringComparison.OrdinalIgnoreCase))
		{
			_logger.LogInformation("  [{Checker}] Loop exit condition met after {Max} iteration(s).",
				checkerStep.Name, loop.MaxIterations);
		}
		else
		{
			_logger.LogWarning("  [{Checker}] Loop exhausted {Max} iterations without meeting exit condition. Using last result.",
				checkerStep.Name, loop.MaxIterations);
		}
	}

	private static StepRunRecord BuildStepRecord(
		PromptOrchestrationStep step,
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
}
