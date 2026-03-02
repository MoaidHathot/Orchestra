using Microsoft.Extensions.Logging;

namespace Orchestra.Engine;

public partial class PromptExecutor : Executor<PromptOrchestrationStep>
{
	private readonly AgentBuilder _agentBuilder;
	private readonly IOrchestrationReporter _reporter;
	private readonly ILogger<PromptExecutor> _logger;

	public PromptExecutor(AgentBuilder agentBuilder, IOrchestrationReporter reporter, ILogger<PromptExecutor> logger)
	{
		_agentBuilder = agentBuilder;
		_reporter = reporter;
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
				.WithReasoningLevel(step.ReasoningLevel)
				.WithSystemPromptMode(step.SystemPromptMode)
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

	private static string BuildUserPrompt(PromptOrchestrationStep step, OrchestrationExecutionContext context)
	{
		var userPrompt = InjectParameters(step.UserPrompt, step.Parameters, context.Parameters);
		var dependencyOutputs = context.GetDependencyOutputs(step.DependsOn);
		var loopFeedback = context.ConsumeLoopFeedback(step.Name);

		if (string.IsNullOrEmpty(dependencyOutputs) && loopFeedback is null)
			return userPrompt;

		if (step.InputHandlerPrompt is not null)
		{
			var prompt = $"""
				{step.InputHandlerPrompt}

				---
				Previous step outputs:
				{dependencyOutputs}

				---
				Task:
				{userPrompt}
				""";

			if (loopFeedback is not null)
			{
				prompt += $"""


					---
					Feedback from previous attempt (use this to improve your output):
					{loopFeedback}
					""";
			}

			return prompt;
		}

		var result = $"""
			{userPrompt}

			---
			Context from previous steps:
			{dependencyOutputs}
			""";

		if (loopFeedback is not null)
		{
			result += $"""


				---
				Feedback from previous attempt (use this to improve your output):
				{loopFeedback}
				""";
		}

		return result;
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
		var systemPrompt = $"""
			You are a stateless content transformation function.

			CRITICAL RULES:
			1. You receive INPUT CONTENT and TRANSFORMATION INSTRUCTIONS
			2. You output ONLY the transformed content - nothing else
			3. Do NOT engage in conversation, ask questions, or add commentary
			4. Do NOT reference any external context, projects, or repositories
			5. Do NOT add greetings, offers to help, or clarifying questions
			6. Simply apply the transformation and output the result

			TRANSFORMATION INSTRUCTIONS:
			{handlerPrompt}

			OUTPUT FORMAT:
			Return ONLY the transformed content. No preamble. No commentary. No follow-up questions.
			""";

		var agent = await _agentBuilder
			.WithModel(model)
			.WithSystemPrompt(systemPrompt)
			.WithSystemPromptMode(SystemPromptMode.Replace)
			.WithMcp()
			.WithReporter(_reporter)
			.BuildAgentAsync(cancellationToken);

		// Wrap content in clear delimiters to prevent model from treating it as a conversation
		var wrappedContent = $"""
			<INPUT_CONTENT>
			{content}
			</INPUT_CONTENT>

			Transform the content above according to your instructions. Output ONLY the transformed content.
			""";

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
