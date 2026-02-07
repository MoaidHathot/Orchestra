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

	public async Task RunAsync(CancellationToken cancellationToken = default)
	{
		_logger.LogInformation("Starting orchestration '{Name}'...", _orchestration.Name);

		var schedule = _scheduler.Schedule(_orchestration);
		var context = new OrchestrationExecutionContext { Mcps = _mcps };
		var executor = new PromptExecutor(_agentBuilder);

		for (var i = 0; i < schedule.Entries.Length; i++)
		{
			var entry = schedule.Entries[i];
			var stepNames = string.Join(", ", entry.Steps.Select(s => s.Name));
			_logger.LogInformation("Executing layer {Layer}/{Total}: [{Steps}]", i + 1, schedule.Entries.Length, stepNames);

			if (entry.Steps.Length == 1)
			{
				var step = (PromptOrchestrationStep)entry.Steps[0];
				_logger.LogInformation("  Running step '{StepName}'...", step.Name);

				var result = await executor.ExecuteAsync(step, context, cancellationToken);
				context.AddResult(step.Name, result);

				_logger.LogInformation("  Step '{StepName}' completed.", step.Name);
			}
			else
			{
				// Run parallel steps concurrently
				var tasks = entry.Steps.Select(async s =>
				{
					var step = (PromptOrchestrationStep)s;
					_logger.LogInformation("  Running step '{StepName}'...", step.Name);

					var result = await executor.ExecuteAsync(step, context, cancellationToken);

					_logger.LogInformation("  Step '{StepName}' completed.", step.Name);
					return (step.Name, result);
				}).ToArray();

				var results = await Task.WhenAll(tasks);

				foreach (var (name, result) in results)
				{
					context.AddResult(name, result);
				}
			}
		}

		_logger.LogInformation("Orchestration '{Name}' completed successfully.", _orchestration.Name);

		// Log final outputs
		foreach (var step in _orchestration.Steps)
		{
			var result = context.GetResult(step.Name);
			_logger.LogInformation("=== Output from '{StepName}' ===\n{Content}\n", step.Name, result.Content);
		}
	}
}
