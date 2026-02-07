using Microsoft.Extensions.Logging;
using Orchestra.Engine;

namespace Orchestra.Playground.Copilot;

public class OrchestraWorker
{
	private readonly Orchestration _orchestration;
	private readonly Mcp[] _mcps;
	private readonly IScheduler _scheduler;
	private readonly AgentBuilder _agentBuilder;
	private readonly ILogger<OrchestraWorker> _logger;

	public OrchestraWorker(
		Orchestration orchestration,
		Mcp[] mcps,
		IScheduler scheduler,
		AgentBuilder agentBuilder,
		ILogger<OrchestraWorker> logger)
	{
		_orchestration = orchestration;
		_mcps = mcps;
		_scheduler = scheduler;
		_agentBuilder = agentBuilder;
		_logger = logger;
	}

	public async Task<OrchestrationResult> RunAsync(
		Dictionary<string, string>? parameters = null,
		bool printResult = false,
		CancellationToken cancellationToken = default)
	{
		_logger.LogInformation("Starting orchestration '{Name}'...", _orchestration.Name);

		var schedule = _scheduler.Schedule(_orchestration);

		// Validate that all required parameters are provided
		ValidateParameters(parameters);

		var context = new OrchestrationExecutionContext
		{
			Mcps = _mcps,
			Parameters = parameters ?? [],
		};
		var executor = new PromptExecutor(_agentBuilder);
		var stepResults = new Dictionary<string, ExecutionResult>();

		// Build a reverse dependency map: step name → all steps that transitively depend on it
		var allSteps = _orchestration.Steps.ToDictionary(s => s.Name);

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

		var orchestrationResult = OrchestrationResult.From(stepResults);

		if (orchestrationResult.Status == ExecutionStatus.Succeeded)
		{
			_logger.LogInformation("Orchestration '{Name}' completed successfully.", _orchestration.Name);
		}
		else
		{
			_logger.LogWarning("Orchestration '{Name}' completed with failures.", _orchestration.Name);
		}

		// Output the final step's result
		var lastStep = _orchestration.Steps[^1];
		var lastResult = context.GetResult(lastStep.Name);

		if (lastResult.Status == ExecutionStatus.Succeeded)
		{
			var filename = parameters?.GetValueOrDefault("filename");
			if (!string.IsNullOrWhiteSpace(filename))
			{
				if (!filename.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
					filename += ".md";

				await File.WriteAllTextAsync(filename, lastResult.Content, cancellationToken);
				_logger.LogInformation("Final result written to '{Filename}'.", filename);
			}

			if (printResult)
			{
				Console.WriteLine();
				Console.WriteLine(lastResult.Content);
			}
		}
		else
		{
			_logger.LogError("Final step '{StepName}' did not succeed (status: {Status}).", lastStep.Name, lastResult.Status);
		}

		return orchestrationResult;
	}

	private void ValidateParameters(Dictionary<string, string>? parameters)
	{
		var requiredByStep = _orchestration.Steps
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
