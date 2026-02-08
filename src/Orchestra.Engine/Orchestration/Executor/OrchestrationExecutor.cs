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
			}
			else
			{
				var tasks = entry.Steps.Select(async s =>
				{
					var step = (PromptOrchestrationStep)s;
					var result = await ExecuteOrSkipStepAsync(step, executor, context, stepResults, cancellationToken);
					return (step.Name, result);
				}).ToArray();

				var results = await Task.WhenAll(tasks);

				foreach (var (name, result) in results)
				{
					context.AddResult(name, result);
					stepResults[name] = result;
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
			return ExecutionResult.Skipped(reason);
		}

		_logger.LogInformation("  Running step '{StepName}'...", step.Name);
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
}
