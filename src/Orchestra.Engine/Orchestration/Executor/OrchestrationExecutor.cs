using Microsoft.Extensions.Logging;

namespace Orchestra.Engine;

public class OrchestrationExecutor
{
	private readonly IScheduler _scheduler;
	private readonly AgentBuilder _agentBuilder;
	private readonly IOrchestrationReporter _reporter;
	private readonly ILogger<OrchestrationExecutor> _logger;

	public OrchestrationExecutor(
		IScheduler scheduler,
		AgentBuilder agentBuilder,
		IOrchestrationReporter reporter,
		ILogger<OrchestrationExecutor> logger)
	{
		_scheduler = scheduler;
		_agentBuilder = agentBuilder;
		_reporter = reporter;
		_logger = logger;
	}

	public async Task<OrchestrationResult> ExecuteAsync(
		Orchestration orchestration,
		Dictionary<string, string>? parameters = null,
		CancellationToken cancellationToken = default)
	{
		_logger.LogInformation("Starting orchestration '{Name}'...", orchestration.Name);

		ValidateParameters(orchestration, parameters);

		var schedule = _scheduler.Schedule(orchestration);

		var context = new OrchestrationExecutionContext
		{
			Parameters = parameters ?? [],
		};
		var executor = new PromptExecutor(_agentBuilder, _reporter);
		var stepResults = new Dictionary<string, ExecutionResult>();

		// Build a lookup of all steps for loop target resolution
		var allStepsByName = orchestration.Steps
			.ToDictionary(s => s.Name, s => (PromptOrchestrationStep)s);

		for (var i = 0; i < schedule.Entries.Length; i++)
		{
			var entry = schedule.Entries[i];
			var stepNames = string.Join(", ", entry.Steps.Select(s => s.Name));
			_logger.LogInformation("Executing layer {Layer}/{Total}: [{Steps}]", i + 1, schedule.Entries.Length, stepNames);

			if (entry.Steps.Length == 1)
			{
				var step = (PromptOrchestrationStep)entry.Steps[0];
				var result = await ExecuteOrSkipStepAsync(step, executor, context, stepResults, cancellationToken);
				context.AddResult(step.Name, result);
				stepResults[step.Name] = result;

				// Handle loop if configured
				if (step.Loop is not null && result.Status == ExecutionStatus.Succeeded)
				{
					await HandleLoopAsync(step, allStepsByName, executor, context, stepResults, cancellationToken);
				}
			}
			else
			{
				var tasks = entry.Steps.Select(async s =>
				{
					var step = (PromptOrchestrationStep)s;
					var result = await ExecuteOrSkipStepAsync(step, executor, context, stepResults, cancellationToken);
					return (step, result);
				}).ToArray();

				var results = await Task.WhenAll(tasks);

				foreach (var (step, result) in results)
				{
					context.AddResult(step.Name, result);
					stepResults[step.Name] = result;
				}

				// Handle loops for any steps in this layer that have loop configs
				foreach (var (step, result) in results)
				{
					if (step.Loop is not null && result.Status == ExecutionStatus.Succeeded)
					{
						await HandleLoopAsync(step, allStepsByName, executor, context, stepResults, cancellationToken);
					}
				}
			}
		}

		var orchestrationResult = OrchestrationResult.From(orchestration, stepResults);

		if (orchestrationResult.Status == ExecutionStatus.Succeeded)
		{
			_logger.LogInformation("Orchestration '{Name}' completed successfully.", orchestration.Name);
		}
		else
		{
			_logger.LogWarning("Orchestration '{Name}' completed with failures.", orchestration.Name);
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
		CancellationToken cancellationToken)
	{
		// Check if any dependency failed or was skipped
		if (context.HasAnyDependencyFailed(step.DependsOn))
		{
			var failedDeps = step.DependsOn
				.Where(dep => stepResults.TryGetValue(dep, out var r) &&
					r.Status is ExecutionStatus.Failed or ExecutionStatus.Skipped)
				.ToArray();

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
			var checkerResult = context.GetResult(checkerStep.Name);
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
			context.SetLoopFeedback(loop.Target, checkerResult.Content);

			// Re-execute the target step
			context.ClearResult(loop.Target);
			_reporter.ReportStepStarted(loop.Target);
			var targetResult = await executor.ExecuteAsync(targetStep, context, cancellationToken);
			context.AddResult(loop.Target, targetResult);
			stepResults[loop.Target] = targetResult;

			if (targetResult.Status != ExecutionStatus.Succeeded)
			{
				_logger.LogWarning("  [{Target}] Failed during loop iteration {Iteration}, stopping loop.",
					loop.Target, iteration);
				return;
			}

			// Re-execute the checker step
			context.ClearResult(checkerStep.Name);
			_reporter.ReportStepStarted(checkerStep.Name);
			var newCheckerResult = await executor.ExecuteAsync(checkerStep, context, cancellationToken);
			context.AddResult(checkerStep.Name, newCheckerResult);
			stepResults[checkerStep.Name] = newCheckerResult;

			if (newCheckerResult.Status != ExecutionStatus.Succeeded)
			{
				_logger.LogWarning("  [{Checker}] Failed during loop iteration {Iteration}, stopping loop.",
					checkerStep.Name, iteration);
				return;
			}
		}

		// Check exit condition one final time after exhausting all iterations
		var finalResult = context.GetResult(checkerStep.Name);
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
}
