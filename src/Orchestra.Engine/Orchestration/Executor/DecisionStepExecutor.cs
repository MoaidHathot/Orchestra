using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Orchestra.Engine;

public sealed partial class DecisionStepExecutor(IAgentProviderRegistry providers, IOrchestrationReporter reporter,
	ILogger<DecisionStepExecutor> logger) : IStepExecutor
{
	private static readonly JsonSerializerOptions s_outputOptions = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
	};

	public OrchestrationStepType StepType => OrchestrationStepType.Decision;

	public async Task<ExecutionResult> ExecuteAsync(OrchestrationStep step, OrchestrationExecutionContext context,
		CancellationToken cancellationToken = default)
	{
		var decision = (DecisionOrchestrationStep)step;
		var dependencies = context.GetRawDependencyOutputs(step.DependsOn);
		string Resolve(string value) => TemplateResolver.Resolve(value, context.Parameters, context, step.DependsOn, step);
		var model = decision.Model;
		try
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (providers.Resolve(DecisionOrchestrationStep.ProviderName) is not IDecisionProvider provider)
				throw new NotSupportedException("Decision steps require a Copilot provider with the Decisions API.");
			model = Resolve(model);
			var evidence = Resolve(decision.Context);
			ArgumentException.ThrowIfNullOrWhiteSpace(evidence);
			ArgumentException.ThrowIfNullOrWhiteSpace(model);
			var input = decision.ContextFormat == DecisionContextFormat.Json
				? ParseJsonEvidence(evidence)
				: JsonSerializer.SerializeToElement(evidence);

			var request = new DecisionRequest(model, input, decision.Questions, step.TimeoutSeconds ?? context.DefaultStepTimeoutSeconds);
			var result = await provider.AskAsync(request, cancellationToken).ConfigureAwait(false);
			var actualModel = result.UpstreamModel ?? result.Model;
			var output = JsonSerializer.Serialize(result, s_outputOptions);
			var trace = new StepExecutionTrace
			{
				ConfiguredProvider = DecisionOrchestrationStep.ProviderName,
				ActualProvider = DecisionOrchestrationStep.ProviderName,
				UserPromptRaw = decision.Context,
				UserPromptProcessed = evidence,
				FinalResponse = output,
			};
			if (result.Usage is { } usage)
			{
				reporter.ReportUsage(step.Name, actualModel, new AgentUsage
				{
					InputTokens = usage.InputTokens, OutputTokens = usage.OutputTokens,
					CacheReadTokens = usage.CacheReadTokens, CacheWriteTokens = usage.CacheWriteTokens,
					Duration = usage.Duration,
				});
			}
			LogDecision(step.Name, model, actualModel, result.Answers.Count, result.LatencyMs);
			return ExecutionResult.Succeeded(output, rawDependencyOutputs: dependencies, promptSent: evidence,
				selectedModel: result.Model, actualModel: actualModel, usage: result.Usage, trace: trace);
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var category = ex switch
			{
				TimeoutException => StepErrorCategory.Timeout,
				ArgumentException or JsonException or NotSupportedException => StepErrorCategory.ValidationError,
				_ => StepErrorCategory.ModelError,
			};
			var message = $"Decision step '{step.Name}' ({model}): {ex.Message}";
			reporter.ReportStepError(step.Name, message);
			return ExecutionResult.Failed(message, dependencies, errorCategory: category);
		}
	}

	private static JsonElement ParseJsonEvidence(string evidence)
	{
		var json = JsonSerializer.Deserialize<JsonElement>(evidence);
		if (json.ValueKind == JsonValueKind.Object ||
			json.ValueKind == JsonValueKind.Array && json.EnumerateArray().All(item => item.ValueKind == JsonValueKind.Object))
			return json;
		throw new ArgumentException("JSON decision context must be an object or an array of objects.");
	}

	[LoggerMessage(EventId = 1, Level = LogLevel.Debug,
		Message = "Decision step '{StepName}' requested {Model}, answered by {ActualModel}: {QuestionCount} questions in {LatencyMs} ms")]
	private partial void LogDecision(string stepName, string model, string actualModel, int questionCount, long latencyMs);
}
