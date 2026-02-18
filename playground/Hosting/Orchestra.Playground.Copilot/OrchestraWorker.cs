using Microsoft.Extensions.Logging;
using Orchestra.Engine;

namespace Orchestra.Playground.Copilot;

public class OrchestraWorker
{
	private readonly Orchestration _orchestration;
	private readonly OrchestrationExecutor _executor;
	private readonly IOrchestrationReporter _reporter;
	private readonly ILogger<OrchestraWorker> _logger;

	public OrchestraWorker(
		Orchestration orchestration,
		OrchestrationExecutor executor,
		IOrchestrationReporter reporter,
		ILogger<OrchestraWorker> logger)
	{
		_orchestration = orchestration;
		_executor = executor;
		_reporter = reporter;
		_logger = logger;
	}

	public async Task<OrchestrationResult> RunAsync(
		Dictionary<string, string>? parameters = null,
		bool printResult = false,
		CancellationToken cancellationToken = default)
	{
		var stopwatch = System.Diagnostics.Stopwatch.StartNew();
		var result = await _executor.ExecuteAsync(_orchestration, parameters, cancellationToken: cancellationToken);
		stopwatch.Stop();

		if (result.Status == ExecutionStatus.Succeeded)
		{
			var filename = parameters?.GetValueOrDefault("filename");

			foreach (var (stepName, stepResult) in result.Results)
			{
				// if (!string.IsNullOrWhiteSpace(filename))
				// {
				// 	await File.WriteAllTextAsync(filename, stepResult.Content, cancellationToken);
				// 	_logger.LogInformation("Result from '{StepName}' written to '{Filename}'.", stepName, filename);
				// }

				if (printResult)
				{
					_reporter.ReportStepOutput(stepName, stepResult.Content);
				}
			}
		}
		else
		{
			var failedSteps = result.Results
				.Where(kv => kv.Value.Status is not ExecutionStatus.Succeeded)
				.Select(kv => $"'{kv.Key}' ({kv.Value.Status})")
				.ToArray();

			_logger.LogError("Orchestration failed. Terminal steps with issues: {Steps}",
				string.Join(", ", failedSteps));
		}

		_logger.LogInformation("Orchestration completed in {Elapsed}.", stopwatch.Elapsed);

		return result;
	}
}
