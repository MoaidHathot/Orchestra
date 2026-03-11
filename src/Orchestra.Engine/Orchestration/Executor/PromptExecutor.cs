using Microsoft.Extensions.Logging;

namespace Orchestra.Engine;

public partial class PromptExecutor : Executor<PromptOrchestrationStep>
{
	private readonly AgentBuilder _agentBuilder;
	private readonly IOrchestrationReporter _reporter;
	private readonly IPromptFormatter _formatter;
	private readonly ILogger<PromptExecutor> _logger;

	public PromptExecutor(
		AgentBuilder agentBuilder,
		IOrchestrationReporter reporter,
		IPromptFormatter formatter,
		ILogger<PromptExecutor> logger)
	{
		_agentBuilder = agentBuilder;
		_reporter = reporter;
		_formatter = formatter;
		_logger = logger;
	}

	public override async Task<ExecutionResult> ExecuteAsync(
		PromptOrchestrationStep step,
		OrchestrationExecutionContext context,
		CancellationToken cancellationToken = default)
	{
		// Capture raw dependency outputs before building the prompt
		var rawDependencyOutputs = context.GetRawDependencyOutputs(step.DependsOn);

		// Get the raw user prompt before input handler processing
		var userPromptRaw = InjectParameters(step.UserPrompt, step.Parameters, context.Parameters);

		// Create event processor to handle agent events and collect trace data
		var eventProcessor = new AgentEventProcessor(_reporter, step.Name);

		try
		{
			// Build the user prompt, incorporating dependency outputs and parameters
			var userPrompt = BuildUserPrompt(step, context);

			// Log step MCPs for debugging
			LogStepMcps(step.Name, step.Mcps.Length, string.Join(", ", step.Mcps.Select(m => m.Name)));
			LogStepMcpNames(step.Name, string.Join(", ", step.McpNames));

			// Build and run the agent
			var agent = await _agentBuilder
				.WithModel(step.Model)
				.WithSystemPrompt(step.SystemPrompt)
				.WithMcp(step.Mcps)
				.WithSubagents(step.Subagents)
				.WithReasoningLevel(step.ReasoningLevel)
				.WithSystemPromptMode(step.SystemPromptMode ?? context.DefaultSystemPromptMode)
				.WithReporter(_reporter)
				.BuildAgentAsync(cancellationToken);

			var task = agent.SendAsync(userPrompt, cancellationToken);

			// Process all agent events, collecting trace data
			await eventProcessor.ProcessEventsAsync(task, cancellationToken);

			var result = await task.GetResultAsync();

			// Report model and usage metadata if available
			if (result.ActualModel is not null)
			{
				_reporter.ReportStepCompleted(step.Name, result);
			}
			if (result.Usage is not null && result.ActualModel is not null)
			{
				_reporter.ReportUsage(step.Name, result.ActualModel, result.Usage);
			}

			var content = result.Content;
			string? rawContent = null;
			string? outputHandlerResult = null;

			// Apply output handler if specified
			if (step.OutputHandlerPrompt is not null)
			{
				rawContent = content;
				content = await RunHandlerAsync(step.OutputHandlerPrompt, content, step.Model, cancellationToken);
				outputHandlerResult = content;
			}

			// Convert usage to our TokenUsage type
			TokenUsage? tokenUsage = null;
			if (result.Usage is not null)
			{
				tokenUsage = new TokenUsage
				{
					InputTokens = (int)(result.Usage.InputTokens ?? 0),
					OutputTokens = (int)(result.Usage.OutputTokens ?? 0)
				};
			}

			// Build the execution trace from collected data
			var trace = eventProcessor.BuildTrace(
				step.SystemPrompt,
				userPromptRaw,
				userPrompt,
				rawContent ?? result.Content,
				outputHandlerResult);

			// Report the step trace for live trace viewing
			_reporter.ReportStepTrace(step.Name, trace);

			return ExecutionResult.Succeeded(
				content,
				rawContent,
				rawDependencyOutputs,
				userPrompt,
				result.ActualModel,
				tokenUsage,
				trace);
		}
		catch (Exception ex)
		{
			// Build partial trace even on failure
			var trace = eventProcessor.BuildPartialTrace(step.SystemPrompt, userPromptRaw);

			// Report the partial trace for live trace viewing
			_reporter.ReportStepTrace(step.Name, trace);

			_reporter.ReportStepError(step.Name, ex.Message);
			return ExecutionResult.Failed(ex.Message, rawDependencyOutputs, trace: trace);
		}
	}

	private string BuildUserPrompt(PromptOrchestrationStep step, OrchestrationExecutionContext context)
	{
		var userPrompt = InjectParameters(step.UserPrompt, step.Parameters, context.Parameters);
		var dependencyOutputsDict = context.GetDependencyOutputs(step.DependsOn);
		var dependencyOutputs = _formatter.FormatDependencyOutputs(dependencyOutputsDict);
		var loopFeedback = context.ConsumeLoopFeedback(step.Name);

		return _formatter.BuildUserPrompt(userPrompt, dependencyOutputs, loopFeedback, step.InputHandlerPrompt);
	}

	private static string InjectParameters(string prompt, string[] parameterNames, Dictionary<string, string> parameters)
	{
		if (parameterNames.Length == 0 || parameters.Count == 0)
			return prompt;

		var result = prompt;
		foreach (var name in parameterNames)
		{
			if (parameters.TryGetValue(name, out var value))
			{
				result = result.Replace($"{{{{{name}}}}}", value);
			}
		}

		return result;
	}

	private async Task<string> RunHandlerAsync(
		string handlerPrompt,
		string content,
		string model,
		CancellationToken cancellationToken)
	{
		var systemPrompt = _formatter.BuildTransformationSystemPrompt(handlerPrompt);

		var agent = await _agentBuilder
			.WithModel(model)
			.WithSystemPrompt(systemPrompt)
			.WithSystemPromptMode(SystemPromptMode.Replace)
			.WithMcp()
			.WithReporter(_reporter)
			.BuildAgentAsync(cancellationToken);

		var wrappedContent = _formatter.WrapContentForTransformation(content);

		var task = agent.SendAsync(wrappedContent, cancellationToken);
		var result = await task.GetResultAsync();

		return result.Content;
	}

	#region Source-Generated Logging

	[LoggerMessage(
		EventId = 1,
		Level = LogLevel.Debug,
		Message = "Step '{StepName}' has {McpCount} MCPs: [{McpNames}]")]
	private partial void LogStepMcps(string stepName, int mcpCount, string mcpNames);

	[LoggerMessage(
		EventId = 2,
		Level = LogLevel.Debug,
		Message = "Step '{StepName}' McpNames configuration: [{McpNames}]")]
	private partial void LogStepMcpNames(string stepName, string mcpNames);

	#endregion
}
